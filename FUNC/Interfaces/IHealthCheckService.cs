namespace enterprise_d365_gateway.Interfaces
{
    public interface IHealthCheckService
    {
        bool IsLive();
        Task<HealthCheckResult> CheckReadinessAsync(CancellationToken cancellationToken = default);
    }

    public sealed class HealthCheckResult
    {
        public required string Status { get; init; }
        public required IDictionary<string, HealthCheckEntry> Checks { get; init; }
    }

    public sealed class HealthCheckEntry
    {
        public required string Status { get; init; }
        public string? Detail { get; init; }
    }
}
