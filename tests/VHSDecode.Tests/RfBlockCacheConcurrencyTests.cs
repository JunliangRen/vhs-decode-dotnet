using System.Numerics;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfBlockCacheConcurrencyTests
{
    [Fact(DisplayName = "Parallel RF reads reuse overlapping decoded blocks in order")]
    public void ParallelRfReadsReuseOverlappingDecodedBlocksInOrder()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4);

        RfDecodedSpan first = decoder.Read(stream, begin: 0, length: 24)!;
        RfDecodedSpan second = decoder.Read(stream, begin: 12, length: 24)!;

        Assert.Equal(3, loader.ReadCount);
        Assert.Equal(first.Input[12..], second.Input[..12]);
        Assert.Equal(first.Video[12..], second.Video[..12]);
        Assert.InRange(decoder.CachedDecodedBlockCount, 1, 16);
    }

    [Fact(DisplayName = "RF decoded-block cache invalidation forces fresh parallel work")]
    public void RfDecodedBlockCacheInvalidationForcesFreshParallelWork()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4);

        _ = decoder.Read(stream, begin: 0, length: 24);
        _ = decoder.Read(stream, begin: 12, length: 24);
        decoder.InvalidateCachedBlocks();
        _ = decoder.Read(stream, begin: 12, length: 24);

        Assert.Equal(5, loader.ReadCount);
        Assert.Equal(2, decoder.CachedDecodedBlockCount);
    }

    [Fact(DisplayName = "RF decoded-block cache is bounded and scoped to its input stream")]
    public void RfDecodedBlockCacheIsBoundedAndScopedToItsInputStream()
    {
        var loader = new CountingSampleLoader();
        using var firstStream = new MemoryStream();
        using var secondStream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4);

        _ = decoder.Read(firstStream, begin: 0, length: 20 * 12);
        Assert.Equal(16, decoder.CachedDecodedBlockCount);
        int readsBeforeStreamChange = loader.ReadCount;

        _ = decoder.Read(secondStream, begin: 12 * 19, length: 12);

        Assert.Equal(readsBeforeStreamChange + 1, loader.ReadCount);
        Assert.Equal(1, decoder.CachedDecodedBlockCount);
    }

    [Fact(DisplayName = "RF block prefetch reuses future work without changing decoded samples")]
    public void RfBlockPrefetchReusesFutureWorkWithoutChangingDecodedSamples()
    {
        int warningCount = 0;
        var loader = new CountingSampleLoader(returnZeros: true);
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(
            loader,
            workerThreads: 4,
            prefetchBlocks: 2,
            weakRfDiagnostics: true,
            diagnosticLogger: (_, _) => Interlocked.Increment(ref warningCount));

        RfDecodedSpan first = decoder.Read(stream, begin: 0, length: 24)!;
        Assert.Equal(4, loader.ReadCount);
        Assert.Equal(2, warningCount);

        RfDecodedSpan second = decoder.Read(stream, begin: 12, length: 24)!;

        Assert.Equal(5, loader.ReadCount);
        Assert.Equal(3, warningCount);
        Assert.Equal(first.Input[12..], second.Input[..12]);
        Assert.Equal(first.Video[12..], second.Video[..12]);
        Assert.Equal(2, decoder.PrefetchBlocks);
        Assert.InRange(decoder.CachedDecodedBlockCount, 1, 18);
        Assert.InRange(decoder.CachedPrefetchedBlockCount, 0, decoder.PrefetchBlocks);
    }

    [Fact(DisplayName = "RF block prefetch is discarded when the input stream changes")]
    public void RfBlockPrefetchIsDiscardedWhenInputStreamChanges()
    {
        var loader = new CountingSampleLoader();
        using var firstStream = new MemoryStream();
        using var secondStream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4, prefetchBlocks: 2);

        _ = decoder.Read(firstStream, begin: 0, length: 24);
        RfDecodedSpan second = decoder.Read(secondStream, begin: 0, length: 24)!;

        Assert.Equal(8, loader.ReadCount);
        Assert.Equal(Enumerable.Range(2, 24).Select(value => (double)value), second.Input);
        Assert.InRange(decoder.CachedDecodedBlockCount, 1, 18);
        Assert.InRange(decoder.CachedPrefetchedBlockCount, 0, decoder.PrefetchBlocks);

        var failingLoader = new FailsAfterReadCountLoader(successfulReads: 2);
        using var failingDecoder = BuildDecoder(failingLoader, workerThreads: 4, prefetchBlocks: 2);
        RfDecodedSpan completed = failingDecoder.Read(firstStream, begin: 0, length: 24)!;
        Assert.Equal(24, completed.Input.Length);
        Assert.Equal(3, failingLoader.ReadCount);
        Assert.Throws<IOException>(() => failingDecoder.Read(firstStream, begin: 12, length: 24));
    }

    [Fact(DisplayName = "RF block prefetch has a hard capacity and observes disposal")]
    public void RfBlockPrefetchHasHardCapacityAndObservesDisposal()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        var decoder = BuildDecoder(loader, workerThreads: 100, prefetchBlocks: int.MaxValue);

        Assert.Equal(RfBlockStreamDecoder.MaximumPrefetchBlocks, decoder.PrefetchBlocks);
        _ = decoder.Read(stream, begin: 0, length: 24);
        Assert.InRange(
            decoder.CachedDecodedBlockCount,
            1,
            16 + RfBlockStreamDecoder.MaximumPrefetchBlocks);
        decoder.Dispose();
        decoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => decoder.Read(stream, begin: 0, length: 12));
    }

    [Fact(DisplayName = "RF block prefetch remains bounded across a sustained forward decode")]
    public void RfBlockPrefetchRemainsBoundedAcrossSustainedForwardDecode()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4, prefetchBlocks: 2);

        for (int field = 0; field < 256; field++)
        {
            RfDecodedSpan span = decoder.Read(stream, begin: field * 12L, length: 24)!;
            Assert.Equal(24, span.Input.Length);
            Assert.InRange(decoder.CachedDecodedBlockCount, 1, 18);
            Assert.InRange(decoder.CachedPrefetchedBlockCount, 0, decoder.PrefetchBlocks);
        }

        Assert.InRange(loader.ReadCount, 258, 260);
    }

    private static RfBlockStreamDecoder BuildDecoder(
        IRfSampleLoader loader,
        int workerThreads,
        int prefetchBlocks = 0,
        bool weakRfDiagnostics = false,
        Action<string, string>? diagnosticLogger = null)
    {
        const int blockLength = 16;
        Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
        double[] ones = Enumerable.Repeat(1.0, blockLength).ToArray();
        var filters = new DecodeFilterSet(
            identity,
            identity,
            identity,
            identity,
            identity,
            identity,
            null,
            ones,
            ones,
            ones,
            ones,
            ones,
            ones,
            null);
        if (weakRfDiagnostics)
        {
            filters = filters with
            {
                VhsEnvelopeSos = [new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)]
            };
        }

        var pipeline = new RfBlockDecodePipeline(
            loader,
            filters,
            sampleRateHz: 16.0,
            filterOptions: weakRfDiagnostics
                ? new DecodeFilterOptions(FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation)
                : null,
            diagnosticLogger: diagnosticLogger);
        return new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads,
            prefetchBlocks);
    }

    private sealed class CountingSampleLoader : IRfSampleLoader
    {
        private readonly bool _returnZeros;

        public CountingSampleLoader(bool returnZeros = false)
        {
            _returnZeros = returnZeros;
        }

        public int ReadCount { get; private set; }

        public double[] Read(Stream stream, long sample, int readLength)
        {
            ReadCount++;
            if (_returnZeros)
            {
                return new double[readLength];
            }

            return Enumerable.Range(0, readLength)
                .Select(index => (double)(sample + index))
                .ToArray();
        }
    }

    private sealed class FailsAfterReadCountLoader(int successfulReads) : IRfSampleLoader
    {
        public int ReadCount { get; private set; }

        public double[] Read(Stream stream, long sample, int readLength)
        {
            ReadCount++;
            if (ReadCount > successfulReads)
            {
                throw new IOException("Synthetic loader failure.");
            }

            return Enumerable.Range(0, readLength)
                .Select(index => (double)(sample + index))
                .ToArray();
        }
    }
}
