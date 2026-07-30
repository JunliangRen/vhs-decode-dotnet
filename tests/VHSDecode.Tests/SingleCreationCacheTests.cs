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

        Assert.True(ready.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        start.Set();
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref callsStarted) == callerCount,
            TimeSpan.FromSeconds(5)));
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        try
        {
            Thread.Sleep(50);
            Assert.Equal(1, Volatile.Read(ref factoryCalls));
        }
        finally
        {
            releaseFactory.Set();
        }

        object[] values = await Task.WhenAll(callers);

        Assert.Equal(1, factoryCalls);
        Assert.All(values, value => Assert.Same(values[0], value));
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
}
