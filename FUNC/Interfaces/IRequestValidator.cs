using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IRequestValidator
    {
        void Validate(UpsertPayload payload);
        void ValidateBatch(UpsertBatchRequest request);
    }
}
