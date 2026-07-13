using System.ServiceModel;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

/// <summary>
/// Classification of real Dataverse organization-service faults — the exception
/// shape actual throttling/duplicate/not-found errors arrive in.
/// </summary>
public class ErrorClassifierFaultTests
{
    private readonly ErrorClassifier _sut = new();

    private static FaultException<OrganizationServiceFault> Fault(int errorCode, string message = "fault")
        => new(new OrganizationServiceFault { ErrorCode = errorCode, Message = message }, message);

    [Theory]
    [InlineData(DataverseErrorCodes.NumberOfRequestsExceeded)]
    [InlineData(DataverseErrorCodes.ExecutionTimeExceeded)]
    [InlineData(DataverseErrorCodes.ConcurrentRequestsExceeded)]
    [InlineData(DataverseErrorCodes.ThrottlingBurstRequestLimitExceededError)]
    public void Classify_ServiceProtectionFaults_Throttling(int errorCode)
    {
        // Real service-protection fault text contains none of the "429"/"throttle"
        // keywords — classification must work from the error code alone.
        var fault = Fault(errorCode, "Combined execution time of incoming requests exceeded limit.");

        _sut.Classify(fault).Should().Be(ErrorCategory.Throttling);
    }

    [Theory]
    [InlineData(DataverseErrorCodes.SqlTimeoutError)]
    [InlineData(DataverseErrorCodes.SqlErrorGeneric)]
    [InlineData(DataverseErrorCodes.UnexpectedError)]
    public void Classify_TransientPlatformFaults_Transient(int errorCode)
    {
        _sut.Classify(Fault(errorCode)).Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void Classify_UnknownFault_Permanent()
    {
        _sut.Classify(Fault(-2147204304, "Invalid attribute value.")).Should().Be(ErrorCategory.Permanent);
    }

    [Theory]
    [InlineData(DataverseErrorCodes.DuplicateRecordsFound)]
    [InlineData(DataverseErrorCodes.DuplicateAlternateKey)]
    [InlineData(DataverseErrorCodes.ObjectDoesNotExist)]
    [InlineData(DataverseErrorCodes.RecordNotFoundByEntityKey)]
    public void IsKeyConflict_KeyConflictFaultCodes_True(int errorCode)
    {
        _sut.IsKeyConflict(Fault(errorCode, "some fault")).Should().BeTrue();
    }

    [Theory]
    [InlineData("A record with these values already exists.")]
    [InlineData("account With Id = 00000000-0000-0000-0000-000000000001 Does Not Exist")]
    [InlineData("Duplicate record found.")]
    public void IsKeyConflict_KeyConflictMessages_True(string message)
    {
        _sut.IsKeyConflict(new InvalidOperationException(message)).Should().BeTrue();
    }

    [Fact]
    public void IsKeyConflict_UnrelatedError_False()
    {
        _sut.IsKeyConflict(new InvalidOperationException("Missing privilege.")).Should().BeFalse();
    }

    [Fact]
    public void IsKeyConflict_NestedInnerException_True()
    {
        var outer = new InvalidOperationException(
            "wrapper",
            Fault(DataverseErrorCodes.DuplicateAlternateKey, "inner"));

        _sut.IsKeyConflict(outer).Should().BeTrue();
    }

    [Fact]
    public void Classify_AggregateWithHttpRequestException_Transient()
    {
        var agg = new AggregateException(new HttpRequestException("connection reset"));

        _sut.Classify(agg).Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void Classify_AggregateWithThrottlingFault_Throttling()
    {
        var agg = new AggregateException(Fault(DataverseErrorCodes.NumberOfRequestsExceeded, "requests exceeded"));

        _sut.Classify(agg).Should().Be(ErrorCategory.Throttling);
    }

    [Fact]
    public void Classify_AggregateCancellationWinsOverTransient()
    {
        var agg = new AggregateException(
            new TimeoutException(),
            new OperationCanceledException());

        _sut.Classify(agg).Should().Be(ErrorCategory.Cancellation);
    }
}
