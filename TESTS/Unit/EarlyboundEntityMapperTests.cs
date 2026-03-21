using FluentAssertions;
using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

public class EarlyboundEntityMapperTests
{
    private readonly EarlyboundEntityMapper _sut = new();

    [Fact]
    public void ValidatePayload_ValidAccount_NoError()
    {
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithAttributes(new Dictionary<string, object?> { ["name"] = "Test" })
            .Build();

        var act = () => _sut.ValidatePayload(payload);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidatePayload_UnknownEntity_ThrowsError()
    {
        var payload = new TestPayloadBuilder()
            .WithEntity("nonexistent_entity_xyz")
            .Build();

        var act = () => _sut.ValidatePayload(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("does not exist"));
    }

    [Fact]
    public void ValidatePayload_UnknownAttribute_ThrowsError()
    {
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithAttributes(new Dictionary<string, object?> { ["totally_fake_field_xyz"] = "value" })
            .Build();

        var act = () => _sut.ValidatePayload(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("not defined"));
    }

    [Fact]
    public void ValidatePayload_KeyAttributeNotOnEntity_ThrowsError()
    {
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithAttributes(new Dictionary<string, object?> { ["name"] = "Test" })
            .WithExternalId("totally_fake_field_xyz", "EXT-001")
            .Build();

        var act = () => _sut.ValidatePayload(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("KeyAttribute"));
    }

    [Fact]
    public void ValidatePayload_KeyAttributeAlsoInAttributes_ThrowsError()
    {
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithAttributes(new Dictionary<string, object?> { ["name"] = "Test", ["accountnumber"] = "A001" })
            .WithExternalId("accountnumber", "A001")
            .Build();

        var act = () => _sut.ValidatePayload(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("must not also be present"));
    }

    [Fact]
    public void MapToEntity_AccountWithStringAttributes_CreatesEntity()
    {
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithAttributes(new Dictionary<string, object?> { ["name"] = "Test Corp" })
            .Build();

        var entity = _sut.MapToEntity(payload);

        entity.Should().NotBeNull();
        entity.LogicalName.Should().Be("account");
        entity["name"].Should().Be("Test Corp");
    }

    [Fact]
    public void MapToEntity_AccountWithGuidAttribute_CorrectConversion()
    {
        var guid = Guid.NewGuid();
        var element = JsonElementFactory.From(guid);
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithAttributes(new Dictionary<string, object?> { ["accountid"] = element })
            .Build();

        var entity = _sut.MapToEntity(payload);

        entity["accountid"].Should().Be(guid);
    }

    [Fact]
    public void MapToEntity_EntityIdSet_PopulatesEntityId()
    {
        var id = Guid.NewGuid();
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithId(id)
            .WithAttributes(new Dictionary<string, object?> { ["name"] = "Test" })
            .Build();

        var entity = _sut.MapToEntity(payload);

        entity.Id.Should().Be(id);
    }

    [Fact]
    public void MapToEntity_KeyAttributeNotInAttributes_SetsOnEntity()
    {
        var payload = new TestPayloadBuilder()
            .WithEntity("account")
            .WithAttributes(new Dictionary<string, object?> { ["name"] = "Test" })
            .WithExternalId("accountnumber", "EXT-001")
            .Build();

        var entity = _sut.MapToEntity(payload);

        entity["accountnumber"].Should().Be("EXT-001");
    }
}
