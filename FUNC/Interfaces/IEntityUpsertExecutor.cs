using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IEntityUpsertExecutor
    {
        Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken = default);
        Task UpdateAsync(Entity entity, CancellationToken cancellationToken = default);
        Task<EntityCollection> RetrieveMultipleAsync(QueryExpression query, CancellationToken cancellationToken = default);
    }
}
