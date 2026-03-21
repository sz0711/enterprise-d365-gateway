using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class AdaptiveConcurrencyLimiterTests
{
    private static DataverseOptions DefaultOptions(
        int max = 8, int min = 1, int successThreshold = 5) => new()
    {
        Url = "https://test.crm.dynamics.com",
        MaxDegreeOfParallelism = max,
        MinDegreeOfParallelism = min,
        AdaptiveConcurrencySuccessThreshold = successThreshold,
        AdaptiveConcurrencyEnabled = true
    };

    private static AdaptiveConcurrencyLimiter CreateSut(DataverseOptions? options = null)
    {
        var opts = options ?? DefaultOptions();
        return new AdaptiveConcurrencyLimiter(
            new Mock<ILogger<AdaptiveConcurrencyLimiter>>().Object,
            Options.Create(opts));
    }

    [Fact]
    public void InitialLimit_EqualsMaxDegreeOfParallelism()
    {
        var sut = CreateSut(DefaultOptions(max: 8));

        sut.CurrentLimit.Should().Be(8);
    }

    [Fact]
    public void RecordThrottle_HalvesCurrentLimit()
    {
        var sut = CreateSut(DefaultOptions(max: 8));

        sut.RecordThrottle();

        sut.CurrentLimit.Should().Be(4);
    }

    [Fact]
    public void RecordThrottle_TwiceInSuccession_QuartersCurrentLimit()
    {
        var sut = CreateSut(DefaultOptions(max: 8));

        sut.RecordThrottle();
        sut.RecordThrottle();

        sut.CurrentLimit.Should().Be(2);
    }

    [Fact]
    public void RecordThrottle_NeverGoesBelowMinimum()
    {
        var sut = CreateSut(DefaultOptions(max: 8, min: 2));

        // Halve repeatedly: 8 → 4 → 2 → 2 (clamped at min)
        sut.RecordThrottle();
        sut.RecordThrottle();
        sut.RecordThrottle();
        sut.RecordThrottle();

        sut.CurrentLimit.Should().Be(2);
    }

    [Fact]
    public void RecordSuccess_AfterThresholdReached_IncrementsLimit()
    {
        var sut = CreateSut(DefaultOptions(max: 8, successThreshold: 3));

        // First throttle down to 4
        sut.RecordThrottle();
        sut.CurrentLimit.Should().Be(4);

        // Need 3 successes to increment
        sut.RecordSuccess();
        sut.RecordSuccess();
        sut.CurrentLimit.Should().Be(4, "not enough successes yet");

        sut.RecordSuccess(); // 3rd success → triggers increment
        sut.CurrentLimit.Should().Be(5);
    }

    [Fact]
    public void RecordSuccess_DoesNotExceedMaxLimit()
    {
        var sut = CreateSut(DefaultOptions(max: 4, successThreshold: 1));

        // Already at max
        sut.RecordSuccess();

        sut.CurrentLimit.Should().Be(4);
    }

    [Fact]
    public void RecordThrottle_ResetsSuccessCounter()
    {
        var sut = CreateSut(DefaultOptions(max: 8, successThreshold: 3));

        // Throttle down first
        sut.RecordThrottle(); // 8 → 4

        // Record 2 successes (not yet at threshold)
        sut.RecordSuccess();
        sut.RecordSuccess();

        // Throttle resets counter
        sut.RecordThrottle(); // 4 → 2

        // Need fresh 3 successes now
        sut.RecordSuccess();
        sut.RecordSuccess();
        sut.CurrentLimit.Should().Be(2, "still need one more success after reset");

        sut.RecordSuccess(); // 3rd fresh success → increment
        sut.CurrentLimit.Should().Be(3);
    }

    [Fact]
    public void RecordSuccess_MultipleRounds_RecoversFully()
    {
        var sut = CreateSut(DefaultOptions(max: 8, successThreshold: 2));

        sut.RecordThrottle(); // 8 → 4
        sut.RecordThrottle(); // 4 → 2

        // Recover: each 2 successes → +1
        for (int round = 0; round < 6; round++)
        {
            sut.RecordSuccess();
            sut.RecordSuccess();
        }

        sut.CurrentLimit.Should().Be(8, "should recover to max after sustained success");
    }

    [Fact]
    public void ThreadSafety_ConcurrentThrottlesDoNotCorrupt()
    {
        var sut = CreateSut(DefaultOptions(max: 100, min: 1));

        // Fire many concurrent throttles
        Parallel.For(0, 100, _ => sut.RecordThrottle());

        sut.CurrentLimit.Should().BeGreaterOrEqualTo(1);
        sut.CurrentLimit.Should().BeLessOrEqualTo(100);
    }

    [Fact]
    public void ThreadSafety_ConcurrentSuccessesDoNotCorrupt()
    {
        var sut = CreateSut(DefaultOptions(max: 100, min: 1, successThreshold: 1));

        // Throttle to minimum first
        for (int i = 0; i < 10; i++) sut.RecordThrottle();

        // Fire many concurrent successes
        Parallel.For(0, 500, _ => sut.RecordSuccess());

        sut.CurrentLimit.Should().BeGreaterOrEqualTo(1);
        sut.CurrentLimit.Should().BeLessOrEqualTo(100);
    }
}
