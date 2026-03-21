namespace enterprise_d365_gateway.Interfaces
{
    /// <summary>
    /// AIMD (Additive Increase / Multiplicative Decrease) concurrency limiter.
    /// Dynamically adjusts parallelism based on Dataverse throttling signals.
    /// </summary>
    public interface IAdaptiveConcurrencyLimiter
    {
        /// <summary>Current effective parallelism limit.</summary>
        int CurrentLimit { get; }

        /// <summary>Record a successful Dataverse operation (additive increase after N successes).</summary>
        void RecordSuccess();

        /// <summary>Record a throttle / 429 response (multiplicative decrease).</summary>
        void RecordThrottle();
    }
}
