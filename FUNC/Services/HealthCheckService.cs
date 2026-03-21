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

            // Dataverse connectivity
            try
            {
                var client = await _clientFactory.GetOrCreateServiceAsync(cancellationToken);
                checks["dataverse"] = new HealthCheckEntry { Status = "Healthy", Detail = "ServiceClient ready." };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Readiness check: Dataverse connectivity failed.");
                checks["dataverse"] = new HealthCheckEntry { Status = "Unhealthy", Detail = ex.Message };
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
