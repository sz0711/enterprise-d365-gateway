using Microsoft.PowerPlatform.Dataverse.Client;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IDataverseServiceClientFactory
    {
        Task<IOrganizationServiceAsync2> GetOrCreateServiceAsync(CancellationToken cancellationToken = default);
    }
}
