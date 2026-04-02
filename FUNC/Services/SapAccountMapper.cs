using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class SapAccountMapper : ISapAccountMapper
    {
        public SapMappingResult Map(SapAccountWithContactsRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.AccountNumber))
                throw new PayloadValidationException(new[] { "AccountNumber is required." });
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new PayloadValidationException(new[] { "Name is required." });

            var result = new SapMappingResult
            {
                AccountPayload = MapAccount(request)
            };

            if (request.Contacts != null)
            {
                foreach (var contact in request.Contacts)
                {
                    result.ContactPayloads.Add(MapContact(contact, request.AccountNumber));
                }
            }

            var primaryContact = request.Contacts?.FirstOrDefault(c => c.IsPrimary);
            if (primaryContact != null)
            {
                result.PrimaryContactLinkPayload = new UpsertPayload
                {
                    EntityLogicalName = "account",
                    KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = request.AccountNumber },
                    Attributes = new Dictionary<string, object?>(),
                    SourceSystem = "SAP",
                    Lookups = new Dictionary<string, LookupDefinition>
                    {
                        ["primarycontactid"] = new LookupDefinition
                        {
                            EntityLogicalName = "contact",
                            KeyAttributes = new Dictionary<string, object?> { ["emailaddress1"] = primaryContact.Email }
                        }
                    }
                };
            }

            return result;
        }

        private static UpsertPayload MapContact(SapContact contact, string accountNumber)
        {
            var attributes = new Dictionary<string, object?>
            {
                ["firstname"] = contact.FirstName,
                ["lastname"] = contact.LastName
            };

            if (contact.Phone != null) attributes["telephone1"] = contact.Phone;
            if (contact.JobTitle != null) attributes["jobtitle"] = contact.JobTitle;

            return new UpsertPayload
            {
                EntityLogicalName = "contact",
                KeyAttributes = new Dictionary<string, object?> { ["emailaddress1"] = contact.Email },
                Attributes = attributes,
                SourceSystem = "SAP",
                Lookups = new Dictionary<string, LookupDefinition>
                {
                    ["parentcustomerid"] = new LookupDefinition
                    {
                        EntityLogicalName = "account",
                        KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = accountNumber }
                    }
                }
            };
        }

        private static UpsertPayload MapAccount(SapAccountWithContactsRequest request)
        {
            var attributes = new Dictionary<string, object?>
            {
                ["name"] = request.Name
            };

            if (request.City != null) attributes["address1_city"] = request.City;
            if (request.Street != null) attributes["address1_line1"] = request.Street;
            if (request.PostalCode != null) attributes["address1_postalcode"] = request.PostalCode;
            if (request.Country != null) attributes["address1_country"] = request.Country;
            if (request.Phone != null) attributes["telephone1"] = request.Phone;
            if (request.Email != null) attributes["emailaddress1"] = request.Email;
            if (request.Website != null) attributes["websiteurl"] = request.Website;

            return new UpsertPayload
            {
                EntityLogicalName = "account",
                KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = request.AccountNumber },
                Attributes = attributes,
                SourceSystem = "SAP"
            };
        }
    }
}
