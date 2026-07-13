using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class DataverseServiceClientFactory : IDataverseServiceClientFactory
    {
        private readonly DataverseOptions _options;
        private readonly IDataverseTokenProvider _tokenProvider;
        private readonly ILogger<DataverseServiceClientFactory> _logger;
        private readonly SemaphoreSlim _clientLock = new(1, 1);
        private ServiceClient? _serviceClient;

        public DataverseServiceClientFactory(
            IOptions<DataverseOptions> options,
            IDataverseTokenProvider tokenProvider,
            ILogger<DataverseServiceClientFactory> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (string.IsNullOrWhiteSpace(_options.Url))
                throw new InvalidOperationException("Dataverse URL configuration is required (Dataverse:Url).");
        }

        public async Task<IOrganizationServiceAsync2> GetOrCreateServiceAsync(CancellationToken cancellationToken = default)
        {
            var existing = Volatile.Read(ref _serviceClient);
            if (existing != null && existing.IsReady)
            {
                return existing;
            }

            await _clientLock.WaitAsync(cancellationToken);
            try
            {
                existing = Volatile.Read(ref _serviceClient);
                if (existing != null && existing.IsReady)
                {
                    return existing;
                }

                // Fail fast (honoring the caller's token) before building the client.
                var token = await _tokenProvider.GetTokenAsync(_options.Url, cancellationToken);
                _logger.LogDebug("Dataverse access token acquired. ExpiresOn={ExpiresOn}", token.ExpiresOn);

                // IMPORTANT: the token callback lives as long as the (process-wide)
                // ServiceClient. It must NOT observe any request's cancellation
                // token — a cancelled captured token would poison every future
                // token refresh and take the client down until process restart.
                var client = new ServiceClient(new Uri(_options.Url), async (string resource) =>
                {
                    var refreshed = await _tokenProvider.GetTokenAsync(resource, CancellationToken.None);
                    return refreshed.Token;
                });

                if (!client.IsReady)
                {
                    var lastError = string.IsNullOrWhiteSpace(client.LastError)
                        ? "ServiceClient initialization failed without LastError."
                        : client.LastError;

                    _logger.LogError(
                        client.LastException,
                        "ServiceClient initialization failed. Url={DataverseUrl}. LastError={LastError}",
                        _options.Url,
                        lastError);

                    var initException = new InvalidOperationException(
                        $"Failed to connect to Dataverse. {lastError}",
                        client.LastException);
                    client.Dispose();
                    throw initException;
                }

                // The gateway runs parallel batches against the shared client;
                // pinning every call to one Dataverse front-end node via the
                // affinity cookie caps throughput for no benefit here.
                client.EnableAffinityCookie = false;

                if (client is not IOrganizationServiceAsync2)
                {
                    client.Dispose();
                    throw new InvalidOperationException("Unable to create a Dataverse IOrganizationServiceAsync2 from ServiceClient.");
                }

                // Deliberately do NOT dispose a previous (broken) client here:
                // other threads may still hold a reference from the lock-free fast
                // path. Reconnects are rare; the abandoned instance is reclaimed
                // by the GC once those calls drain.
                Volatile.Write(ref _serviceClient, client);
                return client;
            }
            finally
            {
                _clientLock.Release();
            }
        }
    }
}
