using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Functions
{
    public class HealthTrigger
    {
        private readonly IHealthCheckService _healthCheckService;
        private readonly ILogger<HealthTrigger> _logger;

        public HealthTrigger(IHealthCheckService healthCheckService, ILogger<HealthTrigger> logger)
        {
            _healthCheckService = healthCheckService;
            _logger = logger;
        }

        [Function("HealthLive")]
        public async Task<HttpResponseData> LiveAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/live")] HttpRequestData req)
        {
            var response = req.CreateResponse(_healthCheckService.IsLive() ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { status = "Healthy" }));
            return response;
        }

        [Function("HealthReady")]
        public async Task<HttpResponseData> ReadyAsync(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "health/ready")] HttpRequestData req)
        {
            var result = await _healthCheckService.CheckReadinessAsync(req.FunctionContext.CancellationToken);

            var statusCode = result.Status == "Healthy" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(result));
            return response;
        }
    }
}
