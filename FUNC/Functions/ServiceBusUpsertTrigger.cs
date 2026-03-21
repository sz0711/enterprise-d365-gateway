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
            _logger.LogInformation("DataverseUpsertServiceBus message received. InvocationId={InvocationId}", invocationId);

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
                            "Service Bus upsert contains validation failures only. InvocationId={InvocationId}, Total={Total}, ValidationFailed={ValidationFailed}",
                            invocationId,
                            results.Count,
                            validationFailures);
                        return;
                    }

                    _logger.LogError(
                        "Service Bus upsert completed with technical failures. InvocationId={InvocationId}, Total={Total}, Failed={Failed}, ValidationFailed={ValidationFailed}, TechnicalFailed={TechnicalFailed}",
                        invocationId,
                        results.Count,
                        failures,
                        validationFailures,
                        technicalFailures);

                    throw new InvalidOperationException($"Service Bus upsert failed for {technicalFailures} technical item(s).");
                }

                _logger.LogInformation("Service Bus upsert succeeded. InvocationId={InvocationId}, Total={Total}", invocationId, results.Count);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid message format. Discarding message. InvocationId={InvocationId}", invocationId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing ServiceBus upsert message. InvocationId={InvocationId}", invocationId);
                throw;
            }
        }
    }
}
