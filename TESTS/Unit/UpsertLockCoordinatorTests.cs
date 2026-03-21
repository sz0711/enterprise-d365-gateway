using FluentAssertions;
using enterprise_d365_gateway.Services;

namespace enterprise_d365_gateway.Tests.Unit;

public class UpsertLockCoordinatorTests
{
    private readonly UpsertLockCoordinator _sut = new();

    [Fact]
    public async Task AcquireAsync_ReturnsDisposable()
    {
        var handle = await _sut.AcquireAsync("key-1");

        handle.Should().NotBeNull();
        handle.Should().BeAssignableTo<IDisposable>();
        handle.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_SameKey_Serialized()
    {
        var order = new List<int>();
        var handle1 = await _sut.AcquireAsync("key-1");

        var task2 = Task.Run(async () =>
        {
            var h2 = await _sut.AcquireAsync("key-1");
            order.Add(2);
            h2.Dispose();
        });

        await Task.Delay(100); // give task2 a chance to block
        order.Add(1);
        handle1.Dispose(); // release → task2 proceeds

        await task2;

        order.Should().Equal(1, 2);
    }

    [Fact]
    public async Task AcquireAsync_DifferentKeys_Parallel()
    {
        var handle1 = await _sut.AcquireAsync("key-a");
        var handle2Task = _sut.AcquireAsync("key-b");

        var handle2 = await handle2Task.WaitAsync(TimeSpan.FromSeconds(1));

        handle2.Should().NotBeNull();
        handle1.Dispose();
        handle2.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_KeyNormalization_CaseInsensitive()
    {
        var order = new List<int>();
        var handle1 = await _sut.AcquireAsync("  Key-1  ");

        var task2 = Task.Run(async () =>
        {
            var h2 = await _sut.AcquireAsync("KEY-1");
            order.Add(2);
            h2.Dispose();
        });

        await Task.Delay(100);
        order.Add(1);
        handle1.Dispose();

        await task2;

        order.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Dispose_ReleasesLock_SecondAcquirerProceeds()
    {
        var handle = await _sut.AcquireAsync("key-1");
        handle.Dispose();

        // Second acquire should complete immediately
        var handle2 = await _sut.AcquireAsync("key-1").WaitAsync(TimeSpan.FromSeconds(1));
        handle2.Should().NotBeNull();
        handle2.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_CancellationToken_Honored()
    {
        var handle = await _sut.AcquireAsync("key-1");

        using var cts = new CancellationTokenSource(100);
        var act = async () => await _sut.AcquireAsync("key-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        handle.Dispose();
    }
}
