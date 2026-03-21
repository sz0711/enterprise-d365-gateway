using Azure.Core;
using Azure.Identity;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
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

        public async Task<ServiceClient> GetOrCreateServiceAsync(CancellationToken cancellationToken = default)
        {
            if (_serviceClient != null && _serviceClient.IsReady)
            {
                return _serviceClient;
            }

            await _clientLock.WaitAsync(cancellationToken);
            try
            {
                if (_serviceClient != null && _serviceClient.IsReady)
                {
                    return _serviceClient;
                }

                // refresh the cached token before re-creating client.
                var token = await _tokenProvider.GetTokenAsync(_options.Url, cancellationToken);
                _logger.LogDebug("Dataverse access token acquired. ExpiresOn={ExpiresOn}", token.ExpiresOn);

                _serviceClient?.Dispose();

                _serviceClient = new ServiceClient(new Uri(_options.Url), async (string resource) =>
                {
                    var token = await _tokenProvider.GetTokenAsync(resource, cancellationToken);
                    return token.Token;
                });

                if (!_serviceClient.IsReady)
                {
                    var lastError = string.IsNullOrWhiteSpace(_serviceClient.LastError)
                        ? "ServiceClient initialization failed without LastError."
                        : _serviceClient.LastError;

                    _logger.LogError(
                        _serviceClient.LastException,
                        "ServiceClient initialization failed. Url={DataverseUrl}. LastError={LastError}",
                        _options.Url,
                        lastError);

                    throw new InvalidOperationException(
                        $"Failed to connect to Dataverse. {lastError}",
                        _serviceClient.LastException);
                }

                if (!(_serviceClient is IOrganizationServiceAsync2))
                {
                    throw new InvalidOperationException("Unable to create a Dataverse IOrganizationServiceAsync2 from ServiceClient.");
                }

                return _serviceClient;
            }
            finally
            {
                _clientLock.Release();
            }
        }
    }
}