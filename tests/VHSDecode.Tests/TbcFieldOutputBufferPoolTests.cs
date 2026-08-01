using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcFieldOutputBufferPoolTests
{
    [Fact(DisplayName = "Field output buffers return only after the final lease release")]
    public void FieldOutputBuffersReturnOnlyAfterFinalLeaseRelease()
    {
        var pool = new TbcFieldOutputBufferPool(
            lumaLength: 8,
            chromaLength: 4,
            maximumRetainedBuffers: 2);
        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease first = pool.Rent();
        ushort[] firstLuma = first.Luma;
        ushort[] firstChroma = first.Chroma!;
        first.Retain();
        first.Dispose();

        using (TbcFieldOutputBufferPool.TbcFieldOutputBufferLease concurrent = pool.Rent())
        {
            Assert.NotSame(firstLuma, concurrent.Luma);
            Assert.NotSame(firstChroma, concurrent.Chroma);
        }

        first.Dispose();
        Assert.Equal(2, pool.RetainedLumaBufferCount);
        Assert.Equal(2, pool.RetainedChromaBufferCount);

        using TbcFieldOutputBufferPool.TbcFieldOutputBufferLease reused = pool.Rent();
        Assert.Same(firstLuma, reused.Luma);
        Assert.Same(firstChroma, reused.Chroma);
    }

    [Fact(DisplayName = "Field output buffer retention is bounded per channel")]
    public void FieldOutputBufferRetentionIsBoundedPerChannel()
    {
        var pool = new TbcFieldOutputBufferPool(
            lumaLength: 8,
            chromaLength: 4,
            maximumRetainedBuffers: 2);
        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease[] leases =
            Enumerable.Range(0, 5).Select(_ => pool.Rent()).ToArray();

        foreach (TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease in leases)
        {
            lease.Dispose();
        }

        Assert.Equal(2, pool.RetainedLumaBufferCount);
        Assert.Equal(2, pool.RetainedChromaBufferCount);
    }

    [Fact(DisplayName = "Field output pool supports luma-only decoding")]
    public void FieldOutputPoolSupportsLumaOnlyDecoding()
    {
        var pool = new TbcFieldOutputBufferPool(
            lumaLength: 8,
            chromaLength: null,
            maximumRetainedBuffers: 1);

        using TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease = pool.Rent();

        Assert.Equal(8, lease.Luma.Length);
        Assert.Null(lease.Chroma);
    }

    [Fact(DisplayName = "Warm field output buffer reuse avoids repeated array allocation")]
    public void WarmFieldOutputBufferReuseAvoidsRepeatedArrayAllocation()
    {
        var pool = new TbcFieldOutputBufferPool(
            lumaLength: 32_768,
            chromaLength: 32_768,
            maximumRetainedBuffers: 1);
        pool.Rent().Dispose();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            pool.Rent().Dispose();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(
            allocated < 128_000,
            $"Reusing 1,000 field output leases allocated {allocated:N0} bytes.");
        Assert.Equal(1, pool.RetainedLumaBufferCount);
        Assert.Equal(1, pool.RetainedChromaBufferCount);
    }

    [Fact(DisplayName = "Concurrent field output leases never alias active buffers")]
    public void ConcurrentFieldOutputLeasesNeverAliasActiveBuffers()
    {
        var pool = new TbcFieldOutputBufferPool(
            lumaLength: 1_024,
            chromaLength: 1_024,
            maximumRetainedBuffers: 8);
        var activeLuma = new System.Collections.Concurrent.ConcurrentDictionary<ushort[], byte>();
        var activeChroma = new System.Collections.Concurrent.ConcurrentDictionary<ushort[], byte>();

        Parallel.For(
            0,
            10_000,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            iteration =>
            {
                _ = iteration;
                using TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease = pool.Rent();
                Assert.True(activeLuma.TryAdd(lease.Luma, 0));
                Assert.True(activeChroma.TryAdd(lease.Chroma!, 0));
                Thread.SpinWait(50);
                Assert.True(activeLuma.TryRemove(lease.Luma, out _));
                Assert.True(activeChroma.TryRemove(lease.Chroma!, out _));
            });

        Assert.Empty(activeLuma);
        Assert.Empty(activeChroma);
        Assert.InRange(pool.RetainedLumaBufferCount, 1, 8);
        Assert.InRange(pool.RetainedChromaBufferCount, 1, 8);
    }

    [Fact(DisplayName = "Field output ownership does not change decoded field value equality")]
    public void FieldOutputOwnershipDoesNotChangeDecodedFieldValueEquality()
    {
        var pool = new TbcFieldOutputBufferPool(0, chromaLength: null);
        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease = pool.Rent();
        TbcDecodedField first = CreateEmptyField() with { Samples = lease.Luma };
        TbcDecodedField second = first with { };
        first.AttachOutputBuffers(lease);

        try
        {
            Assert.Equal(first, second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
        }
        finally
        {
            first.ReleaseOutputBuffers();
        }
    }

    [Fact(DisplayName = "Fields with pooled output buffers reject record cloning")]
    public void FieldsWithPooledOutputBuffersRejectRecordCloning()
    {
        var pool = new TbcFieldOutputBufferPool(8, chromaLength: 8, maximumRetainedBuffers: 1);
        TbcFieldOutputBufferPool.TbcFieldOutputBufferLease lease = pool.Rent();
        TbcDecodedField original = CreateEmptyField() with
        {
            Samples = lease.Luma,
            ChromaSamples = lease.Chroma
        };
        original.AttachOutputBuffers(lease);

        try
        {
            Assert.Throws<InvalidOperationException>(() => original with { StartSample = 1 });
            Assert.Same(lease, original.OutputBufferLease);
        }
        finally
        {
            original.ReleaseOutputBuffers();
        }

        Assert.Null(original.OutputBufferLease);
        Assert.Throws<InvalidOperationException>(() => original with { StartSample = 2 });
    }

    private static TbcDecodedField CreateEmptyField()
        => new(
            StartSample: 0,
            Samples: [],
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                0,
                0,
                0,
                new SyncRange(0, 0),
                new SyncRange(0, 0),
                new SyncRange(0, 0)),
            SyncThresholdHz: 0,
            MeanLineLength: 0,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0);
}
