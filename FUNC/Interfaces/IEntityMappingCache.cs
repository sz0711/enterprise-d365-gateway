namespace enterprise_d365_gateway.Interfaces;

public interface IEntityMappingCache
{
    Task<Guid?> GetAsync(string entityLogicalName, string keySignature, CancellationToken cancellationToken = default);
    Task SetAsync(string entityLogicalName, string keySignature, Guid id, CancellationToken cancellationToken = default);
    void Remove(string entityLogicalName, string keySignature);
}
