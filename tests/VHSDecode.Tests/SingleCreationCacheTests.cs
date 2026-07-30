using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class SingleCreationCacheTests
{
    [Fact(DisplayName = "Single-creation cache runs one factory for concurrent callers")]
    public async Task RunsOneFactoryForConcurrentCallers()
    {
        const int callerCount = 12;
        var cache = new SingleCreationCache<int, object>();
        using var ready = new CountdownEvent(callerCount);
        using var start = new ManualResetEventSlim();
        using var factoryEntered = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        int callsStarted = 0;
        int factoryCalls = 0;

        Task<object>[] callers = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    ready.Signal();
                    start.Wait(cancellationToken);
                    Interlocked.Increment(ref callsStarted);
                    return cache.GetOrAdd(7, _ =>
                    {
                        Interlocked.Increment(ref factoryCalls);
                        factoryEntered.Set();
                        releaseFactory.Wait(cancellationToken);
                        return new object();
                    });
                },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        bool allReady = false;
        bool allStarted = false;
        bool factoryStarted = false;
        int blockedFactoryCalls = 0;
        object[]? values = null;
        try
        {
            allReady = ready.Wait(TimeSpan.FromSeconds(5), cancellationToken);
            start.Set();
            allStarted = SpinWait.SpinUntil(
                () => Volatile.Read(ref callsStarted) == callerCount,
                TimeSpan.FromSeconds(5));
            factoryStarted = factoryEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken);
            if (factoryStarted)
            {
                Thread.Sleep(50);
            }

            blockedFactoryCalls = Volatile.Read(ref factoryCalls);
        }
        finally
        {
            start.Set();
            releaseFactory.Set();
            values = await Task.WhenAll(callers)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        Assert.True(allReady);
        Assert.True(allStarted);
        Assert.True(factoryStarted);
        Assert.Equal(1, blockedFactoryCalls);
        Assert.Equal(1, factoryCalls);
        Assert.All(values, value => Assert.Same(values[0], value));
    }

    [Fact(DisplayName = "Single-creation cache builds different keys concurrently")]
    public async Task BuildsDifferentKeysConcurrently()
    {
        var cache = new SingleCreationCache<int, object>();
        using var factoriesEntered = new CountdownEvent(2);
        using var releaseFactories = new ManualResetEventSlim();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Task<object>[] callers = Enumerable.Range(1, 2)
            .Select(key => Task.Factory.StartNew(
                () => cache.GetOrAdd(key, _ =>
                {
                    factoriesEntered.Signal();
                    releaseFactories.Wait(cancellationToken);
                    return new object();
                }),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        bool bothFactoriesStarted = false;
        object[]? values = null;
        try
        {
            bothFactoriesStarted = factoriesEntered.Wait(
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        finally
        {
            releaseFactories.Set();
            values = await Task.WhenAll(callers)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        Assert.True(bothFactoriesStarted);
        Assert.NotSame(values[0], values[1]);
    }

    [Fact(DisplayName = "Single-creation cache retries a failed factory")]
    public void RetriesFailedFactory()
    {
        var cache = new SingleCreationCache<int, object>();
        int factoryCalls = 0;

        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrAdd(7, _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                throw new InvalidOperationException("probe");
            }));

        object expected = cache.GetOrAdd(7, _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return new object();
        });
        object actual = cache.GetOrAdd(7, _ => throw new InvalidOperationException("must not run"));

        Assert.Equal(2, factoryCalls);
        Assert.Same(expected, actual);
    }

    [Fact(DisplayName = "Single-creation cache rejects same-key factory reentrancy")]
    public void RejectsSameKeyFactoryReentrancy()
    {
        var cache = new SingleCreationCache<int, object>();
        var expected = new object();

        object actual = cache.GetOrAdd(7, key =>
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => cache.GetOrAdd(key, _ => new object()));

            Assert.Contains("same cache key", exception.Message, StringComparison.Ordinal);
            return expected;
        });

        object cached = cache.GetOrAdd(7, _ => throw new InvalidOperationException("must not run"));

        Assert.Same(expected, actual);
        Assert.Same(expected, cached);
    }
}
