using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using enterprise_d365_gateway.Interfaces;
using System.Collections.Concurrent;

namespace enterprise_d365_gateway.Services
{
    public class DataverseTokenProvider : IDataverseTokenProvider
    {
        private readonly TokenCredential _credential;
        private readonly string _defaultScope;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly ConcurrentDictionary<string, AccessToken> _cachedTokens = new(StringComparer.OrdinalIgnoreCase);

        public DataverseTokenProvider(IConfiguration configuration)
        {
            var clientId = configuration.GetValue<string>("Dataverse:UserAssignedManagedIdentityClientId");
            var dataverseUrl = configuration.GetValue<string>("Dataverse:Url");
            var configuredScope = configuration.GetValue<string>("Dataverse:Scope");

            // If no explicit scope is configured, derive it from the Dataverse URL
            var scope = configuredScope;
            if (string.IsNullOrWhiteSpace(scope) && !string.IsNullOrWhiteSpace(dataverseUrl))
            {
                var uri = new Uri(dataverseUrl);
                scope = $"{uri.Scheme}://{uri.Host}/.default";
            }
            else if (string.IsNullOrWhiteSpace(scope))
            {
                scope = "https://*.crm.dynamics.com/.default";
            }

            _defaultScope = scope;

            _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = clientId
            });
        }

        public async Task<AccessToken> GetTokenAsync(string resource, CancellationToken cancellationToken = default)
        {
            var scope = ResolveScope(resource);

            if (_cachedTokens.TryGetValue(scope, out var cachedToken) && cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return cachedToken;
            }

            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                if (_cachedTokens.TryGetValue(scope, out cachedToken) && cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    return cachedToken;
                }

                var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), cancellationToken);
                _cachedTokens[scope] = token;
                return token;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private string ResolveScope(string resource)
        {
            if (string.IsNullOrWhiteSpace(resource))
            {
                return _defaultScope;
            }

            if (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri))
            {
                return _defaultScope;
            }

            return $"{resourceUri.Scheme}://{resourceUri.Host}/.default";
        }
    }
}