using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    /// <summary>
    /// AIMD concurrency limiter: halves on throttle, increments by 1 after a streak of successes.
    /// Thread-safe via Interlocked operations.
    /// </summary>
    public sealed class AdaptiveConcurrencyLimiter : IAdaptiveConcurrencyLimiter
    {
        private readonly int _minLimit;
        private readonly int _maxLimit;
        private readonly int _successesBeforeIncrease;
        private readonly ILogger<AdaptiveConcurrencyLimiter> _logger;

        private int _currentLimit;
        private int _consecutiveSuccesses;

        public AdaptiveConcurrencyLimiter(
            ILogger<AdaptiveConcurrencyLimiter> logger,
            IOptions<DataverseOptions> options)
        {
            var opts = options.Value;
            _minLimit = opts.MinDegreeOfParallelism;
            _maxLimit = opts.MaxDegreeOfParallelism;
            _successesBeforeIncrease = opts.AdaptiveConcurrencySuccessThreshold;
            _logger = logger;

            _currentLimit = _maxLimit;
        }

        public int CurrentLimit => Volatile.Read(ref _currentLimit);

        public void RecordSuccess()
        {
            var successes = Interlocked.Increment(ref _consecutiveSuccesses);
            if (successes >= _successesBeforeIncrease)
            {
                Interlocked.Exchange(ref _consecutiveSuccesses, 0);

                int snapshot, next;
                do
                {
                    snapshot = Volatile.Read(ref _currentLimit);
                    if (snapshot >= _maxLimit) return;
                    next = Math.Min(snapshot + 1, _maxLimit);
                }
                while (Interlocked.CompareExchange(ref _currentLimit, next, snapshot) != snapshot);

                _logger.LogInformation(
                    "Adaptive concurrency increased: {Old} -> {New} (max {Max})",
                    snapshot, next, _maxLimit);
            }
        }

        public void RecordThrottle()
        {
            Interlocked.Exchange(ref _consecutiveSuccesses, 0);

            int snapshot, next;
            do
            {
                snapshot = Volatile.Read(ref _currentLimit);
                next = Math.Max(_minLimit, snapshot / 2);
                if (next == snapshot) return; // already at minimum
            }
            while (Interlocked.CompareExchange(ref _currentLimit, next, snapshot) != snapshot);

            _logger.LogWarning(
                "Adaptive concurrency decreased (throttle): {Old} -> {New} (min {Min})",
                snapshot, next, _minLimit);
        }
    }
}
