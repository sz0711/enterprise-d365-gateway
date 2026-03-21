using System.ComponentModel.DataAnnotations;

namespace enterprise_d365_gateway.Models
{
    public sealed class DataverseOptions
    {
        [Required]
        public string Url { get; set; } = string.Empty;
        public string? UserAssignedManagedIdentityClientId { get; set; }

        [Range(1, 5000)]
        public int MaxRequestsPerSecond { get; set; } = 300;

        [Range(1, 128)]
        public int MaxDegreeOfParallelism { get; set; } = 8;

        // Adaptive Concurrency (AIMD)
        public bool AdaptiveConcurrencyEnabled { get; set; } = true;

        [Range(1, 64)]
        public int MinDegreeOfParallelism { get; set; } = 1;

        [Range(1, 500)]
        public int AdaptiveConcurrencySuccessThreshold { get; set; } = 20;

        [Range(0, 10)]
        public int MaxRetries { get; set; } = 4;

        // Resilience
        [Range(50, 5000)]
        public int RetryBaseDelayMs { get; set; } = 200;

        [Range(5, 900)]
        public int RateLimitRetryDelaySeconds { get; set; } = 300;

        [Range(5, 300)]
        public int TimeoutPerOperationSeconds { get; set; } = 30;

        [Range(3, 100)]
        public int CircuitBreakerFailureThreshold { get; set; } = 10;

        [Range(10, 300)]
        public int CircuitBreakerSamplingDurationSeconds { get; set; } = 60;

        [Range(5, 300)]
        public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;

        // Cache
        [Range(1, 1440)]
        public int CacheSlidingExpirationMinutes { get; set; } = 120;

        [Range(1, 1440)]
        public int CacheAbsoluteExpirationMinutes { get; set; } = 360;

        [Range(1, 90)]
        public int CacheMemoryBudgetPercent { get; set; } = 20;

        [Range(16, 8192)]
        public int CacheMemoryBudgetMinMb { get; set; } = 64;

        [Range(16, 8192)]
        public int CacheMemoryBudgetMaxMb { get; set; } = 512;

        [Range(32, 4096)]
        public long CacheEntrySizeBytes { get; set; } = 128;

        // Lookup
        [Range(1, 10)]
        public int MaxLookupDepth { get; set; } = 3;

        [Range(5, 300)]
        public int LookupTimeoutSeconds { get; set; } = 60;

        // Request limits
        [Range(1, 10_000)]
        public int MaxBatchItems { get; set; } = 1000;

        [Range(1024, 50 * 1024 * 1024)]
        public long MaxRequestBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

        // Plugin bypass
        /// <summary>
        /// Per-entity map of plugin step registration GUIDs to bypass.
        /// Key = entity logical name (case-insensitive), Value = comma-separated step IDs.
        /// Requires prvBypassCustomBusinessLogic privilege.
        /// Configured via Dataverse:BypassPluginStepIds:account = "guid1,guid2" etc.
        /// </summary>
        public Dictionary<string, string> BypassPluginStepIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
