using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Services
{
    public class HealthCheckService : IHealthCheckService
    {
        private readonly IDataverseServiceClientFactory _clientFactory;
        private readonly ILogger<HealthCheckService> _logger;

        public HealthCheckService(
            IDataverseServiceClientFactory clientFactory,
            ILogger<HealthCheckService> logger)
        {
            _clientFactory = clientFactory;
            _logger = logger;
        }

        public bool IsLive() => true;

        public async Task<HealthCheckResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            var checks = new Dictionary<string, HealthCheckEntry>();

            // Dataverse connectivity: a real round trip (WhoAmI), not just a cached
            // client handle — a poisoned/broken client must turn readiness red.
            try
            {
                var client = await _clientFactory.GetOrCreateServiceAsync(cancellationToken);
                var response = (WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest(), cancellationToken);
                checks["dataverse"] = new HealthCheckEntry
                {
                    Status = "Healthy",
                    Detail = $"WhoAmI round trip OK (org {response.OrganizationId})."
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Readiness check: Dataverse connectivity failed.");
                // Exception details stay in the logs; the (function-key gated)
                // endpoint only reports the failure class.
                checks["dataverse"] = new HealthCheckEntry
                {
                    Status = "Unhealthy",
                    Detail = $"Dataverse round trip failed ({ex.GetType().Name})."
                };
            }

            var overallStatus = checks.Values.All(c => c.Status == "Healthy") ? "Healthy" : "Unhealthy";

            return new HealthCheckResult
            {
                Status = overallStatus,
                Checks = checks
            };
        }
    }
}
