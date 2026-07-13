using System.Net;
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
            var live = _healthCheckService.IsLive();
            var correlationId = HttpResponseWriter.ResolveCorrelationId(req);
            return await HttpResponseWriter.WriteJsonAsync(
                req,
                live ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                new { Status = live ? "Healthy" : "Unhealthy" },
                correlationId);
        }

        [Function("HealthReady")]
        public async Task<HttpResponseData> ReadyAsync(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "health/ready")] HttpRequestData req)
        {
            var correlationId = HttpResponseWriter.ResolveCorrelationId(req);
            var result = await _healthCheckService.CheckReadinessAsync(req.FunctionContext.CancellationToken);

            var statusCode = result.Status == "Healthy" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            return await HttpResponseWriter.WriteJsonAsync(req, statusCode, result, correlationId);
        }
    }
}
