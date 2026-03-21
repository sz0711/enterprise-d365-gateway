using FluentAssertions;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

public class RequestValidatorTests
{
    private readonly Mock<IEarlyboundEntityMapper> _mapperMock = new();
    private readonly RequestValidator _sut;

    public RequestValidatorTests()
    {
        _sut = new RequestValidator(_mapperMock.Object);
    }

    [Fact]
    public void Validate_ValidPayload_NoException()
    {
        var payload = new TestPayloadBuilder().Build();
        _mapperMock.Setup(m => m.ValidatePayload(payload));

        var act = () => _sut.Validate(payload);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NullPayload_ThrowsPayloadValidationException()
    {
        var act = () => _sut.Validate(null!);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("null"));
    }

    [Fact]
    public void Validate_EmptyKeyAttributes_ThrowsValidationError()
    {
        var payload = new TestPayloadBuilder().WithUpsertKey("").Build();

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("KeyAttributes"));
    }

    [Fact]
    public void Validate_EmptyEntityLogicalName_ThrowsValidationError()
    {
        var payload = new TestPayloadBuilder().WithEntity("").Build();

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("EntityLogicalName"));
    }

    [Fact]
    public void Validate_NullAttributes_ThrowsValidationError()
    {
        var payload = new TestPayloadBuilder().WithNullAttributes().Build();

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("Attributes"));
    }

    [Fact]
    public void Validate_MapperThrows_BubblesUp()
    {
        var payload = new TestPayloadBuilder().Build();
        _mapperMock.Setup(m => m.ValidatePayload(payload))
            .Throws(new PayloadValidationException(new[] { "Unknown field 'xyz'" }));

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain("Unknown field 'xyz'");
    }

    [Fact]
    public void Validate_LookupMissingKeyAttributes_ThrowsError()
    {
        var payload = new TestPayloadBuilder()
            .WithLookup("parentaccountid", new LookupDefinition
            {
                EntityLogicalName = "account",
                KeyAttributes = new Dictionary<string, object?>()
            })
            .Build();

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("KeyAttributes"));
    }

    [Fact]
    public void Validate_LookupMissingEntityLogicalName_ThrowsError()
    {
        var payload = new TestPayloadBuilder()
            .WithLookup("parentaccountid", new LookupDefinition
            {
                EntityLogicalName = "",
                KeyAttributes = new Dictionary<string, object?> { ["name"] = "Parent" }
            })
            .Build();

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("EntityLogicalName"));
    }

    [Fact]
    public void Validate_LookupEmptyKeyAttributes_ThrowsError()
    {
        var payload = new TestPayloadBuilder()
            .WithLookup("parentaccountid", new LookupDefinition
            {
                EntityLogicalName = "account",
                KeyAttributes = new Dictionary<string, object?>()
            })
            .Build();

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().Contain(e => e.Contains("KeyAttributes"));
    }

    [Fact]
    public void Validate_NestedLookupValidation_Recursive()
    {
        var payload = new TestPayloadBuilder()
            .WithLookup("parentaccountid", new LookupDefinition
            {
                EntityLogicalName = "account",
                KeyAttributes = new Dictionary<string, object?> { ["name"] = "Parent" },
                NestedLookups = new Dictionary<string, LookupDefinition>
                {
                    ["ownerid"] = new LookupDefinition
                    {
                        EntityLogicalName = "",
                        KeyAttributes = new Dictionary<string, object?>()
                    }
                }
            })
            .Build();

        var act = () => _sut.Validate(payload);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().HaveCountGreaterOrEqualTo(2);
    }

    [Fact]
    public void ValidateBatch_OneInvalid_PrefixesErrors()
    {
        var request = new UpsertBatchRequest
        {
            Payloads = new List<UpsertPayload>
            {
                new TestPayloadBuilder().Build(),
                new TestPayloadBuilder().WithUpsertKey("").WithEntity("").WithNullAttributes().Build()
            }
        };
        _mapperMock.Setup(m => m.ValidatePayload(It.IsAny<UpsertPayload>()));

        var act = () => _sut.ValidateBatch(request);

        act.Should().Throw<PayloadValidationException>()
            .Which.ValidationErrors.Should().AllSatisfy(e => e.Should().StartWith("Payload[1]:"));
    }

    [Fact]
    public void ValidateBatch_AllValid_NoException()
    {
        var request = new UpsertBatchRequest
        {
            Payloads = new List<UpsertPayload>
            {
                new TestPayloadBuilder().Build(),
                new TestPayloadBuilder().WithUpsertKey("EXT-002").Build()
            }
        };
        _mapperMock.Setup(m => m.ValidatePayload(It.IsAny<UpsertPayload>()));

        var act = () => _sut.ValidateBatch(request);

        act.Should().NotThrow();
    }
}
