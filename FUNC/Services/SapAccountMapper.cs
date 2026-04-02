using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using MODEL;

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
                    EntityLogicalName = Account.EntityLogicalName,
                    KeyAttributes = new Dictionary<string, object?> { [Account.Fields.AccountNumber] = request.AccountNumber },
                    Attributes = new Dictionary<string, object?>(),
                    SourceSystem = "SAP",
                    Lookups = new Dictionary<string, LookupDefinition>
                    {
                        [Account.Fields.PrimaryContactId] = new LookupDefinition
                        {
                            EntityLogicalName = Contact.EntityLogicalName,
                            KeyAttributes = new Dictionary<string, object?> { [Contact.Fields.EMailAddress1] = primaryContact.Email }
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
                [Contact.Fields.FirstName] = contact.FirstName,
                [Contact.Fields.LastName] = contact.LastName
            };

            if (contact.Phone != null) attributes[Contact.Fields.Telephone1] = contact.Phone;
            if (contact.JobTitle != null) attributes[Contact.Fields.JobTitle] = contact.JobTitle;

            return new UpsertPayload
            {
                EntityLogicalName = Contact.EntityLogicalName,
                KeyAttributes = new Dictionary<string, object?> { [Contact.Fields.EMailAddress1] = contact.Email },
                Attributes = attributes,
                SourceSystem = "SAP",
                Lookups = new Dictionary<string, LookupDefinition>
                {
                    [Contact.Fields.ParentCustomerId] = new LookupDefinition
                    {
                        EntityLogicalName = Account.EntityLogicalName,
                        KeyAttributes = new Dictionary<string, object?> { [Account.Fields.AccountNumber] = accountNumber }
                    }
                }
            };
        }

        private static UpsertPayload MapAccount(SapAccountWithContactsRequest request)
        {
            var attributes = new Dictionary<string, object?>
            {
                [Account.Fields.Name] = request.Name
            };

            if (request.City != null) attributes[Account.Fields.Address1_City] = request.City;
            if (request.Street != null) attributes[Account.Fields.Address1_Line1] = request.Street;
            if (request.PostalCode != null) attributes[Account.Fields.Address1_PostalCode] = request.PostalCode;
            if (request.Country != null) attributes[Account.Fields.Address1_Country] = request.Country;
            if (request.Phone != null) attributes[Account.Fields.Telephone1] = request.Phone;
            if (request.Email != null) attributes[Account.Fields.EMailAddress1] = request.Email;
            if (request.Website != null) attributes[Account.Fields.WebSiteURL] = request.Website;

            return new UpsertPayload
            {
                EntityLogicalName = Account.EntityLogicalName,
                KeyAttributes = new Dictionary<string, object?> { [Account.Fields.AccountNumber] = request.AccountNumber },
                Attributes = attributes,
                SourceSystem = "SAP"
            };
        }
    }
}
