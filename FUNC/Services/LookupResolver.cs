using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class LookupResolver : ILookupResolver
    {
        private readonly IEntityUpsertExecutor _executor;
        private readonly IEntityMappingCache _cache;
        private readonly IEarlyboundEntityMapper _mapper;
        private readonly IUpsertLockCoordinator _lockCoordinator;
        private readonly ILogger<LookupResolver> _logger;
        private readonly DataverseOptions _options;

        public LookupResolver(
            IEntityUpsertExecutor executor,
            IEntityMappingCache cache,
            IEarlyboundEntityMapper mapper,
            IUpsertLockCoordinator lockCoordinator,
            ILogger<LookupResolver> logger,
            IOptions<DataverseOptions> options)
        {
            _executor = executor;
            _cache = cache;
            _mapper = mapper;
            _lockCoordinator = lockCoordinator;
            _logger = logger;
            _options = options.Value;
        }

        public async Task<(EntityReference Reference, LookupTrace Trace)> ResolveAsync(
            string attributeName,
            LookupDefinition lookupDef,
            int currentDepth,
            int maxDepth,
            HashSet<string> visited,
            CancellationToken cancellationToken = default)
        {
            // Apply lookup timeout budget at the root call (depth 0)
            if (currentDepth == 0 && _options.LookupTimeoutSeconds > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.LookupTimeoutSeconds));

                try
                {
                    return await ResolveInternalAsync(attributeName, lookupDef, currentDepth, maxDepth, visited, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Lookup resolution for '{attributeName}' exceeded the timeout of {_options.LookupTimeoutSeconds}s.");
                }
            }

            return await ResolveInternalAsync(attributeName, lookupDef, currentDepth, maxDepth, visited, cancellationToken);
        }

        private async Task<(EntityReference Reference, LookupTrace Trace)> ResolveInternalAsync(
            string attributeName,
            LookupDefinition lookupDef,
            int currentDepth,
            int maxDepth,
            HashSet<string> visited,
            CancellationToken cancellationToken)
        {
            // Caller-supplied depth overrides are clamped to the configured bound
            // so a request can narrow the budget but never widen it.
            var effectiveMaxDepth = lookupDef.MaxDepth.HasValue
                ? Math.Clamp(lookupDef.MaxDepth.Value, 1, _options.MaxLookupDepth)
                : maxDepth;

            if (currentDepth >= effectiveMaxDepth)
            {
                throw new InvalidOperationException(
                    $"Lookup resolution for '{attributeName}' exceeded maximum depth of {effectiveMaxDepth}.");
            }

            // Cycle detection
            var cycleKey = KeyAttributesFormatter.BuildSignature(lookupDef.EntityLogicalName, lookupDef.KeyAttributes);
            if (!visited.Add(cycleKey))
            {
                throw new InvalidOperationException(
                    $"Cyclic lookup detected for '{attributeName}' ({cycleKey}). Resolution path: {string.Join(" -> ", visited)}");
            }

            try
            {
                var trace = new LookupTrace
                {
                    AttributeName = attributeName,
                    EntityLogicalName = lookupDef.EntityLogicalName,
                    Depth = currentDepth
                };

                // Shared mapping cache first — identical lookups within a batch
                // (e.g. many rows referencing the same parent) cost one query.
                var cached = await _cache.GetAsync(lookupDef.EntityLogicalName, cycleKey, cancellationToken);
                if (cached.HasValue)
                {
                    trace.ResolvedId = cached.Value;
                    trace.WasCreated = false;

                    _logger.LogDebug(
                        "Lookup cache hit: {Attribute} -> {Entity} {Id} (depth {Depth})",
                        attributeName, lookupDef.EntityLogicalName, cached.Value, currentDepth);

                    return (new EntityReference(lookupDef.EntityLogicalName, cached.Value), trace);
                }

                if (!lookupDef.CreateIfNotExists)
                {
                    // Read-only path — no lock needed.
                    return await QueryOrCreateAsync(attributeName, lookupDef, currentDepth, effectiveMaxDepth, visited, trace, cycleKey, cancellationToken);
                }

                // Create-if-missing must be atomic per key, otherwise concurrent
                // batch items race the query-then-create and produce duplicates.
                // The orchestrator resolves lookups BEFORE taking the payload lock,
                // so this lookup lock never nests under a payload lock. The only
                // residual nesting is a parent lookup lock held while a nested
                // create-if-not-exists lookup acquires its own lock; the wait is
                // bounded by the passed token, which already carries the depth-0
                // LookupTimeoutSeconds budget (a deliberately generous bound so
                // legitimate slow creates under high same-parent contention are
                // never mistaken for a stall).
                using var lockHandle = await _lockCoordinator.AcquireAsync(cycleKey, cancellationToken);

                // Another holder may have created the record while we waited.
                cached = await _cache.GetAsync(lookupDef.EntityLogicalName, cycleKey, cancellationToken);
                if (cached.HasValue)
                {
                    trace.ResolvedId = cached.Value;
                    trace.WasCreated = false;
                    return (new EntityReference(lookupDef.EntityLogicalName, cached.Value), trace);
                }

                return await QueryOrCreateAsync(attributeName, lookupDef, currentDepth, effectiveMaxDepth, visited, trace, cycleKey, cancellationToken);
            }
            finally
            {
                visited.Remove(cycleKey);
            }
        }

        private async Task<(EntityReference Reference, LookupTrace Trace)> QueryOrCreateAsync(
            string attributeName,
            LookupDefinition lookupDef,
            int currentDepth,
            int effectiveMaxDepth,
            HashSet<string> visited,
            LookupTrace trace,
            string cycleKey,
            CancellationToken cancellationToken)
        {
            // Query for existing entity
            var query = new QueryExpression(lookupDef.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                TopCount = 3,
                Criteria = new FilterExpression(LogicalOperator.And)
            };

            foreach (var keyAttr in lookupDef.KeyAttributes)
            {
                query.Criteria.AddCondition(
                    keyAttr.Key.ToLowerInvariant(),
                    ConditionOperator.Equal,
                    _mapper.ConvertQueryValue(lookupDef.EntityLogicalName, keyAttr.Key, keyAttr.Value));
            }

            EntityCollection results;
            try
            {
                results = await _executor.RetrieveMultipleAsync(query, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Lookup query failed for attribute {Attribute}, entity {Entity}, signature {Signature}, depth {Depth}",
                    attributeName,
                    lookupDef.EntityLogicalName,
                    cycleKey,
                    currentDepth);
                throw;
            }

            if (results.Entities.Count > 1)
            {
                _logger.LogError(
                    "DuplicateLookupDetected. Attribute={Attribute}, Entity={Entity}, Signature={Signature}, MatchCount={MatchCount}, Depth={Depth}",
                    attributeName, lookupDef.EntityLogicalName, cycleKey, results.Entities.Count, currentDepth);

                throw new InvalidOperationException(
                    $"Lookup resolution for '{attributeName}': {results.Entities.Count}+ '{lookupDef.EntityLogicalName}' records match the alternate key.");
            }

            if (results.Entities.Count == 1)
            {
                var existing = results.Entities[0];
                trace.ResolvedId = existing.Id;
                trace.WasCreated = false;

                await _cache.SetAsync(lookupDef.EntityLogicalName, cycleKey, existing.Id, cancellationToken);

                _logger.LogInformation(
                    "Lookup resolved: {Attribute} -> {Entity} {Id} (depth {Depth})",
                    attributeName, lookupDef.EntityLogicalName, existing.Id, currentDepth);

                return (new EntityReference(lookupDef.EntityLogicalName, existing.Id), trace);
            }

            // Not found — create if allowed
            if (!lookupDef.CreateIfNotExists)
            {
                throw new InvalidOperationException(
                    $"Lookup resolution for '{attributeName}': entity '{lookupDef.EntityLogicalName}' not found and CreateIfNotExists is false.");
            }

            var newEntity = new Entity(lookupDef.EntityLogicalName);

            if (lookupDef.CreateAttributes != null)
            {
                foreach (var attr in lookupDef.CreateAttributes)
                {
                    newEntity[attr.Key.ToLowerInvariant()] =
                        _mapper.ConvertWriteValue(lookupDef.EntityLogicalName, attr.Key, attr.Value);
                }
            }

            foreach (var keyAttr in lookupDef.KeyAttributes)
            {
                var canonicalKey = keyAttr.Key.ToLowerInvariant();
                if (!newEntity.Attributes.ContainsKey(canonicalKey))
                {
                    newEntity[canonicalKey] =
                        _mapper.ConvertWriteValue(lookupDef.EntityLogicalName, keyAttr.Key, keyAttr.Value);
                }
            }

            // Resolve nested lookups BEFORE creating the entity
            if (lookupDef.NestedLookups != null && lookupDef.NestedLookups.Count > 0)
            {
                var nestedTraces = new List<LookupTrace>();
                foreach (var nested in lookupDef.NestedLookups)
                {
                    var (nestedRef, nestedTrace) = await ResolveInternalAsync(
                        nested.Key,
                        nested.Value,
                        currentDepth + 1,
                        effectiveMaxDepth,
                        visited,
                        cancellationToken);

                    newEntity[nested.Key.ToLowerInvariant()] = nestedRef;
                    nestedTraces.Add(nestedTrace);
                }

                trace.NestedTraces = nestedTraces;
            }

            var newId = await _executor.CreateAsync(newEntity, cancellationToken);
            trace.ResolvedId = newId;
            trace.WasCreated = true;

            await _cache.SetAsync(lookupDef.EntityLogicalName, cycleKey, newId, cancellationToken);

            _logger.LogInformation(
                "Lookup created: {Attribute} -> {Entity} {Id} (depth {Depth})",
                attributeName, lookupDef.EntityLogicalName, newId, currentDepth);

            return (new EntityReference(lookupDef.EntityLogicalName, newId), trace);
        }
    }
}
