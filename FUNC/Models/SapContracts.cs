namespace enterprise_d365_gateway.Models
{
    public class SapAccountWithContactsRequest
    {
        public required string AccountNumber { get; set; }
        public required string Name { get; set; }
        public string? City { get; set; }
        public string? Street { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }
        public IList<SapContact>? Contacts { get; set; }
    }

    public class SapContact
    {
        public required string Email { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public string? Phone { get; set; }
        public string? JobTitle { get; set; }
        public bool IsPrimary { get; set; }
    }

    public class SapMappingResult
    {
        public required UpsertPayload AccountPayload { get; set; }
        public IList<UpsertPayload> ContactPayloads { get; set; } = new List<UpsertPayload>();
        public UpsertPayload? PrimaryContactLinkPayload { get; set; }
    }
}
