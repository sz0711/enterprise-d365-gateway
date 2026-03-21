using System.Net;
using FluentAssertions;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class ResultMapperTests
{
    private readonly ResultMapper _sut = new();

    [Fact]
    public void MapSuccess_WithLookupTraces_PopulatesAllFields()
    {
        var id = Guid.NewGuid();
        var traces = new List<LookupTrace>
        {
            new() { AttributeName = "parentaccountid", EntityLogicalName = "account", ResolvedId = Guid.NewGuid(), WasCreated = true, Depth = 0 }
        };

        var result = _sut.MapSuccess("account", "EXT-001", id, true, traces);

        result.EntityLogicalName.Should().Be("account");
        result.UpsertKey.Should().Be("EXT-001");
        result.Id.Should().Be(id);
        result.Created.Should().BeTrue();
        result.ErrorCategory.Should().Be(ErrorCategory.None);
        result.LookupTraces.Should().HaveCount(1);
    }

    [Fact]
    public void MapSuccess_WithoutLookupTraces_TracesNull()
    {
        var result = _sut.MapSuccess("contact", "C-001", Guid.NewGuid(), false);

        result.LookupTraces.Should().BeNull();
        result.ErrorCategory.Should().Be(ErrorCategory.None);
    }

    [Fact]
    public void MapError_PayloadValidationException_ExtractsValidationErrors()
    {
        var ex = new PayloadValidationException(new[] { "Field required", "Invalid type" });

        var result = _sut.MapError("account", "EXT-001", ex, ErrorCategory.Validation);

        result.ErrorCategory.Should().Be(ErrorCategory.Validation);
        result.ErrorMessage.Should().Contain("Field required");
        result.ValidationErrors.Should().HaveCount(2);
        result.Created.Should().BeFalse();
        result.Id.Should().Be(Guid.Empty);
    }

    [Fact]
    public void MapError_GenericException_SetsErrorMessageOnly()
    {
        var ex = new InvalidOperationException("Something broke");

        var result = _sut.MapError("account", "EXT-001", ex, ErrorCategory.Permanent);

        result.ErrorMessage.Should().Be("Something broke");
        result.ValidationErrors.Should().BeNull();
    }

    [Fact]
    public void DetermineBatchStatusCode_AllNone_Returns200()
    {
        var results = new List<UpsertResult>
        {
            new() { ErrorCategory = ErrorCategory.None },
            new() { ErrorCategory = ErrorCategory.None }
        };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void DetermineBatchStatusCode_OnlyValidation_Returns400()
    {
        var results = new List<UpsertResult>
        {
            new() { ErrorCategory = ErrorCategory.None },
            new() { ErrorCategory = ErrorCategory.Validation }
        };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void DetermineBatchStatusCode_AnyTransient_Returns500()
    {
        var results = new List<UpsertResult>
        {
            new() { ErrorCategory = ErrorCategory.None },
            new() { ErrorCategory = ErrorCategory.Transient }
        };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public void DetermineBatchStatusCode_MixedValidationAndPermanent_Returns500()
    {
        var results = new List<UpsertResult>
        {
            new() { ErrorCategory = ErrorCategory.Validation },
            new() { ErrorCategory = ErrorCategory.Permanent }
        };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.InternalServerError);
    }
}
