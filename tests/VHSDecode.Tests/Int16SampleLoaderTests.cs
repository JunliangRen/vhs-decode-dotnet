using System.Runtime.InteropServices;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class Int16SampleLoaderTests
{
    [Fact(DisplayName = "S16 reusable reads preserve exact samples and reuse exact-length output")]
    public void ReusableReadsPreserveSamplesAndReuseExactLengthOutput()
    {
        short[] samples = CreateSamples(64);
        byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var loader = new Int16SampleLoader();
        IReusableRfSampleLoader reusable = loader;

        using var firstStream = new MemoryStream(bytes, writable: false);
        double[] first = Assert.IsType<double[]>(
            reusable.ReadReusable(firstStream, sample: 3, readLength: 16));
        Assert.Equal(16, first.Length);
        Assert.Equal(samples.Skip(3).Take(16).Select(value => (double)value), first);

        Array.Fill(first, double.NaN);
        reusable.ReturnReusable(first);
        using var secondStream = new MemoryStream(bytes, writable: false);
        double[] second = Assert.IsType<double[]>(
            reusable.ReadReusable(secondStream, sample: 29, readLength: 16));

        Assert.True(reusable.ReuseForSequentialDecode);
        Assert.Same(first, second);
        Assert.Equal(16, second.Length);
        Assert.Equal(samples.Skip(29).Take(16).Select(value => (double)value), second);
        reusable.ReturnReusable(second);
    }

    [Fact(DisplayName = "S16 reusable short reads return null before taking decoded output")]
    public void ReusableShortReadReturnsNullBeforeTakingDecodedOutput()
    {
        var loader = new Int16SampleLoader();
        double[] retained = new double[8];
        loader.ReturnReusable(retained);
        short[] samples = CreateSamples(7);
        byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

        using (var shortStream = new MemoryStream(bytes, writable: false))
        {
            Assert.Null(loader.ReadReusable(shortStream, sample: 0, readLength: 8));
        }

        Assert.Equal(1, loader.CachedReusableDecodedBufferCount);

        short[] completeSamples = CreateSamples(8);
        using var completeStream = new MemoryStream(
            MemoryMarshal.AsBytes(completeSamples.AsSpan()).ToArray(),
            writable: false);
        double[] complete = Assert.IsType<double[]>(
            loader.ReadReusable(completeStream, sample: 0, readLength: 8));
        Assert.Same(retained, complete);
        Assert.Equal(completeSamples.Select(value => (double)value), complete);
        loader.ReturnReusable(complete);
    }

    [Fact(DisplayName = "S16 reusable output retention is concurrency-safe and bounded")]
    public void ReusableOutputRetentionIsConcurrencySafeAndBounded()
    {
        const int readLength = 32;
        int leaseCount = Int16SampleLoader.MaximumRetainedDecodedBufferCount * 2;
        short[] samples = CreateSamples(readLength + leaseCount);
        byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var loader = new Int16SampleLoader();
        var leases = new double[leaseCount][];

        Parallel.For(
            0,
            leaseCount,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            index =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                double[] lease = Assert.IsType<double[]>(
                    loader.ReadReusable(stream, index, readLength));
                Assert.Equal(readLength, lease.Length);
                Assert.Equal((double)samples[index], lease[0]);
                Assert.Equal((double)samples[index + readLength - 1], lease[^1]);
                leases[index] = lease;
            });

        Assert.Equal(leaseCount, leases.Distinct(ReferenceEqualityComparer.Instance).Count());

        Parallel.For(0, leaseCount, index => loader.ReturnReusable(leases[index]));
        Assert.Equal(
            Int16SampleLoader.MaximumRetainedDecodedBufferCount,
            loader.CachedReusableDecodedBufferCount);

        var oversizedLoader = new Int16SampleLoader();
        oversizedLoader.ReturnReusable(
            new double[Int16SampleLoader.MaximumRetainedDecodedBufferLength + 1]);
        Assert.Equal(0, oversizedLoader.CachedReusableDecodedBufferCount);
    }

    [Fact(DisplayName = "S16 reusable 32K reads allocate no decoded array after warmup")]
    public void Reusable32KReadsAllocateNoDecodedArrayAfterWarmup()
    {
        const int readLength = 32 * 1024;
        short[] samples = CreateSamples(readLength);
        byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var loader = new Int16SampleLoader();

        using (var warmStream = new MemoryStream(bytes, writable: false))
        {
            double[] warm = loader.ReadReusable(warmStream, 0, readLength)!;
            loader.ReturnReusable(warm);
        }

        using var measuredStream = new MemoryStream(bytes, writable: false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double[] actual = loader.ReadReusable(measuredStream, 0, readLength)!;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(readLength, actual.Length);
        Assert.Equal((double)samples[0], actual[0]);
        Assert.Equal((double)samples[^1], actual[^1]);
        loader.ReturnReusable(actual);
        Assert.True(
            allocated < 32 * 1024,
            $"Warm reusable 32K S16 read allocated {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "S16 reusable reads preserve validation and exact EOF behavior")]
    public void ReusableReadsPreserveValidationAndExactEofBehavior()
    {
        var loader = new Int16SampleLoader();
        short[] samples = [short.MinValue, -1, 0, 1, short.MaxValue];
        byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

        using var exactStream = new MemoryStream(bytes, writable: false);
        Assert.Equal(
            samples.Select(value => (double)value),
            loader.ReadReusable(exactStream, 0, samples.Length));

        using var emptyStream = new MemoryStream(bytes, writable: false);
        Assert.Empty(loader.ReadReusable(emptyStream, samples.Length, 0)!);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => loader.ReadReusable(new MemoryStream(bytes), -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => loader.ReadReusable(new MemoryStream(bytes), 0, -1));
    }

    private static short[] CreateSamples(int length)
    {
        var samples = new short[length];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = unchecked((short)((index * 7_919) + short.MinValue));
        }

        return samples;
    }
}
