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
        var decoder = BuildDecoder(loader, workerThreads: 4);

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
        var decoder = BuildDecoder(loader, workerThreads: 4);

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
        var decoder = BuildDecoder(loader, workerThreads: 4);

        _ = decoder.Read(firstStream, begin: 0, length: 20 * 12);
        Assert.Equal(16, decoder.CachedDecodedBlockCount);
        int readsBeforeStreamChange = loader.ReadCount;

        _ = decoder.Read(secondStream, begin: 12 * 19, length: 12);

        Assert.Equal(readsBeforeStreamChange + 1, loader.ReadCount);
        Assert.Equal(1, decoder.CachedDecodedBlockCount);
    }

    private static RfBlockStreamDecoder BuildDecoder(IRfSampleLoader loader, int workerThreads)
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
        var pipeline = new RfBlockDecodePipeline(loader, filters, sampleRateHz: 16.0);
        return new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads);
    }

    private sealed class CountingSampleLoader : IRfSampleLoader
    {
        public int ReadCount { get; private set; }

        public double[] Read(Stream stream, long sample, int readLength)
        {
            ReadCount++;
            return Enumerable.Range(0, readLength)
                .Select(index => (double)(sample + index))
                .ToArray();
        }
    }
}
