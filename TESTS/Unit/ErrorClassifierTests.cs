using FluentAssertions;
using Polly.CircuitBreaker;
using Polly.Timeout;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class ErrorClassifierTests
{
    private readonly ErrorClassifier _sut = new();

    [Fact]
    public void Classify_PayloadValidationException_ReturnsValidation()
    {
        var ex = new PayloadValidationException(new[] { "error" });
        _sut.Classify(ex).Should().Be(ErrorCategory.Validation);
    }

    [Fact]
    public void Classify_ArgumentException_ReturnsValidation()
    {
        _sut.Classify(new ArgumentException("bad arg")).Should().Be(ErrorCategory.Validation);
    }

    [Fact]
    public void Classify_OperationCanceledException_ReturnsCancellation()
    {
        _sut.Classify(new OperationCanceledException()).Should().Be(ErrorCategory.Cancellation);
    }

    [Fact]
    public void Classify_TimeoutException_ReturnsTransient()
    {
        _sut.Classify(new TimeoutException()).Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void Classify_TimeoutRejectedException_ReturnsTransient()
    {
        _sut.Classify(new TimeoutRejectedException()).Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void Classify_BrokenCircuitException_ReturnsTransient()
    {
        _sut.Classify(new BrokenCircuitException()).Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void Classify_HttpRequestException_ReturnsTransient()
    {
        _sut.Classify(new HttpRequestException()).Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void Classify_InvalidOperationExceptionRateLimit_ReturnsThrottling()
    {
        _sut.Classify(new InvalidOperationException("Rate limit exceeded"))
            .Should().Be(ErrorCategory.Throttling);
    }

    [Fact]
    public void Classify_AggregateExceptionWithTimeout_ReturnsTransient()
    {
        var ex = new AggregateException(new TimeoutException());
        _sut.Classify(ex).Should().Be(ErrorCategory.Transient);
    }

    [Fact]
    public void Classify_AggregateExceptionWithCancellation_ReturnsCancellation()
    {
        var ex = new AggregateException(new OperationCanceledException());
        _sut.Classify(ex).Should().Be(ErrorCategory.Cancellation);
    }

    [Fact]
    public void Classify_UnknownException_ReturnsPermanent()
    {
        _sut.Classify(new NotSupportedException("unknown")).Should().Be(ErrorCategory.Permanent);
    }
}
