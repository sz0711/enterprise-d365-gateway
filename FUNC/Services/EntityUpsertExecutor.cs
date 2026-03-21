using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System.ServiceModel;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class EntityUpsertExecutor : IEntityUpsertExecutor
    {
        private readonly IDataverseServiceClientFactory _serviceClientFactory;
        private readonly ILogger<EntityUpsertExecutor> _logger;
        private readonly ResiliencePipeline _resiliencePipeline;
        private readonly TokenBucketRateLimiter _rateLimiter;
        private readonly Dictionary<string, string> _bypassStepIds;

        public EntityUpsertExecutor(
            IDataverseServiceClientFactory serviceClientFactory,
            ILogger<EntityUpsertExecutor> logger,
            IOptions<DataverseOptions> options)
        {
            _serviceClientFactory = serviceClientFactory;
            _logger = logger;
            var opts = options.Value;

            _resiliencePipeline = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = opts.MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(opts.RetryBaseDelayMs),
                    DelayGenerator = args =>
                    {
                        if (IsRateLimitException(args.Outcome.Exception))
                        {
                            return new ValueTask<TimeSpan?>(TimeSpan.FromSeconds(opts.RateLimitRetryDelaySeconds));
                        }

                        // null = use configured exponential backoff
                        return new ValueTask<TimeSpan?>((TimeSpan?)null);
                    },
                    ShouldHandle = new PredicateBuilder()
                        .Handle<TimeoutException>()
                        .Handle<HttpRequestException>()
                        .Handle<ProtocolException>(IsRateLimitException)
                        .Handle<TimeoutRejectedException>()
                        .Handle<InvalidOperationException>(ex =>
                            ex.Message.Contains("response is empty", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("ThrowIfResponseIsEmpty", StringComparison.OrdinalIgnoreCase)
                            || IsRateLimitException(ex))
                        .Handle<AggregateException>(ex =>
                            ex.InnerException is TimeoutException or HttpRequestException
                            || IsRateLimitException(ex)),
                    OnRetry = args =>
                    {
                        var exception = args.Outcome.Exception;
                        if (IsRateLimitException(exception))
                        {
                            _logger.LogWarning(
                                "Dataverse throttling detected (429/rate-limit). Retrying operation. Attempt={Attempt}, Delay={DelayMs}ms",
                                args.AttemptNumber,
                                args.RetryDelay.TotalMilliseconds);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Retrying Dataverse operation. Attempt={Attempt}, Delay={DelayMs}ms, ErrorType={ErrorType}, Error={ErrorMessage}",
                                args.AttemptNumber,
                                args.RetryDelay.TotalMilliseconds,
                                exception?.GetType().Name,
                                exception?.Message);
                        }
                        return ValueTask.CompletedTask;
                    }
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = opts.CircuitBreakerFailureThreshold,
                    SamplingDuration = TimeSpan.FromSeconds(opts.CircuitBreakerSamplingDurationSeconds),
                    BreakDuration = TimeSpan.FromSeconds(opts.CircuitBreakerBreakDurationSeconds),
                    ShouldHandle = new PredicateBuilder()
                        .Handle<TimeoutException>()
                        .Handle<HttpRequestException>()
                        .Handle<ProtocolException>(IsRateLimitException)
                        .Handle<TimeoutRejectedException>()
                        .Handle<InvalidOperationException>(ex =>
                            ex.Message.Contains("response is empty", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("ThrowIfResponseIsEmpty", StringComparison.OrdinalIgnoreCase)
                            || IsRateLimitException(ex))
                        .Handle<AggregateException>(IsRateLimitException),
                    OnOpened = args =>
                    {
                        _logger.LogError(
                            "Circuit breaker OPENED. Dataverse operations rejected for {BreakDuration}s",
                            opts.CircuitBreakerBreakDurationSeconds);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = _ =>
                    {
                        _logger.LogInformation("Circuit breaker CLOSED. Dataverse operations resumed.");
                        return ValueTask.CompletedTask;
                    },
                    OnHalfOpened = _ =>
                    {
                        _logger.LogInformation("Circuit breaker HALF-OPEN. Testing Dataverse connectivity.");
                        return ValueTask.CompletedTask;
                    }
                })
                .AddTimeout(TimeSpan.FromSeconds(opts.TimeoutPerOperationSeconds))
                .Build();

            _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = opts.MaxRequestsPerSecond,
                TokensPerPeriod = opts.MaxRequestsPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 1024,
                AutoReplenishment = true
            });

            _bypassStepIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in opts.BypassPluginStepIds)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                    _bypassStepIds[kvp.Key] = kvp.Value.Trim();
            }

            if (_bypassStepIds.Count > 0)
            {
                foreach (var kvp in _bypassStepIds)
                    _logger.LogInformation(
                        "Plugin bypass enabled for entity '{Entity}' with step IDs: {StepIds}",
                        kvp.Key, kvp.Value);
            }
        }

        public async Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken = default)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
                throw new InvalidOperationException("Rate limit exceeded for Dataverse operations.");

            var service = await _serviceClientFactory.GetOrCreateServiceAsync(cancellationToken);

            if (_bypassStepIds.TryGetValue(entity.LogicalName, out var createStepIds))
            {
                return await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    var request = new CreateRequest { Target = entity };
                    request.Parameters.Add("BypassBusinessLogicExecutionStepIds", createStepIds);
                    var response = (CreateResponse)await service.ExecuteAsync(request, ct);
                    return response.id;
                }, cancellationToken);
            }

            return await _resiliencePipeline.ExecuteAsync(
                async ct => await service.CreateAsync(entity, ct),
                cancellationToken);
        }

        public async Task UpdateAsync(Entity entity, CancellationToken cancellationToken = default)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
                throw new InvalidOperationException("Rate limit exceeded for Dataverse operations.");

            var service = await _serviceClientFactory.GetOrCreateServiceAsync(cancellationToken);

            if (_bypassStepIds.TryGetValue(entity.LogicalName, out var updateStepIds))
            {
                await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    var request = new UpdateRequest { Target = entity };
                    request.Parameters.Add("BypassBusinessLogicExecutionStepIds", updateStepIds);
                    await service.ExecuteAsync(request, ct);
                }, cancellationToken);
                return;
            }

            await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                await service.UpdateAsync(entity, ct);
            }, cancellationToken);
        }

        public async Task<EntityCollection> RetrieveMultipleAsync(QueryExpression query, CancellationToken cancellationToken = default)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
                throw new InvalidOperationException("Rate limit exceeded for Dataverse operations.");

            var service = await _serviceClientFactory.GetOrCreateServiceAsync(cancellationToken);

            return await _resiliencePipeline.ExecuteAsync(
                async ct => await service.RetrieveMultipleAsync(query, ct),
                cancellationToken);
        }

        private static bool IsRateLimitException(Exception? exception)
        {
            if (exception == null)
            {
                return false;
            }

            if (IsRateLimitMessage(exception.Message))
            {
                return true;
            }

            if (exception is AggregateException aggregate)
            {
                return aggregate.Flatten().InnerExceptions.Any(IsRateLimitException);
            }

            return exception.InnerException is not null && IsRateLimitException(exception.InnerException);
        }

        private static bool IsRateLimitMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("throttl", StringComparison.OrdinalIgnoreCase);
        }

    }
}
