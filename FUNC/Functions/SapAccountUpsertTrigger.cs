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
    public class SapAccountUpsertTrigger
    {
        private static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = 32
        };

        private readonly ISapAccountMapper _mapper;
        private readonly IDataverseUpsertService _upsertService;
        private readonly IResultMapper _resultMapper;
        private readonly ILogger<SapAccountUpsertTrigger> _logger;
        private readonly DataverseOptions _options;

        public SapAccountUpsertTrigger(
            ISapAccountMapper mapper,
            IDataverseUpsertService upsertService,
            IResultMapper resultMapper,
            ILogger<SapAccountUpsertTrigger> logger,
            IOptions<DataverseOptions> options)
        {
            _mapper = mapper;
            _upsertService = upsertService;
            _resultMapper = resultMapper;
            _logger = logger;
            _options = options.Value;
        }

        [Function("SapAccountUpsertHttp")]
        public async Task<HttpResponseData> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sap/account-with-contacts")] HttpRequestData req)
        {
            var correlationId = req.Headers.TryGetValues("x-correlation-id", out var headerValues)
                ? headerValues.FirstOrDefault() ?? Guid.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N");

            _logger.LogInformation("SapAccountUpsertHttp triggered. CorrelationId={CorrelationId}", correlationId);

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

            SapAccountWithContactsRequest sapRequest;
            try
            {
                sapRequest = await JsonSerializer.DeserializeAsync<SapAccountWithContactsRequest>(req.Body, DeserializeOptions)
                    ?? throw new InvalidOperationException("Invalid payload body.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot parse SAP request body. CorrelationId={CorrelationId}", correlationId);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                badResponse.Headers.Add("x-correlation-id", correlationId);
                await badResponse.WriteStringAsync($"Invalid request: {ex.Message}");
                return badResponse;
            }

            SapMappingResult mapping;
            try
            {
                mapping = _mapper.Map(sapRequest);
            }
            catch (PayloadValidationException ex)
            {
                _logger.LogWarning(ex, "SAP payload validation failed. CorrelationId={CorrelationId}", correlationId);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                badResponse.Headers.Add("x-correlation-id", correlationId);
                await badResponse.WriteStringAsync($"Validation failed: {ex.Message}");
                return badResponse;
            }

            var allResults = new List<UpsertResult>();
            var ct = req.FunctionContext.CancellationToken;
            try
            {
                // Phase 1: Upsert account first so it exists for contact lookups
                allResults.Add(await _upsertService.UpsertAsync(mapping.AccountPayload, ct));

                // Phase 2: Upsert contacts (parentcustomerid lookup can now find the account)
                if (mapping.ContactPayloads.Count > 0)
                {
                    var contactResults = await _upsertService.UpsertBatchAsync(mapping.ContactPayloads, ct);
                    allResults.AddRange(contactResults);
                }

                // Phase 3: Link primary contact to account (contact now exists from phase 2)
                if (mapping.PrimaryContactLinkPayload != null)
                {
                    allResults.Add(await _upsertService.UpsertAsync(mapping.PrimaryContactLinkPayload, ct));
                }
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
                _logger.LogError(ex, "Unhandled SAP upsert exception. CorrelationId={CorrelationId}", correlationId);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                errorResponse.Headers.Add("x-correlation-id", correlationId);
                await errorResponse.WriteStringAsync("Internal server error while processing SAP upsert request.");
                return errorResponse;
            }

            int failures = 0, validationFailures = 0;
            foreach (var r in allResults)
            {
                if (r.ErrorCategory != ErrorCategory.None)
                {
                    failures++;
                    if (r.ErrorCategory == ErrorCategory.Validation)
                        validationFailures++;
                }
            }
            var technicalFailures = failures - validationFailures;

            var statusCode = _resultMapper.DetermineBatchStatusCode(allResults);

            _logger.LogInformation(
                "SAP upsert finished. CorrelationId={CorrelationId}, Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}",
                correlationId,
                allResults.Count,
                failures,
                validationFailures,
                technicalFailures);

            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("x-correlation-id", correlationId);
            await response.WriteStringAsync(JsonSerializer.Serialize(allResults));
            return response;
        }
    }
}
