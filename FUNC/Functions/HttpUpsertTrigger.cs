using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Functions
{
    public class HttpUpsertTrigger
    {
        private readonly IDataverseUpsertService _upsertService;
        private readonly ILogger<HttpUpsertTrigger> _logger;

        public HttpUpsertTrigger(IDataverseUpsertService upsertService, ILogger<HttpUpsertTrigger> logger)
        {
            _upsertService = upsertService;
            _logger = logger;
        }

        [Function("DataverseUpsertHttp")]
        public async Task<HttpResponseData> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "upsert")] HttpRequestData req)
        {
            var correlationId = req.Headers.TryGetValues("x-correlation-id", out var headerValues)
                ? headerValues.FirstOrDefault() ?? Guid.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N");

            _logger.LogInformation("DataverseUpsertHttp triggered. CorrelationId={CorrelationId}", correlationId);

            UpsertBatchRequest payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<UpsertBatchRequest>(req.Body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new InvalidOperationException("Invalid payload body.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot parse request body. CorrelationId={CorrelationId}", correlationId);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                badResponse.Headers.Add("x-correlation-id", correlationId);
                await badResponse.WriteStringAsync($"Invalid request: {ex.Message}");
                return badResponse;
            }

            if (payload.Payloads == null || payload.Payloads.Count == 0)
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                badResponse.Headers.Add("x-correlation-id", correlationId);
                await badResponse.WriteStringAsync("Invalid request: Payloads must contain at least one item.");
                return badResponse;
            }

            IReadOnlyList<UpsertResult> result;
            try
            {
                result = await _upsertService.UpsertBatchAsync(payload.Payloads, req.FunctionContext.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                var timeoutResponse = req.CreateResponse(HttpStatusCode.RequestTimeout);
                timeoutResponse.Headers.Add("x-correlation-id", correlationId);
                await timeoutResponse.WriteStringAsync("Request was canceled.");
                return timeoutResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled upsert exception. CorrelationId={CorrelationId}", correlationId);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                errorResponse.Headers.Add("x-correlation-id", correlationId);
                await errorResponse.WriteStringAsync("Internal server error while processing upsert request.");
                return errorResponse;
            }

            var failures = result.Count(r => !string.IsNullOrWhiteSpace(r.ErrorMessage));
            var validationFailures = result.Count(r => r.IsValidationError);
            var technicalFailures = failures - validationFailures;

            var statusCode = technicalFailures > 0
                ? HttpStatusCode.InternalServerError
                : (validationFailures > 0 ? HttpStatusCode.BadRequest : HttpStatusCode.OK);

            _logger.LogInformation(
                "HTTP upsert finished. CorrelationId={CorrelationId}, Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}",
                correlationId,
                result.Count,
                failures,
                validationFailures,
                technicalFailures);

            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("x-correlation-id", correlationId);
            await response.WriteStringAsync(JsonSerializer.Serialize(result));
            return response;
        }
    }
}
