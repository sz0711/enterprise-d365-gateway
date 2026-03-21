using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IEarlyboundEntityMapper
    {
        void ValidatePayload(UpsertPayload payload);
        Entity MapToEntity(UpsertPayload payload);
    }
}
