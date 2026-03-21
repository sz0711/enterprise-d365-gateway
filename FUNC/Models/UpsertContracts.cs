namespace enterprise_d365_gateway.Models
{
    public class UpsertPayload
    {
        public required string EntityLogicalName { get; set; }
        public string? UpsertKey { get; set; }
        public Guid? Id { get; set; }
        public required IDictionary<string, object?> Attributes { get; set; }
        public string? SourceSystem { get; set; }
        public string? ExternalIdAttribute { get; set; }
        public object? ExternalIdValue { get; set; }
        public IDictionary<string, LookupDefinition>? Lookups { get; set; }
        public int? MaxLookupDepth { get; set; }
    }

    public class LookupDefinition
    {
        public required string EntityLogicalName { get; set; }
        public string? UpsertKey { get; set; }
        public required IDictionary<string, object?> AlternateKeyAttributes { get; set; }
        public bool CreateIfNotExists { get; set; }
        public IDictionary<string, object?>? CreateAttributes { get; set; }
        public IDictionary<string, LookupDefinition>? NestedLookups { get; set; }
        public int? MaxDepth { get; set; }
    }

    public class UpsertBatchRequest
    {
        public required IList<UpsertPayload> Payloads { get; set; }
        public int? MaxLookupDepth { get; set; }
    }

    public enum ErrorCategory
    {
        None = 0,
        Validation = 1,
        Transient = 2,
        Permanent = 3,
        Throttling = 4,
        Cancellation = 5
    }

    public class UpsertResult
    {
        public Guid Id { get; set; }
        public bool Created { get; set; }
        public string? EntityLogicalName { get; set; }
        public string? UpsertKey { get; set; }
        public string? ErrorMessage { get; set; }
        public ErrorCategory ErrorCategory { get; set; }
        public IList<string>? ValidationErrors { get; set; }
        public IList<LookupTrace>? LookupTraces { get; set; }
    }

    public class LookupTrace
    {
        public required string AttributeName { get; set; }
        public required string EntityLogicalName { get; set; }
        public Guid? ResolvedId { get; set; }
        public bool WasCreated { get; set; }
        public int Depth { get; set; }
        public IList<LookupTrace>? NestedTraces { get; set; }
    }
}
