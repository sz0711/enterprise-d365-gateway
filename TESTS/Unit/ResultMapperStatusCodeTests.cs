using System.Net;
using FluentAssertions;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

/// <summary>
/// Batch status-code semantics: cancellation counts as a technical failure
/// (never 200), throttling-only batches surface as retryable 429.
/// </summary>
public class ResultMapperStatusCodeTests
{
    private readonly ResultMapper _sut = new();

    private static UpsertResult With(ErrorCategory category) => new() { ErrorCategory = category };

    [Fact]
    public void DetermineBatchStatusCode_CancellationFailure_Returns500()
    {
        var results = new[] { With(ErrorCategory.None), With(ErrorCategory.Cancellation) };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public void DetermineBatchStatusCode_ThrottlingOnly_Returns429()
    {
        var results = new[] { With(ErrorCategory.None), With(ErrorCategory.Throttling) };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void DetermineBatchStatusCode_ThrottlingPlusPermanent_Returns500()
    {
        var results = new[] { With(ErrorCategory.Throttling), With(ErrorCategory.Permanent) };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public void DetermineBatchStatusCode_ThrottlingPlusValidation_Returns429()
    {
        // Throttling dominates: the caller must retry the batch anyway.
        var results = new[] { With(ErrorCategory.Throttling), With(ErrorCategory.Validation) };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void DetermineBatchStatusCode_ValidationOnly_Returns400()
    {
        var results = new[] { With(ErrorCategory.None), With(ErrorCategory.Validation) };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void DetermineBatchStatusCode_AllSuccess_Returns200()
    {
        var results = new[] { With(ErrorCategory.None) };

        _sut.DetermineBatchStatusCode(results).Should().Be(HttpStatusCode.OK);
    }
}
