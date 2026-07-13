using System.Text.Json;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

/// <summary>
/// Choice-column (picklist/enum) mapping, restricted-attribute deny list,
/// canonical attribute casing and typed query-value conversion.
/// </summary>
public class EarlyboundEntityMapperEnumAndSecurityTests
{
    private readonly EarlyboundEntityMapper _sut = new();

    private static UpsertPayload AccountPayload(
        IDictionary<string, object?>? attributes = null,
        IDictionary<string, object?>? keys = null)
        => new()
        {
            EntityLogicalName = "account",
            KeyAttributes = keys ?? new Dictionary<string, object?> { ["accountnumber"] = "ACC-1" },
            Attributes = attributes ?? new Dictionary<string, object?>()
        };

    // --- Choice columns (early-bound enums must become OptionSetValue) ---

    [Fact]
    public void MapToEntity_PicklistFromInt_BecomesOptionSetValue()
    {
        var payload = AccountPayload(new Dictionary<string, object?> { ["industrycode"] = 3 });

        var entity = _sut.MapToEntity(payload);

        entity["industrycode"].Should().BeOfType<OptionSetValue>()
            .Which.Value.Should().Be(3);
    }

    [Fact]
    public void MapToEntity_PicklistFromJsonNumber_BecomesOptionSetValue()
    {
        var payload = AccountPayload(new Dictionary<string, object?>
        {
            ["industrycode"] = JsonElementFactory.From(7)
        });

        var entity = _sut.MapToEntity(payload);

        entity["industrycode"].Should().BeOfType<OptionSetValue>()
            .Which.Value.Should().Be(7);
    }

    [Fact]
    public void MapToEntity_PicklistFromEnumName_BecomesOptionSetValue()
    {
        // account_industrycode.Accounting = 1 in the generated model
        var payload = AccountPayload(new Dictionary<string, object?>
        {
            ["industrycode"] = JsonElementFactory.From("Accounting")
        });

        var entity = _sut.MapToEntity(payload);

        entity["industrycode"].Should().BeOfType<OptionSetValue>()
            .Which.Value.Should().Be(1);
    }

    [Fact]
    public void ValidatePayload_PicklistInvalidName_ThrowsValidation()
    {
        var payload = AccountPayload(new Dictionary<string, object?>
        {
            ["industrycode"] = JsonElementFactory.From("NotARealIndustry")
        });

        var act = () => _sut.ValidatePayload(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("industrycode"));
    }

    // --- Restricted attributes ---

    [Fact]
    public void ValidatePayload_PortalIdentityAttribute_Rejected()
    {
        var payload = new UpsertPayload
        {
            EntityLogicalName = "contact",
            KeyAttributes = new Dictionary<string, object?> { ["emailaddress1"] = "a@b.c" },
            Attributes = new Dictionary<string, object?> { ["adx_identity_passwordhash"] = "hash" }
        };

        var act = () => _sut.ValidatePayload(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("not writable"));
    }

    [Fact]
    public void ValidatePayload_PortalIdentityKeyAttribute_Rejected()
    {
        var payload = new UpsertPayload
        {
            EntityLogicalName = "contact",
            KeyAttributes = new Dictionary<string, object?> { ["adx_identity_username"] = "user" },
            Attributes = new Dictionary<string, object?>()
        };

        var act = () => _sut.ValidatePayload(payload);

        act.Should().Throw<PayloadValidationException>();
    }

    // --- Canonical attribute casing ---

    [Fact]
    public void MapToEntity_MixedCaseAttributeNames_WrittenLowercase()
    {
        var payload = new UpsertPayload
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["AccountNumber"] = "ACC-9" },
            Attributes = new Dictionary<string, object?> { ["Name"] = "Contoso" }
        };

        var entity = _sut.MapToEntity(payload);

        entity.Attributes.ContainsKey("name").Should().BeTrue();
        entity.Attributes.ContainsKey("Name").Should().BeFalse();
        entity.Attributes.ContainsKey("accountnumber").Should().BeTrue();
        entity.Attributes.ContainsKey("AccountNumber").Should().BeFalse();
    }

    // --- Lookup definition validation ---

    [Fact]
    public void ValidateLookup_UnknownEntity_ReportsError()
    {
        var errors = _sut.ValidateLookup("Lookups.x", new LookupDefinition
        {
            EntityLogicalName = "notarealentity",
            KeyAttributes = new Dictionary<string, object?> { ["a"] = 1 }
        });

        errors.Should().ContainSingle().Which.Should().Contain("does not exist in the early-bound model");
    }

    [Fact]
    public void ValidateLookup_UnknownKeyAttribute_ReportsError()
    {
        var errors = _sut.ValidateLookup("Lookups.x", new LookupDefinition
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["notanattribute"] = 1 }
        });

        errors.Should().ContainSingle().Which.Should().Contain("notanattribute");
    }

    [Fact]
    public void ValidateLookup_ValidDefinition_NoErrors()
    {
        var errors = _sut.ValidateLookup("Lookups.x", new LookupDefinition
        {
            EntityLogicalName = "account",
            KeyAttributes = new Dictionary<string, object?> { ["accountnumber"] = "A-1" },
            CreateAttributes = new Dictionary<string, object?> { ["name"] = "Fallback" }
        });

        errors.Should().BeEmpty();
    }

    // --- Typed query-value conversion ---

    [Fact]
    public void ConvertQueryValue_GuidLikeStringForStringColumn_StaysString()
    {
        var guidLike = Guid.NewGuid().ToString();

        var converted = _sut.ConvertQueryValue("account", "accountnumber", JsonElementFactory.From(guidLike));

        converted.Should().BeOfType<string>().Which.Should().Be(guidLike);
    }

    [Fact]
    public void ConvertQueryValue_DateLikeStringForStringColumn_StaysString()
    {
        var converted = _sut.ConvertQueryValue("account", "accountnumber", JsonElementFactory.From("2024-01-15"));

        converted.Should().BeOfType<string>().Which.Should().Be("2024-01-15");
    }

    [Fact]
    public void ConvertQueryValue_NumberForStringColumn_CoercedToString()
    {
        var converted = _sut.ConvertQueryValue("account", "accountnumber", 123);

        converted.Should().BeOfType<string>().Which.Should().Be("123");
    }

    [Fact]
    public void ConvertQueryValue_PicklistColumn_ReturnsRawInt()
    {
        var converted = _sut.ConvertQueryValue("account", "industrycode", JsonElementFactory.From(4));

        converted.Should().BeOfType<int>().Which.Should().Be(4);
    }

    [Fact]
    public void ConvertQueryValue_UnknownAttribute_FallsBackToNormalization()
    {
        var converted = _sut.ConvertQueryValue("account", "ext_customfield", JsonElementFactory.From("plain"));

        converted.Should().Be("plain");
    }

    [Fact]
    public void ConvertWriteValue_MoneyFromNumber_BecomesMoney()
    {
        var converted = _sut.ConvertWriteValue("account", "revenue", JsonElementFactory.From(1234.5));

        converted.Should().BeOfType<Money>().Which.Value.Should().Be(1234.5m);
    }
}
