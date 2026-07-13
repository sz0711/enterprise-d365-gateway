using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class UpsertOrchestrator : IDataverseUpsertService
    {
        private static readonly ActivitySource ActivitySource = new("enterprise-d365-gateway.UpsertOrchestrator");
        private readonly IRequestValidator _validator;
        private readonly IEarlyboundEntityMapper _mapper;
        private readonly IExternalIdResolver _externalIdResolver;
        private readonly ILookupResolver _lookupResolver;
        private readonly IEntityUpsertExecutor _executor;
        private readonly IUpsertLockCoordinator _lockCoordinator;
        private readonly IErrorClassifier _errorClassifier;
        private readonly IResultMapper _resultMapper;
        private readonly IEntityMappingCache _cache;
        private readonly IAdaptiveConcurrencyLimiter _concurrencyLimiter;
        private readonly ILogger<UpsertOrchestrator> _logger;
        private readonly DataverseOptions _options;

        public UpsertOrchestrator(
            IRequestValidator validator,
            IEarlyboundEntityMapper mapper,
            IExternalIdResolver externalIdResolver,
            ILookupResolver lookupResolver,
            IEntityUpsertExecutor executor,
            IUpsertLockCoordinator lockCoordinator,
            IErrorClassifier errorClassifier,
            IResultMapper resultMapper,
            IEntityMappingCache cache,
            IAdaptiveConcurrencyLimiter concurrencyLimiter,
            ILogger<UpsertOrchestrator> logger,
            IOptions<DataverseOptions> options)
        {
            _validator = validator;
            _mapper = mapper;
            _externalIdResolver = externalIdResolver;
            _lookupResolver = lookupResolver;
            _executor = executor;
            _lockCoordinator = lockCoordinator;
            _errorClassifier = errorClassifier;
            _resultMapper = resultMapper;
            _cache = cache;
            _concurrencyLimiter = concurrencyLimiter;
            _logger = logger;
            _options = options.Value;
        }

        public async Task<UpsertResult> UpsertAsync(UpsertPayload payload, CancellationToken cancellationToken = default)
        {
            return await ExecuteWithCacheRetry(payload, cancellationToken);
        }

        public async Task<IReadOnlyList<UpsertResult>> UpsertBatchAsync(
            IEnumerable<UpsertPayload> payloads,
            CancellationToken cancellationToken = default)
        {
            var requestList = payloads?.ToList() ?? throw new ArgumentNullException(nameof(payloads));
            if (requestList.Count == 0)
                return Array.Empty<UpsertResult>();

            using var activity = ActivitySource.StartActivity("UpsertBatch");
            activity?.SetTag("batch.size", requestList.Count);

            var results = new UpsertResult[requestList.Count];
            var effectiveParallelism = _concurrencyLimiter.CurrentLimit;
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = effectiveParallelism
            };

            _logger.LogInformation(
                "BatchUpsertStarted. Total={Total}, Parallelism={Parallelism}",
                requestList.Count, effectiveParallelism);

            await Parallel.ForEachAsync(
                Enumerable.Range(0, requestList.Count),
                options,
                async (index, ct) =>
                {
                    results[index] = await UpsertAsync(requestList[index], ct);
                });

            int failures = 0, validationFailures = 0;
            foreach (var r in results)
            {
                if (r.ErrorCategory != ErrorCategory.None)
                {
                    failures++;
                    if (r.ErrorCategory == ErrorCategory.Validation)
                        validationFailures++;
                }
            }
            var technicalFailures = failures - validationFailures;

            activity?.SetTag("batch.failed", failures);
            activity?.SetTag("batch.validation_failed", validationFailures);
            activity?.SetTag("batch.technical_failed", technicalFailures);
            if (failures > 0) activity?.SetStatus(ActivityStatusCode.Error);

            _logger.LogInformation(
                "BatchUpsertCompleted. Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}",
                results.Length, failures, validationFailures, technicalFailures);

            return results;
        }

        private async Task<UpsertResult> ExecuteWithCacheRetry(
            UpsertPayload payload,
            CancellationToken cancellationToken)
        {
            var result = await ExecuteSingleAsync(payload, cancellationToken);

            // Retry exactly the failure modes a stale cached GUID or a create race
            // produces (duplicate record / record does not exist). Anything else —
            // validation faults, privilege errors, genuine transients (already
            // retried with backoff inside the executor) — must not trigger a
            // second full upsert cycle.
            if (result.ErrorCategory is ErrorCategory.Transient or ErrorCategory.Permanent
                && result.Exception != null
                && _errorClassifier.IsKeyConflict(result.Exception)
                && payload?.KeyAttributes != null
                && payload.KeyAttributes.Count > 0)
            {
                _externalIdResolver.Invalidate(payload.EntityLogicalName, payload.KeyAttributes);
                InvalidateLookupCaches(payload.Lookups);

                _logger.LogInformation(
                    "Retrying upsert after key conflict with invalidated cache for {Signature}",
                    KeyAttributesFormatter.BuildSignature(payload.EntityLogicalName, payload.KeyAttributes));

                result = await ExecuteSingleAsync(payload, cancellationToken);
            }

            result.Exception = null; // internal only — never serialized
            return result;
        }

        private void InvalidateLookupCaches(IDictionary<string, LookupDefinition>? lookups)
        {
            if (lookups == null)
                return;

            foreach (var lookup in lookups.Values)
            {
                if (lookup?.KeyAttributes is { Count: > 0 })
                {
                    _externalIdResolver.Invalidate(lookup.EntityLogicalName, lookup.KeyAttributes);
                    InvalidateLookupCaches(lookup.NestedLookups);
                }
            }
        }

        private async Task<UpsertResult> ExecuteSingleAsync(
            UpsertPayload payload,
            CancellationToken cancellationToken)
        {
            try
            {
                // 1. Validate
                _validator.Validate(payload);

                var keySignature = KeyAttributesFormatter.BuildSignature(payload.EntityLogicalName, payload.KeyAttributes);

                // 2. Map to entity
                var entity = _mapper.MapToEntity(payload);

                // 3. Resolve lookups (independent of each other — run concurrently).
                //    Done BEFORE acquiring the payload lock: lookup resolution targets
                //    OTHER records with their own keyed locks, so nesting the lookup
                //    locks under this payload's lock would risk a self- or ABBA deadlock
                //    (a lookup whose key equals the payload's own, or reciprocal
                //    references across concurrent payloads). Resolving first keeps the
                //    lock namespaces from ever nesting.
                List<LookupTrace>? lookupTraces = null;
                if (payload.Lookups != null && payload.Lookups.Count > 0)
                {
                    var maxDepth = Math.Clamp(payload.MaxLookupDepth ?? _options.MaxLookupDepth, 1, _options.MaxLookupDepth);

                    var lookupList = payload.Lookups.ToList();
                    var resolveTasks = lookupList.Select(lookup =>
                    {
                        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        return _lookupResolver.ResolveAsync(
                            lookup.Key,
                            lookup.Value,
                            0,
                            maxDepth,
                            visited,
                            cancellationToken);
                    }).ToArray();

                    // Task.WhenAll surfaces the first exception; drain the rest so no
                    // faulted task goes unobserved when a sibling lookup also fails.
                    try
                    {
                        await Task.WhenAll(resolveTasks);
                    }
                    catch
                    {
                        foreach (var t in resolveTasks)
                            _ = t.Exception; // observe
                        throw;
                    }

                    lookupTraces = new List<LookupTrace>(resolveTasks.Length);
                    for (int i = 0; i < resolveTasks.Length; i++)
                    {
                        var (reference, trace) = resolveTasks[i].Result;
                        entity[lookupList[i].Key.ToLowerInvariant()] = reference;
                        lookupTraces.Add(trace);

                        _logger.LogInformation(
                            "Resolved lookup {Attribute} -> {Entity} {Id}",
                            lookupList[i].Key, reference.LogicalName, reference.Id);
                    }
                }

                // 4. Acquire keyed lock for THIS record's upsert (serializes concurrent
                //    upserts of the same key so they don't both create).
                using var lockHandle = await _lockCoordinator.AcquireAsync(keySignature, cancellationToken);

                // 5. Resolve by key attributes
                if (entity.Id == Guid.Empty
                    && payload.KeyAttributes != null
                    && payload.KeyAttributes.Count > 0)
                {
                    var existingId = await _externalIdResolver.ResolveAsync(
                        payload.EntityLogicalName,
                        payload.KeyAttributes,
                        cancellationToken,
                        keySignature);

                    if (existingId.HasValue)
                        entity.Id = existingId.Value;
                }

                LogEntityAttributes(entity);

                // 6. Execute Create or Update
                Guid id;
                bool created;
                if (entity.Id != Guid.Empty)
                {
                    await _executor.UpdateAsync(entity, cancellationToken);
                    id = entity.Id;
                    created = false;
                }
                else
                {
                    id = await _executor.CreateAsync(entity, cancellationToken);
                    created = true;
                }

                // 7. Update cache after successful operation
                if (payload.KeyAttributes != null
                    && payload.KeyAttributes.Count > 0)
                {
                    await _cache.SetAsync(
                        payload.EntityLogicalName,
                        keySignature,
                        id,
                        cancellationToken);
                }

                return _resultMapper.MapSuccess(
                    payload.EntityLogicalName,
                    keySignature,
                    id,
                    created,
                    lookupTraces is { Count: > 0 } ? lookupTraces : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var category = _errorClassifier.Classify(ex);

                _logger.LogWarning(
                    ex,
                    "Upsert failed for {Entity} (Category={Category})",
                    payload?.EntityLogicalName, category);

                string? keySignature = null;
                if (payload != null
                    && !string.IsNullOrWhiteSpace(payload.EntityLogicalName)
                    && payload.KeyAttributes != null
                    && payload.KeyAttributes.Count > 0)
                {
                    keySignature = KeyAttributesFormatter.BuildSignature(payload.EntityLogicalName, payload.KeyAttributes);
                }

                var errorResult = _resultMapper.MapError(
                    payload?.EntityLogicalName ?? "<unknown>",
                    keySignature,
                    ex,
                    category);
                errorResult.Exception = ex;
                return errorResult;
            }
        }

        private void LogEntityAttributes(Entity entity)
        {
            // Attribute VALUES may contain personal data — only emit them at
            // Debug. The Information-level line carries names and count only.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var attributes = entity.Attributes.Select(a =>
                {
                    var value = a.Value switch
                    {
                        null => "null",
                        EntityReference er => $"EntityReference({er.LogicalName}, {er.Id})",
                        OptionSetValue osv => $"OptionSetValue({osv.Value})",
                        Money money => $"Money({money.Value})",
                        OptionSetValueCollection collection =>
                            $"OptionSetValueCollection([{string.Join(", ", collection.Select(x => x.Value))}])",
                        _ => a.Value.ToString()
                    };
                    return $"{a.Key}={value}";
                });

                _logger.LogDebug(
                    "Entity {EntityLogicalName} attributes before save: {Attributes}",
                    entity.LogicalName,
                    string.Join(", ", attributes));
                return;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Entity {EntityLogicalName} has {AttributeCount} attributes before save: {AttributeNames}",
                    entity.LogicalName,
                    entity.Attributes.Count,
                    string.Join(", ", entity.Attributes.Keys));
            }
        }
    }
}
