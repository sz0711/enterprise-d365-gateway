using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IErrorClassifier
    {
        ErrorCategory Classify(Exception exception);

        /// <summary>
        /// True when the exception indicates a key conflict (duplicate record /
        /// record does not exist) — the failure modes a stale cached GUID or a
        /// create race can produce. Used to target the orchestrator's
        /// invalidate-and-retry pass at exactly these cases.
        /// </summary>
        bool IsKeyConflict(Exception exception);
    }
}
