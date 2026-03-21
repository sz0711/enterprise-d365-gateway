using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Functions
{
    public class ServiceBusUpsertTrigger
    {
        private readonly IDataverseUpsertService _upsertService;
        private readonly ILogger<ServiceBusUpsertTrigger> _logger;

        public ServiceBusUpsertTrigger(IDataverseUpsertService upsertService, ILogger<ServiceBusUpsertTrigger> logger)
        {
            _upsertService = upsertService;
            _logger = logger;
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

            UpsertBatchRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<UpsertBatchRequest>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (payload?.Payloads == null || payload.Payloads.Count == 0)
                {
                    _logger.LogWarning("Empty or invalid payload received, ignoring message. InvocationId={InvocationId}", invocationId);
                    return;
                }

                var results = await _upsertService.UpsertBatchAsync(payload.Payloads, context.CancellationToken);
                var failures = results.Count(r => r.ErrorCategory != ErrorCategory.None);
                var validationFailures = results.Count(r => r.ErrorCategory == ErrorCategory.Validation);
                var technicalFailures = failures - validationFailures;

                if (failures > 0)
                {
                    if (technicalFailures == 0)
                    {
                        _logger.LogWarning(
                            "ServiceBusUpsertValidationOnly. InvocationId={InvocationId}, CorrelationId={CorrelationId}, Total={Total}, ValidationFailed={ValidationFailed}",
                            invocationId,
                            correlationId,
                            results.Count,
                            validationFailures);
                        return;
                    }

                    _logger.LogError(
                        "ServiceBusUpsertFailed. InvocationId={InvocationId}, CorrelationId={CorrelationId}, Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}",
                        invocationId,
                        correlationId,
                        results.Count,
                        failures,
                        validationFailures,
                        technicalFailures);

                    throw new InvalidOperationException($"Service Bus upsert failed for {technicalFailures} technical item(s).");
                }

                _logger.LogInformation("ServiceBusUpsertSucceeded. InvocationId={InvocationId}, CorrelationId={CorrelationId}, Total={Total}", invocationId, correlationId, results.Count);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid message format. Discarding message. InvocationId={InvocationId}", invocationId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ServiceBusUpsertException. InvocationId={InvocationId}, CorrelationId={CorrelationId}", invocationId, correlationId);
                throw;
            }
        }
    }
}
