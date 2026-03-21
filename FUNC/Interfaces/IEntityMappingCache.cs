namespace enterprise_d365_gateway.Interfaces;

public interface IEntityMappingCache
{
    Task<Guid?> GetAsync(string entityLogicalName, string externalSystemId, CancellationToken cancellationToken = default);
    Task SetAsync(string entityLogicalName, string externalSystemId, Guid id, CancellationToken cancellationToken = default);

    Task<Guid> GetOrAddAsync(string entityLogicalName, string externalSystemId, Func<Task<Guid>> factory, CancellationToken cancellationToken = default);
}
