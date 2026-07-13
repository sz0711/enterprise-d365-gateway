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
    public class EntityUpsertExecutor : IEntityUpsertExecutor, IDisposable
    {
        private readonly IDataverseServiceClientFactory _serviceClientFactory;
        private readonly ILogger<EntityUpsertExecutor> _logger;
        private readonly IAdaptiveConcurrencyLimiter _concurrencyLimiter;

        /// <summary>Circuit breaker + per-attempt timeout, shared by all operations.</summary>
        private readonly ResiliencePipeline _innerPipeline;

        /// <summary>Full retry (timeouts, connection faults, throttling) — safe for idempotent operations.</summary>
        private readonly ResiliencePipeline _idempotentPipeline;

        /// <summary>
        /// Narrow retry for Create: only failures where the request provably never
        /// executed (throttle rejections, connection/client setup failures) are
        /// retried. A Create that timed out may have succeeded server-side —
        /// retrying it would produce duplicate records that permanently poison
        /// the alternate key. Such failures surface as Transient to the caller,
        /// whose re-submission is safe because the orchestrator re-resolves the
        /// key attributes first.
        /// </summary>
        private readonly ResiliencePipeline _createPipeline;

        private readonly TokenBucketRateLimiter _rateLimiter;
        private readonly Dictionary<string, string> _bypassStepIds;
        private readonly int _maxRateLimitDelaySeconds;

        public EntityUpsertExecutor(
            IDataverseServiceClientFactory serviceClientFactory,
            ILogger<EntityUpsertExecutor> logger,
            IAdaptiveConcurrencyLimiter concurrencyLimiter,
            IOptions<DataverseOptions> options)
        {
            _serviceClientFactory = serviceClientFactory;
            _logger = logger;
            _concurrencyLimiter = concurrencyLimiter;
            var opts = options.Value;
            _maxRateLimitDelaySeconds = opts.RateLimitRetryDelaySeconds;

            _innerPipeline = new ResiliencePipelineBuilder()
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
                        .Handle<FaultException<OrganizationServiceFault>>(IsRateLimitException)
                        .Handle<InvalidOperationException>(ex =>
                            IsEmptyResponseMessage(ex.Message)
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

            _idempotentPipeline = new ResiliencePipelineBuilder()
                .AddRetry(BuildRetryOptions(opts, createSafeOnly: false))
                .Build();

            _createPipeline = new ResiliencePipelineBuilder()
                .AddRetry(BuildRetryOptions(opts, createSafeOnly: true))
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

        private RetryStrategyOptions BuildRetryOptions(DataverseOptions opts, bool createSafeOnly)
        {
            var predicates = new PredicateBuilder()
                .Handle<ProtocolException>(IsRateLimitException)
                .Handle<FaultException<OrganizationServiceFault>>(IsRateLimitException)
                .Handle<InvalidOperationException>(ex =>
                    IsRateLimitException(ex)
                    || IsClientSetupFailureMessage(ex.Message)
                    || (!createSafeOnly && IsEmptyResponseMessage(ex.Message)))
                .Handle<AggregateException>(ex =>
                    IsRateLimitException(ex)
                    || (!createSafeOnly && ex.InnerException is TimeoutException or HttpRequestException));

            if (!createSafeOnly)
            {
                predicates = predicates
                    .Handle<TimeoutException>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>();
            }

            return new RetryStrategyOptions
            {
                MaxRetryAttempts = opts.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(opts.RetryBaseDelayMs),
                DelayGenerator = args =>
                {
                    if (IsRateLimitException(args.Outcome.Exception))
                    {
                        // Honor the server's Retry-After when present, capped by
                        // the configured maximum throttle delay.
                        var delay = TryGetRetryAfter(args.Outcome.Exception, out var retryAfter)
                            ? TimeSpan.FromSeconds(Math.Min(retryAfter.TotalSeconds, _maxRateLimitDelaySeconds))
                            : TimeSpan.FromSeconds(_maxRateLimitDelaySeconds);
                        return new ValueTask<TimeSpan?>(delay);
                    }

                    // null = use configured exponential backoff
                    return new ValueTask<TimeSpan?>((TimeSpan?)null);
                },
                ShouldHandle = predicates,
                OnRetry = args =>
                {
                    var exception = args.Outcome.Exception;
                    if (IsRateLimitException(exception))
                    {
                        _concurrencyLimiter.RecordThrottle();
                        _logger.LogWarning(
                            "Dataverse throttling detected (429/rate-limit). Retrying operation. Attempt={Attempt}, Delay={DelayMs}ms, Concurrency={Concurrency}",
                            args.AttemptNumber,
                            args.RetryDelay.TotalMilliseconds,
                            _concurrencyLimiter.CurrentLimit);
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
            };
        }

        public async Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken = default)
        {
            var bypass = _bypassStepIds.TryGetValue(entity.LogicalName, out var stepIds);

            var id = await _createPipeline.ExecuteAsync(async ct =>
            {
                var service = await AcquireServiceAsync(ct);
                if (bypass)
                {
                    return await _innerPipeline.ExecuteAsync(async innerCt =>
                    {
                        var request = new CreateRequest { Target = entity };
                        request.Parameters.Add("BypassBusinessLogicExecutionStepIds", stepIds!);
                        var response = (CreateResponse)await service.ExecuteAsync(request, innerCt);
                        return response.id;
                    }, ct);
                }

                return await _innerPipeline.ExecuteAsync(
                    async innerCt => await service.CreateAsync(entity, innerCt),
                    ct);
            }, cancellationToken);

            _concurrencyLimiter.RecordSuccess();
            return id;
        }

        public async Task UpdateAsync(Entity entity, CancellationToken cancellationToken = default)
        {
            var bypass = _bypassStepIds.TryGetValue(entity.LogicalName, out var stepIds);

            await _idempotentPipeline.ExecuteAsync(async ct =>
            {
                var service = await AcquireServiceAsync(ct);
                if (bypass)
                {
                    await _innerPipeline.ExecuteAsync(async innerCt =>
                    {
                        var request = new UpdateRequest { Target = entity };
                        request.Parameters.Add("BypassBusinessLogicExecutionStepIds", stepIds!);
                        await service.ExecuteAsync(request, innerCt);
                    }, ct);
                    return;
                }

                await _innerPipeline.ExecuteAsync(async innerCt =>
                {
                    await service.UpdateAsync(entity, innerCt);
                }, ct);
            }, cancellationToken);

            _concurrencyLimiter.RecordSuccess();
        }

        public async Task<EntityCollection> RetrieveMultipleAsync(QueryExpression query, CancellationToken cancellationToken = default)
        {
            var result = await _idempotentPipeline.ExecuteAsync(async ct =>
            {
                var service = await AcquireServiceAsync(ct);
                return await _innerPipeline.ExecuteAsync(
                    async innerCt => await service.RetrieveMultipleAsync(query, innerCt),
                    ct);
            }, cancellationToken);

            _concurrencyLimiter.RecordSuccess();
            return result;
        }

        /// <summary>
        /// Consumes one rate-limiter token and resolves the service client.
        /// Runs once per attempt (inside the retry strategy) so retries are
        /// rate-limited too and a broken client is re-created between attempts.
        /// </summary>
        private async Task<Microsoft.PowerPlatform.Dataverse.Client.IOrganizationServiceAsync2> AcquireServiceAsync(CancellationToken cancellationToken)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
                throw new InvalidOperationException("Rate limit exceeded for Dataverse operations.");

            return await _serviceClientFactory.GetOrCreateServiceAsync(cancellationToken);
        }

        public void Dispose()
        {
            _rateLimiter.Dispose();
        }

        internal static bool IsRateLimitException(Exception? exception)
        {
            if (exception == null)
            {
                return false;
            }

            if (exception is FaultException<OrganizationServiceFault> fault
                && DataverseErrorCodes.IsThrottling(fault.Detail?.ErrorCode ?? 0))
            {
                return true;
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

        internal static bool TryGetRetryAfter(Exception? exception, out TimeSpan retryAfter)
        {
            retryAfter = default;
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                if (current is FaultException<OrganizationServiceFault> fault
                    && fault.Detail?.ErrorDetails != null
                    && fault.Detail.ErrorDetails.TryGetValue("Retry-After", out var value))
                {
                    switch (value)
                    {
                        case TimeSpan ts when ts > TimeSpan.Zero:
                            retryAfter = ts;
                            return true;
                        case string s when TimeSpan.TryParse(s, out var parsed) && parsed > TimeSpan.Zero:
                            retryAfter = parsed;
                            return true;
                        case int seconds when seconds > 0:
                            retryAfter = TimeSpan.FromSeconds(seconds);
                            return true;
                    }
                }

                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.Flatten().InnerExceptions)
                    {
                        if (TryGetRetryAfter(inner, out retryAfter))
                            return true;
                    }
                }
            }

            return false;
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

        private static bool IsEmptyResponseMessage(string message)
            => message.Contains("response is empty", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ThrowIfResponseIsEmpty", StringComparison.OrdinalIgnoreCase);

        private static bool IsClientSetupFailureMessage(string message)
            => message.Contains("Failed to connect to Dataverse", StringComparison.OrdinalIgnoreCase);
    }
}
