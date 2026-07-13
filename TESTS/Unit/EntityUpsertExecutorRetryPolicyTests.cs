using System.ServiceModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;
using enterprise_d365_gateway.Tests.Helpers;

namespace enterprise_d365_gateway.Tests.Unit;

/// <summary>
/// Retry-policy semantics: Create must never be replayed after ambiguous
/// failures (timeout — the record may exist server-side), while idempotent
/// Update/Retrieve are retried, and throttle rejections retry everywhere.
/// </summary>
public class EntityUpsertExecutorRetryPolicyTests
{
    private static DataverseOptions FastOptions() => new()
    {
        Url = "https://test.crm.dynamics.com",
        MaxRequestsPerSecond = 1000,
        MaxRetries = 2,
        RetryBaseDelayMs = 1,
        RateLimitRetryDelaySeconds = 0, // keep throttle-retry tests instant (annotations not enforced here)
        TimeoutPerOperationSeconds = 30,
        CircuitBreakerFailureThreshold = 100,
        CircuitBreakerSamplingDurationSeconds = 60,
        CircuitBreakerBreakDurationSeconds = 30
    };

    private static (EntityUpsertExecutor Sut, MockServiceClientFactory Mock, Mock<IAdaptiveConcurrencyLimiter> Limiter) CreateSut()
    {
        var mockFactory = new MockServiceClientFactory();
        var limiter = new Mock<IAdaptiveConcurrencyLimiter>();
        var sut = new EntityUpsertExecutor(
            mockFactory.FactoryMock.Object,
            new Mock<ILogger<EntityUpsertExecutor>>().Object,
            limiter.Object,
            Options.Create(FastOptions()));
        return (sut, mockFactory, limiter);
    }

    private static FaultException<OrganizationServiceFault> ThrottleFault()
        => new(new OrganizationServiceFault
        {
            ErrorCode = DataverseErrorCodes.NumberOfRequestsExceeded,
            Message = "Number of requests exceeded the limit of 6000 over time window of 300 seconds."
        }, "throttled");

    [Fact]
    public async Task CreateAsync_TimeoutException_NotRetried()
    {
        var (sut, mockFactory, _) = CreateSut();
        var calls = 0;
        mockFactory.ServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .ThrowsAsync(new TimeoutException("The request channel timed out."));

        var act = async () => await sut.CreateAsync(new Entity("account"));

        await act.Should().ThrowAsync<TimeoutException>();
        calls.Should().Be(1, "an ambiguous Create failure must not be replayed — the record may already exist");
    }

    [Fact]
    public async Task CreateAsync_HttpRequestException_NotRetried()
    {
        var (sut, mockFactory, _) = CreateSut();
        var calls = 0;
        mockFactory.ServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .ThrowsAsync(new HttpRequestException("connection reset"));

        var act = async () => await sut.CreateAsync(new Entity("account"));

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_ThrottleFault_IsRetriedAndRecordsThrottle()
    {
        var (sut, mockFactory, limiter) = CreateSut();
        var expectedId = Guid.NewGuid();
        var calls = 0;
        mockFactory.ServiceMock
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                if (calls == 1) throw ThrottleFault();
                return expectedId;
            });

        var id = await sut.CreateAsync(new Entity("account"));

        id.Should().Be(expectedId);
        calls.Should().Be(2, "throttle rejections are executed-never — retrying a Create is safe");
        limiter.Verify(l => l.RecordThrottle(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task UpdateAsync_TimeoutException_IsRetried()
    {
        var (sut, mockFactory, _) = CreateSut();
        var calls = 0;
        mockFactory.ServiceMock
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                if (calls == 1) throw new TimeoutException("The request channel timed out.");
                return Task.CompletedTask;
            });

        await sut.UpdateAsync(new Entity("account") { Id = Guid.NewGuid() });

        calls.Should().Be(2, "Update is idempotent — transient timeouts are retried");
    }

    [Fact]
    public async Task RetrieveMultipleAsync_HttpRequestException_IsRetried()
    {
        var (sut, mockFactory, _) = CreateSut();
        var calls = 0;
        var collection = new EntityCollection();
        mockFactory.ServiceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                if (calls == 1) throw new HttpRequestException("socket closed");
                return collection;
            });

        var result = await sut.RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression("account"));

        result.Should().BeSameAs(collection);
        calls.Should().Be(2);
    }

    [Fact]
    public void TryGetRetryAfter_FaultWithTimeSpanDetail_Returned()
    {
        var fault = new OrganizationServiceFault
        {
            ErrorCode = DataverseErrorCodes.NumberOfRequestsExceeded,
            Message = "throttled"
        };
        fault.ErrorDetails["Retry-After"] = TimeSpan.FromSeconds(42);
        var exception = new FaultException<OrganizationServiceFault>(fault, "throttled");

        EntityUpsertExecutor.TryGetRetryAfter(exception, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void TryGetRetryAfter_NoDetail_False()
    {
        var exception = ThrottleFault();

        EntityUpsertExecutor.TryGetRetryAfter(exception, out _).Should().BeFalse();
    }

    [Fact]
    public void IsRateLimitException_ThrottleFaultWithoutKeywords_True()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault
            {
                ErrorCode = DataverseErrorCodes.ExecutionTimeExceeded,
                Message = "Combined execution time of incoming requests exceeded limit."
            }, "fault");

        EntityUpsertExecutor.IsRateLimitException(fault).Should().BeTrue();
    }
}
