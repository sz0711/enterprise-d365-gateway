using FluentAssertions;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class SapAccountMapperTests
{
    private readonly SapAccountMapper _sut = new();

    private static SapAccountWithContactsRequest MakeRequest(
        string accountNumber = "SAP-001",
        string name = "Contoso GmbH",
        string? city = null,
        string? street = null,
        string? postalCode = null,
        string? country = null,
        string? phone = null,
        string? email = null,
        string? website = null,
        IList<SapContact>? contacts = null)
    {
        return new SapAccountWithContactsRequest
        {
            AccountNumber = accountNumber,
            Name = name,
            City = city,
            Street = street,
            PostalCode = postalCode,
            Country = country,
            Phone = phone,
            Email = email,
            Website = website,
            Contacts = contacts
        };
    }

    private static SapContact MakeContact(
        string email = "john@example.com",
        string firstName = "John",
        string lastName = "Doe",
        string? phone = null,
        string? jobTitle = null,
        bool isPrimary = false)
    {
        return new SapContact
        {
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            Phone = phone,
            JobTitle = jobTitle,
            IsPrimary = isPrimary
        };
    }

    [Fact]
    public void Map_AccountOnly_ReturnsAccountPayloadAndNoContacts()
    {
        var request = MakeRequest();

        var result = _sut.Map(request);

        result.AccountPayload.EntityLogicalName.Should().Be("account");
        result.AccountPayload.KeyAttributes.Should().ContainKey("accountnumber").WhoseValue.Should().Be("SAP-001");
        result.AccountPayload.Attributes.Should().ContainKey("name").WhoseValue.Should().Be("Contoso GmbH");
        result.AccountPayload.SourceSystem.Should().Be("SAP");
        result.AccountPayload.Lookups.Should().BeNull();
        result.ContactPayloads.Should().BeEmpty();
        result.PrimaryContactLinkPayload.Should().BeNull();
    }

    [Fact]
    public void Map_WithOptionalFields_MapsAllAttributes()
    {
        var request = MakeRequest(
            city: "Berlin",
            street: "Friedrichstr. 1",
            postalCode: "10117",
            country: "Germany",
            phone: "+49 30 1234567",
            email: "info@contoso.de",
            website: "https://contoso.de");

        var result = _sut.Map(request);

        var account = result.AccountPayload;
        account.Attributes["address1_city"].Should().Be("Berlin");
        account.Attributes["address1_line1"].Should().Be("Friedrichstr. 1");
        account.Attributes["address1_postalcode"].Should().Be("10117");
        account.Attributes["address1_country"].Should().Be("Germany");
        account.Attributes["telephone1"].Should().Be("+49 30 1234567");
        account.Attributes["emailaddress1"].Should().Be("info@contoso.de");
        account.Attributes["websiteurl"].Should().Be("https://contoso.de");
    }

    [Fact]
    public void Map_NullOptionalFields_OmittedFromAttributes()
    {
        var request = MakeRequest();

        var result = _sut.Map(request);

        var account = result.AccountPayload;
        account.Attributes.Should().NotContainKey("address1_city");
        account.Attributes.Should().NotContainKey("address1_line1");
        account.Attributes.Should().NotContainKey("telephone1");
        account.Attributes.Should().NotContainKey("emailaddress1");
        account.Attributes.Should().NotContainKey("websiteurl");
    }

    [Fact]
    public void Map_WithContacts_ReturnsContactsInContactPayloads()
    {
        var contacts = new List<SapContact>
        {
            MakeContact("a@example.com", "Alice", "Smith"),
            MakeContact("b@example.com", "Bob", "Jones")
        };
        var request = MakeRequest(contacts: contacts);

        var result = _sut.Map(request);

        result.ContactPayloads.Should().HaveCount(2);
        result.ContactPayloads[0].EntityLogicalName.Should().Be("contact");
        result.ContactPayloads[1].EntityLogicalName.Should().Be("contact");
        result.AccountPayload.EntityLogicalName.Should().Be("account");
    }

    [Fact]
    public void Map_ContactFields_MappedCorrectly()
    {
        var contacts = new List<SapContact>
        {
            MakeContact("john@example.com", "John", "Doe", phone: "+49 123", jobTitle: "Manager")
        };
        var request = MakeRequest(contacts: contacts);

        var result = _sut.Map(request);

        var contact = result.ContactPayloads[0];
        contact.EntityLogicalName.Should().Be("contact");
        contact.KeyAttributes.Should().ContainKey("emailaddress1").WhoseValue.Should().Be("john@example.com");
        contact.Attributes["firstname"].Should().Be("John");
        contact.Attributes["lastname"].Should().Be("Doe");
        contact.Attributes["telephone1"].Should().Be("+49 123");
        contact.Attributes["jobtitle"].Should().Be("Manager");
        contact.SourceSystem.Should().Be("SAP");
    }

    [Fact]
    public void Map_Contact_HasParentCustomerIdLookup()
    {
        var contacts = new List<SapContact> { MakeContact() };
        var request = MakeRequest(accountNumber: "SAP-100", contacts: contacts);

        var result = _sut.Map(request);

        var contact = result.ContactPayloads[0];
        contact.Lookups.Should().NotBeNull();
        contact.Lookups!.Should().ContainKey("parentcustomerid");

        var lookup = contact.Lookups["parentcustomerid"];
        lookup.EntityLogicalName.Should().Be("account");
        lookup.KeyAttributes.Should().ContainKey("accountnumber").WhoseValue.Should().Be("SAP-100");
        lookup.CreateIfNotExists.Should().BeFalse();
    }

    [Fact]
    public void Map_ContactOptionalFieldsNull_OmittedFromAttributes()
    {
        var contacts = new List<SapContact> { MakeContact() };
        var request = MakeRequest(contacts: contacts);

        var result = _sut.Map(request);

        var contact = result.ContactPayloads[0];
        contact.Attributes.Should().NotContainKey("telephone1");
        contact.Attributes.Should().NotContainKey("jobtitle");
    }

    [Fact]
    public void Map_PrimaryContact_CreatesSeparateLinkPayload()
    {
        var contacts = new List<SapContact>
        {
            MakeContact("primary@example.com", "Jane", "Doe", isPrimary: true),
            MakeContact("other@example.com", "Bob", "Smith")
        };
        var request = MakeRequest(contacts: contacts);

        var result = _sut.Map(request);

        // Account payload itself has no lookups (primarycontactid is a separate phase)
        result.AccountPayload.Lookups.Should().BeNull();

        // Separate link payload exists
        result.PrimaryContactLinkPayload.Should().NotBeNull();
        var link = result.PrimaryContactLinkPayload!;
        link.EntityLogicalName.Should().Be("account");
        link.KeyAttributes.Should().ContainKey("accountnumber").WhoseValue.Should().Be("SAP-001");
        link.Lookups.Should().NotBeNull();
        link.Lookups!.Should().ContainKey("primarycontactid");

        var lookup = link.Lookups["primarycontactid"];
        lookup.EntityLogicalName.Should().Be("contact");
        lookup.KeyAttributes.Should().ContainKey("emailaddress1").WhoseValue.Should().Be("primary@example.com");
        lookup.CreateIfNotExists.Should().BeFalse();
    }

    [Fact]
    public void Map_NoPrimaryContact_NoLinkPayload()
    {
        var contacts = new List<SapContact>
        {
            MakeContact("a@example.com", "Alice", "Smith", isPrimary: false)
        };
        var request = MakeRequest(contacts: contacts);

        var result = _sut.Map(request);

        result.AccountPayload.Lookups.Should().BeNull();
        result.PrimaryContactLinkPayload.Should().BeNull();
    }

    [Fact]
    public void Map_NullContacts_ReturnsAccountOnlyAndEmptyContacts()
    {
        var request = MakeRequest(contacts: null);

        var result = _sut.Map(request);

        result.AccountPayload.EntityLogicalName.Should().Be("account");
        result.ContactPayloads.Should().BeEmpty();
        result.PrimaryContactLinkPayload.Should().BeNull();
    }

    [Fact]
    public void Map_EmptyContacts_ReturnsAccountOnlyAndEmptyContacts()
    {
        var request = MakeRequest(contacts: new List<SapContact>());

        var result = _sut.Map(request);

        result.AccountPayload.EntityLogicalName.Should().Be("account");
        result.ContactPayloads.Should().BeEmpty();
    }

    [Fact]
    public void Map_NullRequest_ThrowsArgumentNull()
    {
        var act = () => _sut.Map(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Map_EmptyAccountNumber_ThrowsValidation()
    {
        var request = MakeRequest(accountNumber: "  ");

        var act = () => _sut.Map(request);

        act.Should().Throw<PayloadValidationException>();
    }

    [Fact]
    public void Map_EmptyName_ThrowsValidation()
    {
        var request = MakeRequest(name: "  ");

        var act = () => _sut.Map(request);

        act.Should().Throw<PayloadValidationException>();
    }
}
