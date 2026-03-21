using System.Runtime.Serialization;

namespace enterprise_d365_gateway.Models
{
    [DataContract]
    public class UpsertPayload
    {
        [DataMember(IsRequired = true)]
        public required string EntityLogicalName { get; set; }

        [DataMember(IsRequired = false)]
        public Guid? Id { get; set; }

        [DataMember(IsRequired = true)]
        public required IDictionary<string, object?> Attributes { get; set; }

        [DataMember(IsRequired = false)]
        public string? SourceSystem { get; set; }

        [DataMember(IsRequired = false)]
        public string? ExternalIdAttribute { get; set; }

        [DataMember(IsRequired = false)]
        public object? ExternalIdValue { get; set; }

        [DataMember(IsRequired = false)]
        public IDictionary<string, LookupDefinition>? Lookups { get; set; }
    }

    [DataContract]
    public class LookupDefinition
    {
        [DataMember(IsRequired = true)]
        public required string EntityLogicalName { get; set; }

        [DataMember(IsRequired = true)]
        public required IDictionary<string, object?> AlternateKeyAttributes { get; set; }

        [DataMember(IsRequired = false)]
        public bool CreateIfNotExists { get; set; } = false;

        [DataMember(IsRequired = false)]
        public IDictionary<string, object?>? CreateAttributes { get; set; }
    }

    [DataContract]
    public class UpsertBatchRequest
    {
        [DataMember(IsRequired = true)]
        public required IList<UpsertPayload> Payloads { get; set; }
    }

    [DataContract]
    public class UpsertResult
    {
        [DataMember]
        public Guid Id { get; set; }

        [DataMember]
        public bool Created { get; set; }

        [DataMember]
        public string? EntityLogicalName { get; set; }

        [DataMember]
        public string? ErrorMessage { get; set; }

        [DataMember]
        public bool IsValidationError { get; set; }

        [DataMember]
        public IList<string>? ValidationErrors { get; set; }
    }
}
