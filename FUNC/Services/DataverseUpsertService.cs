using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Polly;
using Polly.Retry;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class DataverseUpsertService : IDataverseUpsertService
    {
        private readonly IDataverseServiceClientFactory _serviceClientFactory;
        private readonly IEarlyboundEntityMapper _mapper;
        private readonly IEntityMappingCache _entityMappingCache;
        private readonly ILogger<DataverseUpsertService> _logger;
        private readonly DataverseOptions _options;
        private readonly AsyncRetryPolicy _retryPolicy;
        private readonly TokenBucketRateLimiter _rateLimiter;

        public DataverseUpsertService(
            IDataverseServiceClientFactory serviceClientFactory,
            IEarlyboundEntityMapper mapper,
            IEntityMappingCache entityMappingCache,
            ILogger<DataverseUpsertService> logger,
            IOptions<DataverseOptions> options)
        {
            _serviceClientFactory = serviceClientFactory ?? throw new ArgumentNullException(nameof(serviceClientFactory));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _entityMappingCache = entityMappingCache ?? throw new ArgumentNullException(nameof(entityMappingCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _retryPolicy = Policy
                .Handle<TimeoutException>()
                .Or<AggregateException>(ex => ex.InnerException is TimeoutException)
                .WaitAndRetryAsync(
                    _options.MaxRetries,
                    retryAttempt => TimeSpan.FromMilliseconds((Math.Pow(2, retryAttempt) * 200) + Random.Shared.Next(25, 150)),
                    OnRetry);

            _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = _options.MaxRequestsPerSecond,
                TokensPerPeriod = _options.MaxRequestsPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 1024,
                AutoReplenishment = true
            });
        }

        public async Task<UpsertResult> UpsertAsync(UpsertPayload payload, CancellationToken cancellationToken = default)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning("Rate limiter denied request for entity {EntityLogicalName}", payload.EntityLogicalName);

                return new UpsertResult
                {
                    EntityLogicalName = payload.EntityLogicalName,
                    Id = payload.Id ?? Guid.Empty,
                    Created = false,
                    ErrorMessage = "Rate limit exceeded."
                };
            }

            return await UpsertSingleAsync(payload, cancellationToken);
        }

        public async Task<IReadOnlyList<UpsertResult>> UpsertBatchAsync(
            IEnumerable<UpsertPayload> payloads,
            CancellationToken cancellationToken = default)
        {
            var requestList = payloads?.ToList() ?? throw new ArgumentNullException(nameof(payloads));

            if (requestList.Count == 0)
            {
                return Array.Empty<UpsertResult>();
            }

            await ThrottleForDataverseLimitAsync(cancellationToken);

            var results = new UpsertResult[requestList.Count];
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism
            };

            await Parallel.ForEachAsync(Enumerable.Range(0, requestList.Count), options, async (index, ct) =>
            {
                results[index] = await UpsertAsync(requestList[index], ct);
            });

            var failures = results.Count(r => r != null && !string.IsNullOrWhiteSpace(r.ErrorMessage));
            _logger.LogInformation("Batch upsert completed. Total={Total}, Failed={Failed}", results.Length, failures);

            return results;
        }

        private async Task<UpsertResult> UpsertSingleAsync(UpsertPayload payload, CancellationToken cancellationToken = default)
        {
            try
            {
                if (payload.Attributes == null)
                {
                    throw new ArgumentException("Attributes are required.", nameof(payload.Attributes));
                }

                _mapper.ValidatePayload(payload);

                var service = await _serviceClientFactory.GetOrCreateServiceAsync(cancellationToken);

                var entity = _mapper.MapToEntity(payload);

                if (payload.Lookups != null && payload.Lookups.Count > 0)
                {
                    foreach (var lookup in payload.Lookups)
                    {
                        var resolvedReference = await ResolveLookupAsync(
                            lookup.Key,
                            lookup.Value,
                            service,
                            cancellationToken);

                        entity[lookup.Key] = resolvedReference;

                        _logger.LogInformation(
                            "Resolved lookup for entity {EntityLogicalName}, attribute {AttributeName}: {LookupEntityLogicalName} {LookupId}",
                            payload.EntityLogicalName,
                            lookup.Key,
                            resolvedReference.LogicalName,
                            resolvedReference.Id);
                    }
                }

                var externalKey = payload.ExternalIdValue?.ToString()?.Trim();
                if (entity.Id == Guid.Empty &&
                    !string.IsNullOrWhiteSpace(payload.ExternalIdAttribute) &&
                    !string.IsNullOrWhiteSpace(externalKey))
                {
                    var cachedId = await _entityMappingCache.GetAsync(
                        payload.EntityLogicalName,
                        externalKey,
                        cancellationToken);

                    var existingId = cachedId ?? await TryFindExistingByExternalIdAsync(
                        payload,
                        service,
                        cancellationToken);

                    if (existingId.HasValue)
                    {
                        entity.Id = existingId.Value;

                        if (!cachedId.HasValue)
                        {
                            await _entityMappingCache.SetAsync(
                                payload.EntityLogicalName,
                                externalKey,
                                existingId.Value,
                                cancellationToken);
                        }
                    }
                }

                LogEntityAttributes(entity);

                var result = await _retryPolicy.ExecuteAsync(async (ctx, ct) =>
                {
                    Guid id;
                    bool created;

                    if (entity.Id != Guid.Empty)
                    {
                        await service.UpdateAsync(entity, ct);
                        id = entity.Id;
                        created = false;
                    }
                    else
                    {
                        id = await service.CreateAsync(entity, ct);
                        created = true;
                    }

                    if (!string.IsNullOrWhiteSpace(payload.ExternalIdAttribute) && payload.ExternalIdValue != null)
                    {
                        var key = payload.ExternalIdValue.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            await _entityMappingCache.SetAsync(
                                payload.EntityLogicalName,
                                key,
                                id,
                                ct);
                        }
                    }

                    return new UpsertResult
                    {
                        EntityLogicalName = payload.EntityLogicalName,
                        Id = id,
                        Created = created
                    };
                }, new Context(), cancellationToken);

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PayloadValidationException ex)
            {
                _logger.LogWarning(
                    "Validation failed for entity {EntityLogicalName}: {Message}",
                    payload.EntityLogicalName,
                    ex.Message);

                return new UpsertResult
                {
                    EntityLogicalName = payload.EntityLogicalName,
                    Id = payload.Id ?? Guid.Empty,
                    Created = false,
                    IsValidationError = true,
                    ValidationErrors = ex.ValidationErrors.ToList(),
                    ErrorMessage = ex.Message
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upsert failed for entity {EntityLogicalName}", payload.EntityLogicalName);

                return new UpsertResult
                {
                    EntityLogicalName = payload.EntityLogicalName,
                    Id = payload.Id ?? Guid.Empty,
                    Created = false,
                    IsValidationError = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<Guid?> TryFindExistingByExternalIdAsync(
            UpsertPayload payload,
            IOrganizationServiceAsync2 service,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(payload.ExternalIdAttribute) || payload.ExternalIdValue is null)
            {
                return null;
            }

            var query = new QueryExpression(payload.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                TopCount = 2,
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            query.Criteria.AddCondition(
                payload.ExternalIdAttribute,
                ConditionOperator.Equal,
                NormalizeDataverseValue(payload.ExternalIdValue));

            var existing = await service.RetrieveMultipleAsync(query, cancellationToken);

            if (existing.Entities.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple '{payload.EntityLogicalName}' records found for ExternalIdAttribute '{payload.ExternalIdAttribute}'.");
            }

            return existing.Entities.FirstOrDefault()?.Id;
        }

        private async Task<EntityReference> ResolveLookupAsync(
            string attributeName,
            LookupDefinition lookupDef,
            IOrganizationServiceAsync2 service,
            CancellationToken cancellationToken)
        {
            if (lookupDef == null)
            {
                throw new ArgumentNullException(nameof(lookupDef));
            }

            if (string.IsNullOrWhiteSpace(lookupDef.EntityLogicalName))
            {
                throw new InvalidOperationException($"Lookup definition for '{attributeName}' is missing EntityLogicalName.");
            }

            if (lookupDef.AlternateKeyAttributes == null || lookupDef.AlternateKeyAttributes.Count == 0)
            {
                throw new InvalidOperationException($"Lookup definition for '{attributeName}' must contain AlternateKeyAttributes.");
            }

            var query = new QueryExpression(lookupDef.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                TopCount = 2,
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            foreach (var keyAttribute in lookupDef.AlternateKeyAttributes)
            {
                query.Criteria.AddCondition(
                    keyAttribute.Key,
                    ConditionOperator.Equal,
                    NormalizeDataverseValue(keyAttribute.Value));
            }

            var results = await service.RetrieveMultipleAsync(query, cancellationToken);

            if (results.Entities.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Lookup resolution failed for '{attributeName}'. Multiple '{lookupDef.EntityLogicalName}' records match the supplied alternate key.");
            }

            if (results.Entities.Count == 1)
            {
                var existing = results.Entities[0];
                return new EntityReference(lookupDef.EntityLogicalName, existing.Id);
            }

            if (lookupDef.CreateIfNotExists)
            {
                var newEntity = new Entity(lookupDef.EntityLogicalName);

                if (lookupDef.CreateAttributes != null)
                {
                    foreach (var attribute in lookupDef.CreateAttributes)
                    {
                        newEntity[attribute.Key] = NormalizeDataverseValue(attribute.Value);
                    }
                }

                foreach (var keyAttribute in lookupDef.AlternateKeyAttributes)
                {
                    if (!newEntity.Attributes.ContainsKey(keyAttribute.Key))
                    {
                        newEntity[keyAttribute.Key] = NormalizeDataverseValue(keyAttribute.Value);
                    }
                }

                var newId = await service.CreateAsync(newEntity, cancellationToken);

                _logger.LogInformation(
                    "Created lookup target entity {LookupEntityLogicalName} with id {LookupId} for attribute {AttributeName}",
                    lookupDef.EntityLogicalName,
                    newId,
                    attributeName);

                return new EntityReference(lookupDef.EntityLogicalName, newId);
            }

            throw new InvalidOperationException(
                $"Lookup resolution failed for '{attributeName}'. Entity '{lookupDef.EntityLogicalName}' was not found and CreateIfNotExists is false.");
        }

        private void LogEntityAttributes(Entity entity)
        {
            if (entity.Attributes.Count == 0)
            {
                _logger.LogInformation("Entity {EntityLogicalName} has no attributes before save.", entity.LogicalName);
                return;
            }

            var attributes = entity.Attributes.Select(a =>
            {
                var value = a.Value switch
                {
                    null => "null",
                    EntityReference er => $"EntityReference({er.LogicalName}, {er.Id})",
                    OptionSetValue osv => $"OptionSetValue({osv.Value})",
                    Money money => $"Money({money.Value})",
                    OptionSetValueCollection collection => $"OptionSetValueCollection([{string.Join(", ", collection.Select(x => x.Value))}])",
                    _ => a.Value.ToString()
                };

                return $"{a.Key}={value}";
            });

            _logger.LogInformation(
                "Entity {EntityLogicalName} attributes before save: {Attributes}",
                entity.LogicalName,
                string.Join(", ", attributes));
        }

        private Task ThrottleForDataverseLimitAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private void OnRetry(Exception ex, TimeSpan delay, int retryCount, Context context)
        {
            _logger.LogWarning(
                ex,
                "Retrying Dataverse operation. Attempt={RetryCount}, DelayMs={DelayMs}",
                retryCount,
                delay.TotalMilliseconds);
        }

        private static object? NormalizeDataverseValue(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is not JsonElement element)
            {
                return value;
            }

            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when element.TryGetGuid(out var guidValue) => guidValue,
                JsonValueKind.String when element.TryGetDateTimeOffset(out var dtoValue) => dtoValue.UtcDateTime,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
                JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.Array => element.EnumerateArray().Select(item => NormalizeDataverseValue(item)).ToArray(),
                JsonValueKind.Object => element.GetRawText(),
                _ => element.ToString()
            };
        }
    }
}