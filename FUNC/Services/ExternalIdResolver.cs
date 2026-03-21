using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Services
{
    public class ExternalIdResolver : IExternalIdResolver
    {
        private readonly IEntityMappingCache _cache;
        private readonly IEntityUpsertExecutor _executor;
        private readonly ILogger<ExternalIdResolver> _logger;

        public ExternalIdResolver(
            IEntityMappingCache cache,
            IEntityUpsertExecutor executor,
            ILogger<ExternalIdResolver> logger)
        {
            _cache = cache;
            _executor = executor;
            _logger = logger;
        }

        public async Task<Guid?> ResolveAsync(
            string entityLogicalName,
            IDictionary<string, object?> keyAttributes,
            CancellationToken cancellationToken = default)
        {
            if (keyAttributes == null || keyAttributes.Count == 0)
            {
                return null;
            }

            var keySignature = KeyAttributesFormatter.BuildSignature(entityLogicalName, keyAttributes);

            // Cache first
            var cached = await _cache.GetAsync(entityLogicalName, keySignature, cancellationToken);
            if (cached.HasValue)
            {
                _logger.LogDebug(
                    "KeyAttributes cache hit: {Signature} -> {Id}",
                    keySignature, cached.Value);
                return cached.Value;
            }

            // Query Dataverse
            var query = new QueryExpression(entityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                TopCount = 3,
                Criteria = new FilterExpression(LogicalOperator.And)
            };
            foreach (var keyAttribute in keyAttributes)
            {
                query.Criteria.AddCondition(
                    keyAttribute.Key,
                    ConditionOperator.Equal,
                    DataverseValueNormalizer.Normalize(keyAttribute.Value));
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
                    "KeyAttributes query failed for entity {Entity}, signature {Signature}",
                    entityLogicalName,
                    keySignature);
                throw;
            }

            if (results.Entities.Count > 1)
            {
                _logger.LogError(
                    "DuplicateKeyAttributesDetected. Entity={Entity}, Signature={Signature}, MatchCount={MatchCount}",
                    entityLogicalName, keySignature, results.Entities.Count);

                throw new InvalidOperationException(
                    $"Multiple '{entityLogicalName}' records ({results.Entities.Count}+) found for KeyAttributes '{keySignature}'.");
            }

            var existingId = results.Entities.FirstOrDefault()?.Id;
            if (existingId.HasValue)
            {
                await _cache.SetAsync(entityLogicalName, keySignature, existingId.Value, cancellationToken);
                _logger.LogDebug(
                    "KeyAttributes resolved from Dataverse: {Signature} -> {Id}",
                    keySignature, existingId.Value);
            }

            return existingId;
        }

        public void Invalidate(string entityLogicalName, IDictionary<string, object?> keyAttributes)
        {
            if (keyAttributes == null || keyAttributes.Count == 0)
            {
                return;
            }

            var keySignature = KeyAttributesFormatter.BuildSignature(entityLogicalName, keyAttributes);
            _cache.Remove(entityLogicalName, keySignature);
            _logger.LogInformation("KeyAttributes cache invalidated: {Signature}", keySignature);
        }
    }
}
