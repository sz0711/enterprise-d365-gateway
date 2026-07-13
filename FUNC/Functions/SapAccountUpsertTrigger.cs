using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using MODEL;
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
            var correlationId = HttpResponseWriter.ResolveCorrelationId(req);

            _logger.LogInformation("SapAccountUpsertHttp triggered. CorrelationId={CorrelationId}", correlationId);

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

            SapAccountWithContactsRequest sapRequest;
            try
            {
                var limitedBody = RequestBodyGuard.LimitBody(req.Body, _options.MaxRequestBytes);
                sapRequest = await JsonSerializer.DeserializeAsync<SapAccountWithContactsRequest>(limitedBody, DeserializeOptions)
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
                _logger.LogWarning(ex, "Cannot parse SAP request body. CorrelationId={CorrelationId}", correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.BadRequest, $"Invalid request: {ex.Message}", correlationId);
            }

            // Guard: contact fan-out is bounded like every other batch surface
            var contactCount = sapRequest.Contacts?.Count ?? 0;
            if (contactCount > _options.MaxBatchItems)
            {
                _logger.LogWarning(
                    "SAP request with too many contacts ({Count}, limit {Limit}). CorrelationId={CorrelationId}",
                    contactCount, _options.MaxBatchItems, correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.BadRequest,
                    $"Invalid request: Contacts count ({contactCount}) exceeds the maximum of {_options.MaxBatchItems}.",
                    correlationId);
            }

            SapMappingResult mapping;
            try
            {
                mapping = _mapper.Map(sapRequest);
            }
            catch (PayloadValidationException ex)
            {
                _logger.LogWarning(ex, "SAP payload validation failed. CorrelationId={CorrelationId}", correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.BadRequest, "Validation failed.", correlationId, ex.ValidationErrors);
            }

            var allResults = new List<UpsertResult>(1 + mapping.ContactPayloads.Count + 1);
            var ct = req.FunctionContext.CancellationToken;
            try
            {
                // Phase 1: Upsert account first so it exists for contact linking.
                var accountResult = await _upsertService.UpsertAsync(mapping.AccountPayload, ct);
                allResults.Add(accountResult);

                if (accountResult.ErrorCategory != ErrorCategory.None)
                {
                    // Short-circuit: without the account, contact upserts and the
                    // primary-contact link can only produce misleading cascade
                    // failures (or worse, a half-initialized account in phase 3).
                    _logger.LogWarning(
                        "SAP phase 1 (account) failed with {Category} — skipping contact phases. CorrelationId={CorrelationId}",
                        accountResult.ErrorCategory, correlationId);

                    return await WriteResultsAsync(req, allResults, correlationId, contactsSkipped: contactCount);
                }

                // Phase 2: Upsert contacts. The account GUID from phase 1 is wired
                // in directly — no per-contact account lookup round-trips.
                IReadOnlyList<UpsertResult> contactResults = Array.Empty<UpsertResult>();
                if (mapping.ContactPayloads.Count > 0)
                {
                    if (accountResult.Id != Guid.Empty)
                    {
                        foreach (var contactPayload in mapping.ContactPayloads)
                        {
                            contactPayload.Attributes[Contact.Fields.ParentCustomerId] =
                                new EntityReference(Account.EntityLogicalName, accountResult.Id);
                            contactPayload.Lookups = null;
                        }
                    }

                    contactResults = await _upsertService.UpsertBatchAsync(mapping.ContactPayloads, ct);
                    allResults.AddRange(contactResults);
                }

                // Phase 3: Link the primary contact to the account.
                if (mapping.PrimaryContactLinkPayload != null)
                {
                    var linkPayload = mapping.PrimaryContactLinkPayload;
                    var primaryResult = mapping.PrimaryContactIndex is int idx && idx < contactResults.Count
                        ? contactResults[idx]
                        : null;

                    if (primaryResult != null && primaryResult.ErrorCategory != ErrorCategory.None)
                    {
                        // The primary contact's state is unknown — a lookup-based
                        // link would fail or bind stale data. Report it explicitly,
                        // carrying the contact's own failure category so a purely
                        // throttled batch still surfaces as a retryable 429 rather
                        // than a manufactured 500.
                        allResults.Add(new UpsertResult
                        {
                            EntityLogicalName = Account.EntityLogicalName,
                            UpsertKey = allResults[0].UpsertKey,
                            ErrorCategory = primaryResult.ErrorCategory,
                            ErrorMessage = $"Primary contact link skipped: the primary contact upsert did not succeed ({primaryResult.ErrorCategory})."
                        });
                    }
                    else
                    {
                        // Wire the known GUIDs directly — no lookup round-trips and
                        // no ambiguity should the e-mail match multiple contacts.
                        // (Falls back to the mapper's e-mail lookup if a GUID is unavailable.)
                        if (accountResult.Id != Guid.Empty)
                            linkPayload.Id = accountResult.Id;
                        if (primaryResult != null && primaryResult.Id != Guid.Empty)
                        {
                            linkPayload.Attributes[Account.Fields.PrimaryContactId] =
                                new EntityReference(Contact.EntityLogicalName, primaryResult.Id);
                            linkPayload.Lookups = null;
                        }

                        allResults.Add(await _upsertService.UpsertAsync(linkPayload, ct));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.RequestTimeout, "Request was canceled.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled SAP upsert exception. CorrelationId={CorrelationId}", correlationId);
                return await HttpResponseWriter.WriteErrorAsync(
                    req, HttpStatusCode.InternalServerError,
                    "Internal server error while processing SAP upsert request.", correlationId);
            }

            return await WriteResultsAsync(req, allResults, correlationId, contactsSkipped: 0);
        }

        private async Task<HttpResponseData> WriteResultsAsync(
            HttpRequestData req,
            IReadOnlyList<UpsertResult> allResults,
            string correlationId,
            int contactsSkipped)
        {
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
                "SAP upsert finished. CorrelationId={CorrelationId}, Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}, ContactsSkipped={ContactsSkipped}",
                correlationId,
                allResults.Count,
                failures,
                validationFailures,
                technicalFailures,
                contactsSkipped);

            var retryAfter = statusCode == HttpStatusCode.TooManyRequests
                ? _options.RateLimitRetryDelaySeconds
                : (int?)null;

            return await HttpResponseWriter.WriteJsonAsync(req, statusCode, allResults, correlationId, retryAfter);
        }
    }
}
