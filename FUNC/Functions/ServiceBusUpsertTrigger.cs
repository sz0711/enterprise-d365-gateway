using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Functions
{
    public class ServiceBusUpsertTrigger
    {
        private static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = 32
        };

        private readonly IDataverseUpsertService _upsertService;
        private readonly ILogger<ServiceBusUpsertTrigger> _logger;
        private readonly DataverseOptions _options;

        public ServiceBusUpsertTrigger(
            IDataverseUpsertService upsertService,
            ILogger<ServiceBusUpsertTrigger> logger,
            IOptions<DataverseOptions> options)
        {
            _upsertService = upsertService;
            _logger = logger;
            _options = options.Value;
        }

        [Function("DataverseUpsertServiceBus")]
        public async Task RunAsync([ServiceBusTrigger("%ServiceBusQueueName%", Connection = "ServiceBusConnection")] string message, FunctionContext context)
        {
            var invocationId = context.InvocationId;

            // Attempt to extract correlation-id from binding data; fall back to invocationId.
            string correlationId = invocationId;
            if (context.BindingContext?.BindingData != null
                && context.BindingContext.BindingData.TryGetValue("CorrelationId", out var cid)
                && cid != null)
            {
                correlationId = cid.ToString()!;
            }

            _logger.LogInformation("ServiceBusUpsertReceived. InvocationId={InvocationId}, CorrelationId={CorrelationId}", invocationId, correlationId);

            // Poison messages must never be silently completed (= permanent data
            // loss). Throwing lets Service Bus retry up to MaxDeliveryCount and
            // then move the message to the dead-letter queue, where it is
            // preserved for inspection and replay.
            UpsertBatchRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<UpsertBatchRequest>(message, DeserializeOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid message format — message will dead-letter. InvocationId={InvocationId}", invocationId);
                throw new InvalidOperationException("Service Bus message is not valid JSON for UpsertBatchRequest.", ex);
            }

            if (payload?.Payloads == null || payload.Payloads.Count == 0)
            {
                _logger.LogError("Empty or invalid payload — message will dead-letter. InvocationId={InvocationId}", invocationId);
                throw new InvalidOperationException("Service Bus message contains no payloads.");
            }

            if (payload.Payloads.Count > _options.MaxBatchItems)
            {
                _logger.LogError(
                    "Batch too large ({Count} items, limit {Limit}) — message will dead-letter. InvocationId={InvocationId}",
                    payload.Payloads.Count, _options.MaxBatchItems, invocationId);
                throw new InvalidOperationException(
                    $"Service Bus message batch size ({payload.Payloads.Count}) exceeds the maximum of {_options.MaxBatchItems}.");
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

            try
            {
                var results = await _upsertService.UpsertBatchAsync(payload.Payloads, context.CancellationToken);
                int failures = 0, validationFailures = 0;
                foreach (var r in results)
                {
                    if (r.ErrorCategory != ErrorCategory.None)
                    {
                        failures++;
                        if (r.ErrorCategory == ErrorCategory.Validation)
                            validationFailures++;
                    }
                }
                var technicalFailures = failures - validationFailures;

                if (failures > 0)
                {
                    _logger.LogError(
                        "ServiceBusUpsertFailed. InvocationId={InvocationId}, CorrelationId={CorrelationId}, Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}",
                        invocationId,
                        correlationId,
                        results.Count,
                        failures,
                        validationFailures,
                        technicalFailures);

                    // Upserts are idempotent, so redelivery re-applies succeeded
                    // items safely; deterministic validation failures exhaust the
                    // delivery count and dead-letter with the evidence intact.
                    throw new InvalidOperationException(
                        $"Service Bus upsert failed for {failures} item(s) ({validationFailures} validation, {technicalFailures} technical).");
                }

                _logger.LogInformation("ServiceBusUpsertSucceeded. InvocationId={InvocationId}, CorrelationId={CorrelationId}, Total={Total}", invocationId, correlationId, results.Count);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                _logger.LogError(ex, "ServiceBusUpsertException. InvocationId={InvocationId}, CorrelationId={CorrelationId}", invocationId, correlationId);
                throw;
            }
        }
    }
}
