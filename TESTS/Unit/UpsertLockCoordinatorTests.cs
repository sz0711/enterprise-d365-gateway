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

        var contenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task2 = Task.Run(async () =>
        {
            contenderStarted.SetResult();
            var h2 = await _sut.AcquireAsync("key-1");
            order.Add(2);
            h2.Dispose();
        });

        // Deterministically wait until the contender is about to block, then
        // assert it is genuinely blocked (a no-op lock would let it complete).
        await contenderStarted.Task;
        await Task.Delay(50);
        task2.IsCompleted.Should().BeFalse("the second acquirer must block while key-1 is held");

        order.Add(1);
        handle1.Dispose(); // release → task2 proceeds

        await task2.WaitAsync(TimeSpan.FromSeconds(2));

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

        var contenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task2 = Task.Run(async () =>
        {
            contenderStarted.SetResult();
            var h2 = await _sut.AcquireAsync("KEY-1");
            order.Add(2);
            h2.Dispose();
        });

        await contenderStarted.Task;
        await Task.Delay(50);
        task2.IsCompleted.Should().BeFalse("differently-cased/whitespaced keys must map to the same lock");

        order.Add(1);
        handle1.Dispose();

        await task2.WaitAsync(TimeSpan.FromSeconds(2));

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

    [Fact]
    public async Task Release_LastHolder_EvictsEntryFromDictionary()
    {
        // Memory-leak guard: distinct keys must not accumulate forever.
        for (int i = 0; i < 100; i++)
        {
            var handle = await _sut.AcquireAsync($"key-{i}");
            handle.Dispose();
        }

        GetInternalLockCount(_sut).Should().Be(0);
    }

    [Fact]
    public async Task Release_WithActiveWaiter_KeepsEntryAlive()
    {
        var handle1 = await _sut.AcquireAsync("key-contended");
        var acquire2 = _sut.AcquireAsync("key-contended");

        // Give the waiter time to register, then assert the entry is STILL present
        // while a waiter holds a reference (a premature eviction would drop it).
        await Task.Delay(50);
        GetInternalLockCount(_sut).Should().Be(1, "the entry must survive while a waiter is registered");

        handle1.Dispose();

        var handle2 = await acquire2.WaitAsync(TimeSpan.FromSeconds(2));
        handle2.Dispose();

        GetInternalLockCount(_sut).Should().Be(0, "the entry is evicted once the last holder releases");
    }

    [Fact]
    public async Task AcquireAsync_HighContentionStress_MutualExclusionAndCleanEviction()
    {
        var counters = new Dictionary<string, int>();
        var maxObserved = 0;
        var current = 0;

        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var key = $"stress-{i % 5}";
            using var handle = await _sut.AcquireAsync(key);
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref maxObserved, now);
            lock (counters)
            {
                counters[key] = counters.GetValueOrDefault(key) + 1;
            }
            await Task.Yield();
            Interlocked.Decrement(ref current);
        }));

        await Task.WhenAll(tasks);

        counters.Values.Sum().Should().Be(200);
        maxObserved.Should().BeLessThanOrEqualTo(5, "at most one holder per distinct key");
        GetInternalLockCount(_sut).Should().Be(0, "all entries must be evicted after release");
    }

    [Fact]
    public async Task CanceledWait_DoesNotLeakEntry()
    {
        var handle = await _sut.AcquireAsync("key-cancel");
        using var cts = new CancellationTokenSource(50);

        var act = async () => await _sut.AcquireAsync("key-cancel", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        handle.Dispose();
        GetInternalLockCount(_sut).Should().Be(0);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot)
                return;
        }
    }

    private static int GetInternalLockCount(UpsertLockCoordinator coordinator)
    {
        var field = typeof(UpsertLockCoordinator).GetField(
            "_locks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (System.Collections.ICollection)field.GetValue(coordinator)!;
        return dict.Count;
    }
}
