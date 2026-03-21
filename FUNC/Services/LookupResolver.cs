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
        private readonly ILogger<LookupResolver> _logger;
        private readonly DataverseOptions _options;

        public LookupResolver(
            IEntityUpsertExecutor executor,
            ILogger<LookupResolver> logger,
            IOptions<DataverseOptions> options)
        {
            _executor = executor;
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
            var effectiveMaxDepth = lookupDef.MaxDepth ?? maxDepth;

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
                        keyAttr.Key,
                        ConditionOperator.Equal,
                        DataverseValueNormalizer.Normalize(keyAttr.Value));
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
                        newEntity[attr.Key] = DataverseValueNormalizer.Normalize(attr.Value);
                    }
                }

                foreach (var keyAttr in lookupDef.KeyAttributes)
                {
                    if (!newEntity.Attributes.ContainsKey(keyAttr.Key))
                    {
                        newEntity[keyAttr.Key] = DataverseValueNormalizer.Normalize(keyAttr.Value);
                    }
                }

                // Resolve nested lookups BEFORE creating the entity
                if (lookupDef.NestedLookups != null && lookupDef.NestedLookups.Count > 0)
                {
                    var nestedTraces = new List<LookupTrace>();
                    foreach (var nested in lookupDef.NestedLookups)
                    {
                        var (nestedRef, nestedTrace) = await ResolveAsync(
                            nested.Key,
                            nested.Value,
                            currentDepth + 1,
                            effectiveMaxDepth,
                            visited,
                            cancellationToken);

                        newEntity[nested.Key] = nestedRef;
                        nestedTraces.Add(nestedTrace);
                    }

                    trace.NestedTraces = nestedTraces;
                }

                var newId = await _executor.CreateAsync(newEntity, cancellationToken);
                trace.ResolvedId = newId;
                trace.WasCreated = true;

                _logger.LogInformation(
                    "Lookup created: {Attribute} -> {Entity} {Id} (depth {Depth})",
                    attributeName, lookupDef.EntityLogicalName, newId, currentDepth);

                return (new EntityReference(lookupDef.EntityLogicalName, newId), trace);
            }
            finally
            {
                visited.Remove(cycleKey);
            }
        }
    }
}
