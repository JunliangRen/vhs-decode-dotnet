using System.Numerics;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfBlockCacheConcurrencyTests
{
    [Fact(DisplayName = "RF prefetch recommendation adds one bounded worker wave")]
    public void RfPrefetchRecommendationAddsOneBoundedWorkerWave()
    {
        Assert.Equal(0, RfBlockStreamDecoder.RecommendedPrefetchBlocks(0, 20));
        Assert.Equal(0, RfBlockStreamDecoder.RecommendedPrefetchBlocks(1, 20));
        Assert.Equal(4, RfBlockStreamDecoder.RecommendedPrefetchBlocks(2, 20));
        Assert.Equal(10, RfBlockStreamDecoder.RecommendedPrefetchBlocks(5, 20));
        Assert.Equal(28, RfBlockStreamDecoder.RecommendedPrefetchBlocks(20, 20));
        Assert.Equal(8, RfBlockStreamDecoder.RecommendedPrefetchBlocks(100, 4));
        Assert.Equal(RfBlockStreamDecoder.MaximumPrefetchBlocks, RfBlockStreamDecoder.RecommendedPrefetchBlocks(100, 64));
        Assert.Equal(RfBlockStreamDecoder.MaximumPrefetchBlocks, RfBlockStreamDecoder.RecommendedPrefetchBlocks(int.MaxValue, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => RfBlockStreamDecoder.RecommendedPrefetchBlocks(-1, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => RfBlockStreamDecoder.RecommendedPrefetchBlocks(20, 0));

        using var constrainedDecoder = BuildDecoder(
            new CountingSampleLoader(),
            workerThreads: 2,
            prefetchBlocks: int.MaxValue);
        Assert.Equal(RfBlockStreamDecoder.MaximumPrefetchBlocks, constrainedDecoder.PrefetchBlocks);
        Assert.Equal(2, constrainedDecoder.PrefetchWorkerThreads);
    }

    [Fact(DisplayName = "Parallel RF reads reuse overlapping decoded blocks in order")]
    public void ParallelRfReadsReuseOverlappingDecodedBlocksInOrder()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4, optionalOutputs: true);
        using var serialStream = new MemoryStream();
        using var serialDecoder = BuildDecoder(
            new CountingSampleLoader(),
            workerThreads: 1,
            optionalOutputs: true);

        RfDecodedSpan first = decoder.Read(stream, begin: 0, length: 24)!;
        RfDecodedSpan second = decoder.Read(stream, begin: 12, length: 24)!;
        RfDecodedSpan serial = serialDecoder.Read(serialStream, begin: 0, length: 24)!;

        Assert.Equal(3, loader.ReadCount);
        Assert.NotSame(first.Input, second.Input);
        Assert.NotSame(first.Video, second.Video);
        Assert.NotSame(first.DemodRaw, second.DemodRaw);
        Assert.Equal(first.Input[12..], second.Input[..12]);
        Assert.Equal(first.Video[12..], second.Video[..12]);
        Assert.Equal(serial.Input, first.Input);
        Assert.Equal(serial.Video, first.Video);
        Assert.Equal(serial.DemodRaw, first.DemodRaw);
        Assert.Equal(serial.Envelope, first.Envelope);
        Assert.Equal(serial.VideoLowPass, first.VideoLowPass);
        Assert.Equal(serial.RfHighPass, first.RfHighPass);
        Assert.Equal(serial.Chroma, first.Chroma);
        Assert.Equal(serial.Efm, first.Efm);
        Assert.Equal(serial.VideoBurst, first.VideoBurst);
        Assert.Equal(serial.VideoPilot, first.VideoPilot);
        Assert.NotNull(first.Chroma);
        Assert.NotNull(first.Efm);
        Assert.NotNull(first.VideoBurst);
        Assert.NotNull(first.VideoPilot);
        Assert.InRange(decoder.CachedDecodedBlockCount, 1, 16);
    }

    [Fact(DisplayName = "Leased RF spans reuse two exact-size buffer sets only after disposal")]
    public void LeasedRfSpansReuseTwoExactSizeBufferSetsOnlyAfterDisposal()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4);

        RfBlockStreamDecoder.RfDecodedSpanLease firstLease = decoder.ReadLeased(
            stream,
            begin: 0,
            length: 24)!;
        RfDecodedSpan first = firstLease.Span;
        double[] firstInputSnapshot = first.Input.ToArray();
        firstLease.Dispose();
        firstLease.Dispose();

        using RfBlockStreamDecoder.RfDecodedSpanLease alternateLease = decoder.ReadLeased(
            stream,
            begin: 6,
            length: 36)!;
        RfDecodedSpan alternate = alternateLease.Span;
        double[] alternateInputSnapshot = alternate.Input.ToArray();
        alternateLease.Dispose();

        using RfBlockStreamDecoder.RfDecodedSpanLease secondLease = decoder.ReadLeased(
            stream,
            begin: 12,
            length: 24)!;
        RfDecodedSpan second = secondLease.Span;
        Assert.Same(first.Input, second.Input);
        Assert.Same(first.Video, second.Video);
        Assert.Same(first.DemodRaw, second.DemodRaw);
        Assert.Same(first.Envelope, second.Envelope);
        Assert.Same(first.VideoLowPass, second.VideoLowPass);
        Assert.Same(first.RfHighPass, second.RfHighPass);
        Assert.Equal(firstInputSnapshot[12..], second.Input[..12]);

        secondLease.Dispose();
        using RfBlockStreamDecoder.RfDecodedSpanLease secondAlternateLease = decoder.ReadLeased(
            stream,
            begin: 18,
            length: 36)!;
        Assert.Same(alternate.Input, secondAlternateLease.Span.Input);
        Assert.Same(alternate.Video, secondAlternateLease.Span.Video);
        Assert.Equal(alternateInputSnapshot[12..], secondAlternateLease.Span.Input[..24]);

        using RfBlockStreamDecoder.RfDecodedSpanLease concurrentLease = decoder.ReadLeased(
            stream,
            begin: 24,
            length: 36)!;
        Assert.NotSame(secondAlternateLease.Span.Input, concurrentLease.Span.Input);
        Assert.NotSame(secondAlternateLease.Span.Video, concurrentLease.Span.Video);
    }

    [Fact(DisplayName = "Compact VHS RF spans retain only field-consumed channels")]
    public void CompactVhsRfSpansRetainOnlyFieldConsumedChannels()
    {
        using var stream = new MemoryStream();
        using var fullDecoder = BuildDecoder(
            new CountingSampleLoader(),
            workerThreads: 4,
            weakRfDiagnostics: true);
        using var compactDecoder = BuildDecoder(
            new CountingSampleLoader(),
            workerThreads: 4,
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false);

        RfDecodedSpan full = fullDecoder.Read(stream, begin: 0, length: 24)!;
        RfDecodedSpan compact = compactDecoder.Read(stream, begin: 0, length: 24)!;

        Assert.Equal(full.Video, compact.Video);
        Assert.Equal(full.Envelope, compact.Envelope);
        Assert.Equal(full.VideoLowPass, compact.VideoLowPass);
        Assert.Empty(compact.Input);
        Assert.Empty(compact.DemodRaw);
        Assert.Empty(compact.RfHighPass!);
        Assert.Equal(24, compact.AvailableSampleCountOverride);
    }

    [Fact(DisplayName = "Compact VHS RF spans widen float32 chroma exactly once during assembly")]
    public void CompactVhsRfSpansWidenFloat32ChromaDuringAssembly()
    {
        using var stream = new MemoryStream();
        using var fullDecoder = BuildDecoder(
            new CountingSampleLoader(),
            workerThreads: 4,
            float32Chroma: true);
        using var compactDecoder = BuildDecoder(
            new CountingSampleLoader(),
            workerThreads: 4,
            retainRfDiagnosticChannels: false,
            float32Chroma: true);

        RfDecodedSpan full = fullDecoder.Read(stream, begin: 0, length: 24)!;
        RfDecodedSpan compact = compactDecoder.Read(stream, begin: 0, length: 24)!;

        Assert.NotNull(full.Chroma);
        Assert.NotNull(compact.Chroma);
        Assert.Equal(full.Chroma, compact.Chroma);
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
        WaitForReadCount(loader, 4);
        Assert.Equal(4, loader.ReadCount);
        Assert.Equal(2, warningCount);

        RfDecodedSpan second = decoder.Read(stream, begin: 12, length: 24)!;

        Assert.InRange(loader.ReadCount, 4, 5);
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
        WaitForReadCount(loader, 4);
        RfDecodedSpan second = decoder.Read(secondStream, begin: 0, length: 24)!;
        WaitForReadCount(loader, 8);

        Assert.Equal(8, loader.ReadCount);
        Assert.Equal(Enumerable.Range(2, 24).Select(value => (double)value), second.Input);
        Assert.InRange(decoder.CachedDecodedBlockCount, 1, 18);
        Assert.InRange(decoder.CachedPrefetchedBlockCount, 0, decoder.PrefetchBlocks);

        var failingLoader = new FailsAfterReadCountLoader(successfulReads: 2);
        using var failingDecoder = BuildDecoder(failingLoader, workerThreads: 4, prefetchBlocks: 2);
        RfDecodedSpan completed = failingDecoder.Read(firstStream, begin: 0, length: 24)!;
        WaitForReadCount(failingLoader, 3);
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
        Assert.Equal(RfBlockStreamDecoder.MaximumConcurrentPrefetchBlocks, decoder.PrefetchWorkerThreads);
        Assert.True(decoder.PrefetchWorkerThreads < decoder.PrefetchBlocks);
        _ = decoder.Read(stream, begin: 0, length: 24);
        RfBlockStreamDecoder.RfDecodedSpanLease lease = decoder.ReadLeased(
            stream,
            begin: 12,
            length: 24)!;
        Assert.InRange(
            decoder.CachedDecodedBlockCount,
            1,
            16 + RfBlockStreamDecoder.MaximumPrefetchBlocks);
        decoder.Dispose();
        lease.Dispose();
        decoder.Dispose();

        Assert.Equal(0, decoder.CachedReusableSpanBufferSetCount);
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

        Assert.InRange(loader.ReadCount, 257, 257 + decoder.PrefetchBlocks);
    }

    [Fact(DisplayName = "Maximum RF prefetch remains bounded across a sustained forward decode")]
    public void MaximumRfBlockPrefetchRemainsBoundedAcrossSustainedForwardDecode()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(
            loader,
            workerThreads: 100,
            prefetchBlocks: int.MaxValue);

        for (int field = 0; field < 256; field++)
        {
            RfDecodedSpan span = decoder.Read(stream, begin: field * 12L, length: 24)!;
            Assert.Equal(24, span.Input.Length);
            Assert.InRange(
                decoder.CachedDecodedBlockCount,
                1,
                16 + RfBlockStreamDecoder.MaximumPrefetchBlocks);
            Assert.InRange(
                decoder.CachedPrefetchedBlockCount,
                0,
                RfBlockStreamDecoder.MaximumPrefetchBlocks);
        }

        Assert.Equal(RfBlockStreamDecoder.MaximumPrefetchBlocks, decoder.PrefetchBlocks);
        Assert.Equal(
            RfBlockStreamDecoder.MaximumConcurrentPrefetchBlocks,
            decoder.PrefetchWorkerThreads);
        Assert.InRange(
            loader.ReadCount,
            257,
            257 + RfBlockStreamDecoder.MaximumPrefetchBlocks);
    }

    [Fact(DisplayName = "RF prefetch publishes required blocks before a later read completes")]
    public async Task RfPrefetchPublishesRequiredBlocksBeforeALaterReadCompletes()
    {
        using var loader = new BlockingFutureSampleLoader(blockedSample: 36);
        using var stream = new MemoryStream();
        using var decoder = BuildDecoder(loader, workerThreads: 4, prefetchBlocks: 2);

        try
        {
            RfDecodedSpan first = decoder.Read(stream, begin: 0, length: 24)!;
            await loader.Blocked.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Task<RfDecodedSpan?> secondRead = Task.Run(
                () => decoder.Read(stream, begin: 12, length: 24),
                TestContext.Current.CancellationToken);
            RfDecodedSpan second = (await secondRead.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken))!;

            Assert.Equal(first.Input[12..], second.Input[..12]);
            Assert.Equal(1, loader.MaximumConcurrentReads);
        }
        finally
        {
            loader.Release();
        }
    }

    private static RfBlockStreamDecoder BuildDecoder(
        IRfSampleLoader loader,
        int workerThreads,
        int prefetchBlocks = 0,
        bool weakRfDiagnostics = false,
        bool optionalOutputs = false,
        Action<string, string>? diagnosticLogger = null,
        bool retainRfDiagnosticChannels = true,
        bool float32Chroma = false)
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
        if (optionalOutputs)
        {
            filters = filters with
            {
                LdEfm = identity,
                LdEfmMagnitude = ones,
                ChromaBurst = identity,
                ChromaBurstMagnitude = ones,
                LdVideoBurst = identity,
                LdVideoBurstMagnitude = ones,
                LdVideoPilot = identity,
                LdVideoPilotMagnitude = ones
            };
        }

        if (weakRfDiagnostics)
        {
            filters = filters with
            {
                VhsEnvelopeSos = [new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)]
            };
        }

        if (float32Chroma)
        {
            filters = filters with
            {
                ChromaBurstSos = [new SosSection(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)]
            };
        }

        var pipeline = new RfBlockDecodePipeline(
            loader,
            filters,
            sampleRateHz: 16.0,
            filterOptions: weakRfDiagnostics
                ? new DecodeFilterOptions(FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation)
                : null,
            diagnosticLogger: diagnosticLogger,
            retainRfDiagnosticChannels: retainRfDiagnosticChannels);
        return new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads,
            prefetchBlocks);
    }

    private static void WaitForReadCount(CountingSampleLoader loader, int expected)
        => Assert.True(SpinWait.SpinUntil(
            () => loader.ReadCount >= expected,
            TimeSpan.FromSeconds(5)));

    private static void WaitForReadCount(FailsAfterReadCountLoader loader, int expected)
        => Assert.True(SpinWait.SpinUntil(
            () => loader.ReadCount >= expected,
            TimeSpan.FromSeconds(5)));

    private sealed class CountingSampleLoader : IRfSampleLoader
    {
        private readonly bool _returnZeros;
        private int _readCount;

        public CountingSampleLoader(bool returnZeros = false)
        {
            _returnZeros = returnZeros;
        }

        public int ReadCount => Volatile.Read(ref _readCount);

        public double[] Read(Stream stream, long sample, int readLength)
        {
            Interlocked.Increment(ref _readCount);
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
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public double[] Read(Stream stream, long sample, int readLength)
        {
            int readCount = Interlocked.Increment(ref _readCount);
            if (readCount > successfulReads)
            {
                throw new IOException("Synthetic loader failure.");
            }

            return Enumerable.Range(0, readLength)
                .Select(index => (double)(sample + index))
                .ToArray();
        }
    }

    private sealed class BlockingFutureSampleLoader(long blockedSample) : IRfSampleLoader, IDisposable
    {
        private readonly TaskCompletionSource<bool> _blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _activeReads;
        private int _maximumConcurrentReads;

        internal Task Blocked => _blocked.Task;

        internal int MaximumConcurrentReads => Volatile.Read(ref _maximumConcurrentReads);

        public double[] Read(Stream stream, long sample, int readLength)
        {
            int activeReads = Interlocked.Increment(ref _activeReads);
            UpdateMaximum(activeReads);
            try
            {
                if (sample == blockedSample)
                {
                    _blocked.TrySetResult(true);
                    _release.Wait(TimeSpan.FromSeconds(10));
                }

                return Enumerable.Range(0, readLength)
                    .Select(index => (double)(sample + index))
                    .ToArray();
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }

        private void UpdateMaximum(int candidate)
        {
            int current;
            while (candidate > (current = Volatile.Read(ref _maximumConcurrentReads))
                && Interlocked.CompareExchange(ref _maximumConcurrentReads, candidate, current) != current)
            {
            }
        }
    }
}
