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
        private static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = 32
        };

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
            var correlationId = HttpResponseWriter.ResolveCorrelationId(req);

            _logger.LogInformation("DataverseUpsertHttp triggered. CorrelationId={CorrelationId}", correlationId);

            // Guard: request body size (declared length fast path + hard read limit)
            if (RequestBodyGuard.ExceedsDeclaredLength(req, _options.MaxRequestBytes))
            {
                _logger.LogWarning(
                    "Request body too large (limit {Limit} bytes). CorrelationId={CorrelationId}",
                    _options.MaxRequestBytes, correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, (HttpStatusCode)413,
                    $"Request body exceeds the maximum allowed size of {_options.MaxRequestBytes} bytes.",
                    correlationId);
            }

            UpsertBatchRequest payload;
            try
            {
                var limitedBody = RequestBodyGuard.LimitBody(req.Body, _options.MaxRequestBytes);
                payload = await JsonSerializer.DeserializeAsync<UpsertBatchRequest>(limitedBody, DeserializeOptions)
                    ?? throw new JsonException("Request body must contain a JSON object.");
            }
            catch (RequestBodyTooLargeException ex)
            {
                _logger.LogWarning(
                    "Request body too large while reading (limit {Limit} bytes). CorrelationId={CorrelationId}",
                    _options.MaxRequestBytes, correlationId);
                return await HttpResponseWriter.WriteErrorAsync(req, (HttpStatusCode)413, ex.Message, correlationId);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Cannot parse request body. CorrelationId={CorrelationId}", correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.BadRequest, $"Invalid request: {ex.Message}", correlationId);
            }

            if (payload.Payloads == null || payload.Payloads.Count == 0)
            {
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.BadRequest,
                    "Invalid request: Payloads must contain at least one item.", correlationId);
            }

            if (payload.Payloads.Count > _options.MaxBatchItems)
            {
                _logger.LogWarning(
                    "Batch too large ({Count} items, limit {Limit}). CorrelationId={CorrelationId}",
                    payload.Payloads.Count, _options.MaxBatchItems, correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.BadRequest,
                    $"Invalid request: Payloads count ({payload.Payloads.Count}) exceeds the maximum of {_options.MaxBatchItems}.",
                    correlationId);
            }

            // Batch-level lookup depth applies to every payload without its own value.
            if (payload.MaxLookupDepth.HasValue)
            {
                foreach (var item in payload.Payloads)
                {
                    if (item != null)
                        item.MaxLookupDepth ??= payload.MaxLookupDepth;
                }
            }

            IReadOnlyList<UpsertResult> result;
            try
            {
                result = await _upsertService.UpsertBatchAsync(payload.Payloads, req.FunctionContext.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.RequestTimeout, "Request was canceled.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled upsert exception. CorrelationId={CorrelationId}", correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.InternalServerError,
                    "Internal server error while processing upsert request.", correlationId);
            }

            int failures = 0, validationFailures = 0;
            foreach (var r in result)
            {
                if (r.ErrorCategory != ErrorCategory.None)
                {
                    failures++;
                    if (r.ErrorCategory == ErrorCategory.Validation)
                        validationFailures++;
                }
            }
            var technicalFailures = failures - validationFailures;

            var statusCode = _resultMapper.DetermineBatchStatusCode(result);

            _logger.LogInformation(
                "HTTP upsert finished. CorrelationId={CorrelationId}, Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}",
                correlationId,
                result.Count,
                failures,
                validationFailures,
                technicalFailures);

            var retryAfter = statusCode == HttpStatusCode.TooManyRequests
                ? _options.RateLimitRetryDelaySeconds
                : (int?)null;

            return await HttpResponseWriter.WriteJsonAsync(req, statusCode, result, correlationId, retryAfter);
        }
    }
}
