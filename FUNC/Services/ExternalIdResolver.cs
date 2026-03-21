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
            string externalIdAttribute,
            object externalIdValue,
            CancellationToken cancellationToken = default)
        {
            var normalizedKey = externalIdValue?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedKey)) return null;

            // Cache first
            var cached = await _cache.GetAsync(entityLogicalName, externalIdAttribute, normalizedKey, cancellationToken);
            if (cached.HasValue)
            {
                _logger.LogDebug(
                    "ExternalId cache hit: {Entity}:{Attribute}:{Key} -> {Id}",
                    entityLogicalName, externalIdAttribute, normalizedKey, cached.Value);
                return cached.Value;
            }

            // Query Dataverse
            var query = new QueryExpression(entityLogicalName)
            {
                ColumnSet = new ColumnSet(false),
                TopCount = 2,
                Criteria = new FilterExpression(LogicalOperator.And)
            };
            query.Criteria.AddCondition(
                externalIdAttribute,
                ConditionOperator.Equal,
                DataverseValueNormalizer.Normalize(externalIdValue));

            var results = await _executor.RetrieveMultipleAsync(query, cancellationToken);

            if (results.Entities.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple '{entityLogicalName}' records found for ExternalIdAttribute '{externalIdAttribute}'.");
            }

            var existingId = results.Entities.FirstOrDefault()?.Id;
            if (existingId.HasValue)
            {
                await _cache.SetAsync(entityLogicalName, externalIdAttribute, normalizedKey, existingId.Value, cancellationToken);
                _logger.LogDebug(
                    "ExternalId resolved from Dataverse: {Entity}:{Attribute}:{Key} -> {Id}",
                    entityLogicalName, externalIdAttribute, normalizedKey, existingId.Value);
            }

            return existingId;
        }

        public void Invalidate(string entityLogicalName, string externalIdAttribute, string externalIdValue)
        {
            var normalizedKey = externalIdValue?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedKey))
            {
                _cache.Remove(entityLogicalName, externalIdAttribute, normalizedKey);
                _logger.LogInformation(
                    "ExternalId cache invalidated: {Entity}:{Attribute}:{Key}",
                    entityLogicalName, externalIdAttribute, normalizedKey);
            }
        }
    }
}
