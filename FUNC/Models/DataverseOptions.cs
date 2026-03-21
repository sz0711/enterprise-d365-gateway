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

        [Range(0, 10)]
        public int MaxRetries { get; set; } = 4;
    }
}
