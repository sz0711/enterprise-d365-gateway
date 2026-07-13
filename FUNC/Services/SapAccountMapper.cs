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

            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(request.AccountNumber))
                errors.Add("AccountNumber is required.");
            if (string.IsNullOrWhiteSpace(request.Name))
                errors.Add("Name is required.");

            ValidateContacts(request.Contacts, errors);

            if (errors.Count > 0)
                throw new PayloadValidationException(errors);

            var result = new SapMappingResult
            {
                AccountPayload = MapAccount(request)
            };

            if (request.Contacts != null)
            {
                for (int i = 0; i < request.Contacts.Count; i++)
                {
                    var contact = request.Contacts[i];
                    result.ContactPayloads.Add(MapContact(contact, request.AccountNumber));

                    if (contact.IsPrimary)
                        result.PrimaryContactIndex = i;
                }
            }

            if (result.PrimaryContactIndex.HasValue)
            {
                var primaryContact = request.Contacts![result.PrimaryContactIndex.Value];
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

        private static void ValidateContacts(IList<SapContact>? contacts, List<string> errors)
        {
            if (contacts == null || contacts.Count == 0)
                return;

            var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryCount = 0;

            for (int i = 0; i < contacts.Count; i++)
            {
                var contact = contacts[i];
                if (contact == null)
                {
                    errors.Add($"Contacts[{i}]: must not be null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(contact.Email))
                    errors.Add($"Contacts[{i}]: Email is required (used as the contact key).");
                else if (!seenEmails.Add(contact.Email.Trim()))
                    errors.Add($"Contacts[{i}]: duplicate Email '{contact.Email}' — contact keys must be unique per request.");

                if (string.IsNullOrWhiteSpace(contact.FirstName))
                    errors.Add($"Contacts[{i}]: FirstName is required.");

                if (string.IsNullOrWhiteSpace(contact.LastName))
                    errors.Add($"Contacts[{i}]: LastName is required.");

                if (contact.IsPrimary)
                    primaryCount++;
            }

            if (primaryCount > 1)
                errors.Add($"Exactly one contact may be marked IsPrimary (found {primaryCount}).");
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
