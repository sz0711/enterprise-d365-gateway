using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface ISapAccountMapper
    {
        SapMappingResult Map(SapAccountWithContactsRequest request);
    }
}
