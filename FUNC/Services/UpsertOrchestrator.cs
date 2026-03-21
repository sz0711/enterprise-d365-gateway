using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class UpsertOrchestrator : IDataverseUpsertService
    {
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

            var results = new UpsertResult[requestList.Count];
            var effectiveParallelism = _concurrencyLimiter.CurrentLimit;
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = effectiveParallelism
            };

            _logger.LogInformation(
                "Starting batch upsert. Total={Total}, Parallelism={Parallelism}",
                requestList.Count, effectiveParallelism);

            await Parallel.ForEachAsync(
                Enumerable.Range(0, requestList.Count),
                options,
                async (index, ct) =>
                {
                    results[index] = await UpsertAsync(requestList[index], ct);
                });

            var failures = results.Count(r => r.ErrorCategory != ErrorCategory.None);
            _logger.LogInformation("Batch upsert completed. Total={Total}, Failed={Failed}", results.Length, failures);

            return results;
        }

        private async Task<UpsertResult> ExecuteWithCacheRetry(
            UpsertPayload payload,
            CancellationToken cancellationToken)
        {
            var result = await ExecuteSingleAsync(payload, cancellationToken);

            // If failed with possible stale cache, invalidate and retry once
            if (result.ErrorCategory is ErrorCategory.Transient or ErrorCategory.Permanent
                && payload.KeyAttributes != null
                && payload.KeyAttributes.Count > 0)
            {
                _externalIdResolver.Invalidate(payload.EntityLogicalName, payload.KeyAttributes);

                _logger.LogInformation(
                    "Retrying upsert with invalidated cache for {Signature}",
                    KeyAttributesFormatter.BuildSignature(payload.EntityLogicalName, payload.KeyAttributes));

                result = await ExecuteSingleAsync(payload, cancellationToken);
            }

            return result;
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

                // 2. Acquire keyed lock
                using var lockHandle = await _lockCoordinator.AcquireAsync(keySignature, cancellationToken);

                // 3. Map to entity
                var entity = _mapper.MapToEntity(payload);

                // 4. Resolve lookups
                var lookupTraces = new List<LookupTrace>();
                if (payload.Lookups != null && payload.Lookups.Count > 0)
                {
                    var maxDepth = payload.MaxLookupDepth ?? _options.MaxLookupDepth;

                    foreach (var lookup in payload.Lookups)
                    {
                        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var (reference, trace) = await _lookupResolver.ResolveAsync(
                            lookup.Key,
                            lookup.Value,
                            0,
                            maxDepth,
                            visited,
                            cancellationToken);

                        entity[lookup.Key] = reference;
                        lookupTraces.Add(trace);

                        _logger.LogInformation(
                            "Resolved lookup {Attribute} -> {Entity} {Id}",
                            lookup.Key, reference.LogicalName, reference.Id);
                    }
                }

                // 5. Resolve by key attributes
                if (entity.Id == Guid.Empty
                    && payload.KeyAttributes != null
                    && payload.KeyAttributes.Count > 0)
                {
                    var existingId = await _externalIdResolver.ResolveAsync(
                        payload.EntityLogicalName,
                        payload.KeyAttributes,
                        cancellationToken);

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
                    lookupTraces.Count > 0 ? lookupTraces : null);
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
                    payload.EntityLogicalName, category);

                string? keySignature = null;
                if (!string.IsNullOrWhiteSpace(payload.EntityLogicalName)
                    && payload.KeyAttributes != null
                    && payload.KeyAttributes.Count > 0)
                {
                    keySignature = KeyAttributesFormatter.BuildSignature(payload.EntityLogicalName, payload.KeyAttributes);
                }

                return _resultMapper.MapError(
                    payload.EntityLogicalName,
                    keySignature,
                    ex,
                    category);
            }
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
                    OptionSetValueCollection collection =>
                        $"OptionSetValueCollection([{string.Join(", ", collection.Select(x => x.Value))}])",
                    _ => a.Value.ToString()
                };
                return $"{a.Key}={value}";
            });

            _logger.LogInformation(
                "Entity {EntityLogicalName} attributes before save: {Attributes}",
                entity.LogicalName,
                string.Join(", ", attributes));
        }
    }
}
