using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class SingleCreationCacheTests
{
    [Fact(DisplayName = "Single-creation cache runs one factory for concurrent callers")]
    public async Task RunsOneFactoryForConcurrentCallers()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertRunsOneFactoryForConcurrentCallers(
            new SingleCreationCache<int, object>(),
            cancellationToken);
        await AssertRunsOneFactoryForConcurrentCallers(
            new SingleCreationCache<int, object>(capacity: 32),
            cancellationToken);
    }

    private static async Task AssertRunsOneFactoryForConcurrentCallers(
        SingleCreationCache<int, object> cache,
        CancellationToken cancellationToken)
    {
        const int callerCount = 12;
        using var ready = new CountdownEvent(callerCount);
        using var start = new ManualResetEventSlim();
        using var factoryEntered = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
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

    [Fact(DisplayName = "Single-creation cache retries failures and enforces optional capacity")]
    public void RetriesFailedFactoryAndEnforcesOptionalCapacity()
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

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SingleCreationCache<int, object>(capacity: 0));

        const int capacity = 32;
        var boundedCache = new SingleCreationCache<int, object>(capacity);
        object[] initialValues = Enumerable.Range(0, capacity)
            .Select(key => boundedCache.GetOrAdd(key, static _ => new object()))
            .ToArray();

        Assert.Same(
            initialValues[0],
            boundedCache.GetOrAdd(0, static _ => new object()));

        _ = boundedCache.GetOrAdd(capacity, static _ => new object());

        Assert.Same(
            initialValues[1],
            boundedCache.GetOrAdd(1, static _ => new object()));

        object firstAfterEviction = boundedCache.GetOrAdd(
            0,
            static _ => new object());

        Assert.NotSame(initialValues[0], firstAfterEviction);
        Assert.Same(
            firstAfterEviction,
            boundedCache.GetOrAdd(0, static _ => new object()));
        Assert.NotSame(
            initialValues[1],
            boundedCache.GetOrAdd(1, static _ => new object()));
    }

    [Fact(DisplayName = "Single-creation cache rejects same-key factory reentrancy")]
    public void RejectsSameKeyFactoryReentrancy()
    {
        AssertRejectsSameKeyFactoryReentrancy(
            new SingleCreationCache<int, object>());
        AssertRejectsSameKeyFactoryReentrancy(
            new SingleCreationCache<int, object>(capacity: 32));
    }

    private static void AssertRejectsSameKeyFactoryReentrancy(
        SingleCreationCache<int, object> cache)
    {
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
