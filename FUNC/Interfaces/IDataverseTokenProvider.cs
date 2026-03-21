using Azure.Core;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IDataverseTokenProvider
    {
        Task<AccessToken> GetTokenAsync(string resource, CancellationToken cancellationToken = default);
    }
}
