using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IDataverseUpsertService
    {
        Task<UpsertResult> UpsertAsync(UpsertPayload payload, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<UpsertResult>> UpsertBatchAsync(IEnumerable<UpsertPayload> payloads, CancellationToken cancellationToken = default);
    }
}
