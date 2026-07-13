using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using System.Collections.Concurrent;

namespace enterprise_d365_gateway.Services
{
    public class DataverseTokenProvider : IDataverseTokenProvider
    {
        private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

        private readonly TokenCredential _credential;
        private readonly string _defaultScope;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly ConcurrentDictionary<string, AccessToken> _cachedTokens = new(StringComparer.OrdinalIgnoreCase);

        public DataverseTokenProvider(IOptions<DataverseOptions> options)
        {
            var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));

            // Prefer an explicitly configured scope; otherwise derive it from the
            // validated Dataverse URL. There is no valid wildcard fallback — an
            // unresolvable scope is a configuration error and must fail fast.
            if (!string.IsNullOrWhiteSpace(opts.Scope))
            {
                _defaultScope = opts.Scope;
            }
            else if (Uri.TryCreate(opts.Url, UriKind.Absolute, out var uri))
            {
                _defaultScope = $"{uri.Scheme}://{uri.Host}/.default";
            }
            else
            {
                throw new InvalidOperationException(
                    "Unable to derive a token scope: configure Dataverse:Scope or a valid Dataverse:Url.");
            }

            _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(opts.UserAssignedManagedIdentityClientId)
                    ? null
                    : opts.UserAssignedManagedIdentityClientId
            });
        }

        public async Task<AccessToken> GetTokenAsync(string resource, CancellationToken cancellationToken = default)
        {
            var scope = ResolveScope(resource);

            if (_cachedTokens.TryGetValue(scope, out var cachedToken) && cachedToken.ExpiresOn > DateTimeOffset.UtcNow.Add(ExpiryBuffer))
            {
                return cachedToken;
            }

            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                if (_cachedTokens.TryGetValue(scope, out cachedToken) && cachedToken.ExpiresOn > DateTimeOffset.UtcNow.Add(ExpiryBuffer))
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
