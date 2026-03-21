using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Functions
{
    public class HttpUpsertTrigger
    {
        private readonly IDataverseUpsertService _upsertService;
        private readonly IResultMapper _resultMapper;
        private readonly ILogger<HttpUpsertTrigger> _logger;
        private readonly DataverseOptions _options;

        public HttpUpsertTrigger(IDataverseUpsertService upsertService, IResultMapper resultMapper, ILogger<HttpUpsertTrigger> logger, IOptions<DataverseOptions> options)
        {
            _upsertService = upsertService;
            _resultMapper = resultMapper;
            _logger = logger;
            _options = options.Value;
        }

        [Function("DataverseUpsertHttp")]
        public async Task<HttpResponseData> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "upsert")] HttpRequestData req)
        {
            var correlationId = req.Headers.TryGetValues("x-correlation-id", out var headerValues)
                ? headerValues.FirstOrDefault() ?? Guid.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N");

            _logger.LogInformation("DataverseUpsertHttp triggered. CorrelationId={CorrelationId}", correlationId);

            // Guard: request body size
            if (req.Body.CanSeek && req.Body.Length > _options.MaxRequestBytes)
            {
                _logger.LogWarning(
                    "Request body too large ({BodySize} bytes, limit {Limit}). CorrelationId={CorrelationId}",
                    req.Body.Length, _options.MaxRequestBytes, correlationId);
                var tooLargeResponse = req.CreateResponse((HttpStatusCode)413);
                tooLargeResponse.Headers.Add("x-correlation-id", correlationId);
                await tooLargeResponse.WriteStringAsync($"Request body exceeds the maximum allowed size of {_options.MaxRequestBytes} bytes.");
                return tooLargeResponse;
            }

            UpsertBatchRequest payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<UpsertBatchRequest>(req.Body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    MaxDepth = 32
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

            if (payload.Payloads.Count > _options.MaxBatchItems)
            {
                _logger.LogWarning(
                    "Batch too large ({Count} items, limit {Limit}). CorrelationId={CorrelationId}",
                    payload.Payloads.Count, _options.MaxBatchItems, correlationId);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                badResponse.Headers.Add("x-correlation-id", correlationId);
                await badResponse.WriteStringAsync($"Invalid request: Payloads count ({payload.Payloads.Count}) exceeds the maximum of {_options.MaxBatchItems}.");
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

            var failures = result.Count(r => r.ErrorCategory != ErrorCategory.None);
            var validationFailures = result.Count(r => r.ErrorCategory == ErrorCategory.Validation);
            var technicalFailures = failures - validationFailures;

            var statusCode = _resultMapper.DetermineBatchStatusCode(result);

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
