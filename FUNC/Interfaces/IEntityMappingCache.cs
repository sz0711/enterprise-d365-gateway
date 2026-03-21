namespace enterprise_d365_gateway.Interfaces;

public interface IEntityMappingCache
{
    Task<Guid?> GetAsync(string entityLogicalName, string externalIdAttribute, string normalizedValue, CancellationToken cancellationToken = default);
    Task SetAsync(string entityLogicalName, string externalIdAttribute, string normalizedValue, Guid id, CancellationToken cancellationToken = default);
    void Remove(string entityLogicalName, string externalIdAttribute, string normalizedValue);
}
