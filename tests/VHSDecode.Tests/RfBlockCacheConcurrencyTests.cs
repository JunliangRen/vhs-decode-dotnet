using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfBlockCacheConcurrencyTests
{
    private const int TestBlockLength = 16;

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

        using var highConcurrencyDecoder = BuildDecoder(
            new CountingSampleLoader(),
            workerThreads: 20,
            prefetchBlocks: RfBlockStreamDecoder.RecommendedPrefetchBlocks(20, 20));
        Assert.Equal(28, highConcurrencyDecoder.PrefetchBlocks);
        Assert.Equal(
            RfBlockStreamDecoder.MaximumConcurrentPrefetchBlocks,
            highConcurrencyDecoder.PrefetchWorkerThreads);
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

    [Fact(DisplayName = "Compact packed LDS inputs are returned after parallel decode")]
    public void CompactPackedLdsInputsAreReturnedAfterParallelDecode()
    {
        int[] samples = Enumerable.Range(0, 256)
            .Select(index => (index * 73) % 1024)
            .ToArray();
        byte[] packed = Pack4x10(samples);
        var fullLoader = new PackedDdD4To40SampleLoader();
        var compactLoader = new PackedDdD4To40SampleLoader();
        using var fullStream = new MemoryStream(packed, writable: false);
        using var compactStream = new MemoryStream(packed, writable: false);
        using var fullDecoder = BuildDecoder(fullLoader, workerThreads: 4);
        using var compactDecoder = BuildDecoder(
            compactLoader,
            workerThreads: 4,
            retainRfDiagnosticChannels: false);

        RfDecodedSpan full = fullDecoder.Read(fullStream, begin: 0, length: 24)!;
        RfDecodedSpan compact = compactDecoder.Read(compactStream, begin: 0, length: 24)!;

        Assert.Equal(full.Video, compact.Video);
        Assert.Equal(full.Envelope, compact.Envelope);
        Assert.Equal(full.VideoLowPass, compact.VideoLowPass);
        Assert.Empty(compact.Input);
        Assert.InRange(
            compactLoader.CachedReusableDecodedBufferCount,
            1,
            PackedDdD4To40SampleLoader.MaximumRetainedDecodedBufferCount);
        Assert.Equal(0, fullLoader.CachedReusableDecodedBufferCount);
    }

    [Fact(DisplayName = "Compact RF input reuse can be limited to parallel decode")]
    public void CompactRfInputReuseCanBeLimitedToParallelDecode()
    {
        var serialLoader = new PolicySampleLoader(reuseForSequentialDecode: false);
        using (var serialDecoder = BuildDecoder(
            serialLoader,
            workerThreads: 1,
            retainRfDiagnosticChannels: false))
        {
            Assert.NotNull(serialDecoder.Read(Stream.Null, begin: 0, length: 24));
        }

        Assert.True(serialLoader.ReadCount > 0);
        Assert.Equal(0, serialLoader.ReusableReadCount);
        Assert.Equal(0, serialLoader.ReturnCount);

        var parallelLoader = new PolicySampleLoader(reuseForSequentialDecode: false);
        using (var parallelDecoder = BuildDecoder(
            parallelLoader,
            workerThreads: 4,
            retainRfDiagnosticChannels: false))
        {
            Assert.NotNull(parallelDecoder.Read(Stream.Null, begin: 0, length: 24));
        }

        Assert.Equal(0, parallelLoader.ReadCount);
        Assert.True(parallelLoader.ReusableReadCount > 0);
        Assert.Equal(parallelLoader.ReusableReadCount, parallelLoader.ReturnCount);
    }

    [Fact(DisplayName = "Parallel decode failures return every compact packed LDS input")]
    public void ParallelDecodeFailuresReturnEveryCompactPackedLdsInput()
    {
        int[] samples = Enumerable.Range(0, 256)
            .Select(index => (index * 73) % 1024)
            .ToArray();
        byte[] packed = Pack4x10(samples);
        var loader = new PackedDdD4To40SampleLoader();
        using var stream = new MemoryStream(packed, writable: false);
        using var decoder = BuildDecoder(
            loader,
            workerThreads: 4,
            retainRfDiagnosticChannels: false,
            fmDemodulatorMode: (RfFmDemodulatorMode)int.MaxValue);

        _ = Assert.ThrowsAny<Exception>(() => decoder.Read(stream, begin: 0, length: 24));

        Assert.InRange(
            loader.CachedReusableDecodedBufferCount,
            1,
            PackedDdD4To40SampleLoader.MaximumRetainedDecodedBufferCount);
    }

    [Fact(DisplayName = "Partial parallel failures return completed compact VHS outputs")]
    public void PartialParallelFailuresReturnCompletedCompactVhsOutputs()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        int diagnosticCalls = 0;
        RfBlockDecodePipeline? pipeline = null;
        pipeline = BuildPipeline(
            loader,
            weakRfDiagnostics: true,
            diagnosticLogger: (_, _) =>
            {
                Interlocked.Increment(ref diagnosticCalls);
                if (!SpinWait.SpinUntil(
                        () => pipeline!.CreatedStreamOutputBufferSetCount >= 2,
                        TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Parallel RF workers did not all start.");
                }

                throw new InvalidOperationException("Synthetic diagnostic failure.");
            },
            retainRfDiagnosticChannels: false,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using (pipeline)
        using (var decoder = new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 2))
        {
            _ = Assert.ThrowsAny<Exception>(() =>
                decoder.Read(stream, begin: 0, length: 24));

            Assert.Equal(1, diagnosticCalls);
            Assert.Equal(2, pipeline.CreatedStreamOutputBufferSetCount);
            Assert.Equal(
                pipeline.CreatedStreamOutputBufferSetCount,
                pipeline.RetainedStreamOutputBufferSetCount);
        }
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

    [Fact(DisplayName = "Compact VHS stream outputs reuse only after block release")]
    public void CompactVhsStreamOutputsReuseOnlyAfterBlockRelease()
    {
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            new CountingSampleLoader(),
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        double[] input = Enumerable.Range(0, TestBlockLength)
            .Select(index => Math.Sin(index * 0.19) + (0.2 * Math.Cos(index * 0.31)))
            .ToArray();

        RfPipelineBlock fullFirst = pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
        RfPipelineBlock fullSecond = pipeline.DecodePreparedBlock(input, reportDiagnostics: false);
        Assert.NotSame(fullFirst.Demodulated.Video, fullSecond.Demodulated.Video);
        Assert.NotSame(fullFirst.Demodulated.Envelope, fullSecond.Demodulated.Envelope);
        Assert.NotSame(fullFirst.Demodulated.VideoLowPass, fullSecond.Demodulated.VideoLowPass);

        RfPipelineBlock first = pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false);
        double[] expectedVideo = first.Demodulated.Video.ToArray();
        double[] expectedEnvelope = first.Demodulated.Envelope.ToArray();
        double[] expectedVideoLowPass = first.Demodulated.VideoLowPass.ToArray();
        float[] expectedChroma = Assert.IsType<float[]>(first.Demodulated.ChromaFloat32).ToArray();

        RfPipelineBlock second = pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false);
        Assert.NotSame(first.Demodulated.Video, second.Demodulated.Video);
        Assert.NotSame(first.Demodulated.Envelope, second.Demodulated.Envelope);
        Assert.NotSame(first.Demodulated.VideoLowPass, second.Demodulated.VideoLowPass);
        Assert.NotSame(first.Demodulated.ChromaFloat32, second.Demodulated.ChromaFloat32);
        Assert.Equal(2, pipeline.CreatedStreamOutputBufferSetCount);
        Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);

        pipeline.ReleaseStreamBlock(first);
        pipeline.ReleaseStreamBlock(first);
        Assert.Equal(1, pipeline.RetainedStreamOutputBufferSetCount);

        RfPipelineBlock reused = pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false);
        Assert.Same(first.Demodulated.Video, reused.Demodulated.Video);
        Assert.Same(first.Demodulated.Envelope, reused.Demodulated.Envelope);
        Assert.Same(first.Demodulated.VideoLowPass, reused.Demodulated.VideoLowPass);
        Assert.Same(first.Demodulated.ChromaFloat32, reused.Demodulated.ChromaFloat32);
        AssertDoubleBitsEqual(expectedVideo, reused.Demodulated.Video);
        AssertDoubleBitsEqual(expectedEnvelope, reused.Demodulated.Envelope);
        AssertDoubleBitsEqual(expectedVideoLowPass, reused.Demodulated.VideoLowPass);
        AssertFloatBitsEqual(
            expectedChroma,
            Assert.IsType<float[]>(reused.Demodulated.ChromaFloat32));
        Assert.Equal(2, pipeline.CreatedStreamOutputBufferSetCount);

        pipeline.ReleaseStreamBlock(second);
        pipeline.ReleaseStreamBlock(reused);
        Assert.Equal(2, pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Fact(DisplayName = "Compact VHS cache invalidation returns stream output buffers")]
    public void CompactVhsCacheInvalidationReturnsStreamOutputBuffers()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            loader,
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 4);

        RfDecodedSpan first = decoder.Read(stream, begin: 0, length: 24)!;
        double[] expectedVideo = first.Video.ToArray();
        double[] expectedEnvelope = first.Envelope!.ToArray();
        double[] expectedVideoLowPass = first.VideoLowPass!.ToArray();
        int created = pipeline.CreatedStreamOutputBufferSetCount;
        Assert.Equal(2, created);
        Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);

        decoder.InvalidateCachedBlocks();
        Assert.Equal(created, pipeline.RetainedStreamOutputBufferSetCount);

        RfDecodedSpan second = decoder.Read(stream, begin: 0, length: 24)!;
        AssertDoubleBitsEqual(expectedVideo, second.Video);
        AssertDoubleBitsEqual(expectedEnvelope, second.Envelope!);
        AssertDoubleBitsEqual(expectedVideoLowPass, second.VideoLowPass!);
        Assert.Equal(created, pipeline.CreatedStreamOutputBufferSetCount);
        Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);

        decoder.InvalidateCachedBlocks();
        Assert.Equal(created, pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Fact(DisplayName = "Serial compact VHS cache eviction keeps the current block alive through assembly")]
    public void SerialCompactVhsCacheEvictionKeepsCurrentBlockAliveThroughAssembly()
    {
        using var stream = new MemoryStream();
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            new CountingSampleLoader(),
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 1);

        using var referenceStream = new MemoryStream();
        using RfBlockDecodePipeline referencePipeline = BuildPipeline(
            new CountingSampleLoader(),
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var referenceDecoder = new RfBlockStreamDecoder(
            referencePipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 1);

        _ = decoder.Read(stream, begin: 0, length: 20 * 12);
        Assert.Equal(16, decoder.CachedDecodedBlockCount);
        Assert.Equal(17, pipeline.CreatedStreamOutputBufferSetCount);
        Assert.Equal(1, pipeline.RetainedStreamOutputBufferSetCount);

        RfDecodedSpan actual = decoder.Read(stream, begin: 0, length: 12)!;
        RfDecodedSpan expected = referenceDecoder.Read(referenceStream, begin: 0, length: 12)!;

        AssertDoubleBitsEqual(expected.Video, actual.Video);
        AssertDoubleBitsEqual(expected.Envelope!, actual.Envelope!);
        AssertDoubleBitsEqual(expected.VideoLowPass!, actual.VideoLowPass!);
        AssertDoubleBitsEqual(expected.Chroma!, actual.Chroma!);
        Assert.Equal(16, decoder.CachedDecodedBlockCount);
        Assert.Equal(17, pipeline.CreatedStreamOutputBufferSetCount);
        Assert.Equal(1, pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Fact(DisplayName = "Compact VHS stream output pool remains bounded")]
    public void CompactVhsStreamOutputPoolRemainsBounded()
    {
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            new CountingSampleLoader(),
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        double[] input = Enumerable.Range(0, TestBlockLength)
            .Select(index => Math.Sin(index * 0.19) + (0.2 * Math.Cos(index * 0.31)))
            .ToArray();
        int retainedCapacity = RfBlockDecodePipeline.MaximumRetainedStreamOutputBufferSets;
        RfPipelineBlock[] blocks = Enumerable.Range(0, retainedCapacity + 16)
            .Select(_ => pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false))
            .ToArray();

        Assert.Equal(blocks.Length, pipeline.CreatedStreamOutputBufferSetCount);
        Assert.Equal(
            blocks.Length,
            blocks
                .Select(block => block.Demodulated.Video)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());

        Parallel.ForEach(blocks, pipeline.ReleaseStreamBlock);
        Assert.Equal(retainedCapacity, pipeline.RetainedStreamOutputBufferSetCount);

        RfPipelineBlock[] reused = Enumerable.Range(0, retainedCapacity)
            .Select(_ => pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false))
            .ToArray();
        Assert.Equal(blocks.Length, pipeline.CreatedStreamOutputBufferSetCount);
        Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);

        Parallel.ForEach(reused, pipeline.ReleaseStreamBlock);
        Assert.Equal(retainedCapacity, pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Fact(DisplayName = "Compact VHS stream output release remains safe during disposal")]
    public void CompactVhsStreamOutputReleaseRemainsSafeDuringDisposal()
    {
        RfBlockDecodePipeline pipeline = BuildPipeline(
            new CountingSampleLoader(),
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        try
        {
            double[] input = Enumerable.Range(0, TestBlockLength)
                .Select(index => Math.Sin(index * 0.19) + (0.2 * Math.Cos(index * 0.31)))
                .ToArray();
            RfPipelineBlock shared = pipeline.DecodePreparedStreamBlock(
                input,
                reportDiagnostics: false);

            Parallel.For(0, 64, _ => pipeline.ReleaseStreamBlock(shared));
            Assert.Equal(1, pipeline.RetainedStreamOutputBufferSetCount);

            RfPipelineBlock[] active = Enumerable.Range(
                    0,
                    RfBlockDecodePipeline.MaximumRetainedStreamOutputBufferSets)
                .Select(_ => pipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false))
                .ToArray();
            Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);

            Parallel.Invoke(
                pipeline.Dispose,
                () => Parallel.ForEach(active, pipeline.ReleaseStreamBlock),
                () => Parallel.ForEach(active, pipeline.ReleaseStreamBlock));

            Assert.Equal(0, pipeline.RetainedStreamOutputBufferSetCount);
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact(DisplayName = "Cancelled compact VHS prefetch returns completed stream outputs")]
    public void CancelledCompactVhsPrefetchReturnsCompletedStreamOutputs()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            loader,
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 4,
            prefetchBlocks: 2);

        _ = decoder.Read(stream, begin: 0, length: 24);
        WaitForReadCount(loader, expected: 4);
        Assert.True(SpinWait.SpinUntil(
            () => decoder.CachedPrefetchedBlockCount == 2,
            TimeSpan.FromSeconds(5)));
        int created = pipeline.CreatedStreamOutputBufferSetCount;
        Assert.Equal(4, created);

        decoder.InvalidateCachedBlocks();
        Assert.Equal(created, pipeline.RetainedStreamOutputBufferSetCount);

        _ = decoder.Read(stream, begin: 0, length: 24);
        WaitForReadCount(loader, expected: 8);
        Assert.True(SpinWait.SpinUntil(
            () => decoder.CachedPrefetchedBlockCount == 2,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(created, pipeline.CreatedStreamOutputBufferSetCount);

        decoder.InvalidateCachedBlocks();
        Assert.Equal(created, pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Fact(DisplayName = "In-flight compact VHS prefetch cancellation returns every stream output")]
    public async Task InFlightCompactVhsPrefetchCancellationReturnsEveryStreamOutput()
    {
        using var loader = new BlockingFutureSampleLoader(blockedSample: 36);
        using var stream = new MemoryStream();
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            loader,
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 4,
            prefetchBlocks: 2);

        _ = decoder.Read(stream, begin: 0, length: 24);
        await loader.Blocked.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(
            () => pipeline.CreatedStreamOutputBufferSetCount >= 3,
            TimeSpan.FromSeconds(5)));
        int created = pipeline.CreatedStreamOutputBufferSetCount;
        int cancellationCount = decoder.PrefetchCancellationCount;
        Task invalidation = Task.Run(
            decoder.InvalidateCachedBlocks,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => decoder.PrefetchCancellationCount > cancellationCount,
                TimeSpan.FromSeconds(5)));
            Assert.False(invalidation.IsCompleted);
        }
        finally
        {
            loader.Release();
        }

        await invalidation.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(created, pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Fact(DisplayName = "Throwing deferred diagnostics return harvested compact VHS outputs")]
    public async Task ThrowingDeferredDiagnosticsReturnHarvestedCompactVhsOutputs()
    {
        using var loader = new BlockingFutureSampleLoader(
            blockedSample: 36,
            returnZeros: true);
        using var stream = new MemoryStream();
        int diagnosticCalls = 0;
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            loader,
            weakRfDiagnostics: true,
            diagnosticLogger: (_, _) =>
            {
                if (Interlocked.Increment(ref diagnosticCalls) == 3)
                {
                    throw new InvalidOperationException("Synthetic deferred diagnostic failure.");
                }
            },
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 4,
            prefetchBlocks: 2);

        _ = decoder.Read(stream, begin: 0, length: 24);
        await loader.Blocked.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(
            () => decoder.CachedPrefetchedBlockCount == 1,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(3, pipeline.CreatedStreamOutputBufferSetCount);

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => decoder.Read(stream, begin: 24, length: 12));
            Assert.Equal("Synthetic deferred diagnostic failure.", exception.Message);
            Assert.Equal(3, diagnosticCalls);
        }
        finally
        {
            loader.Release();
        }

        decoder.InvalidateCachedBlocks();
        Assert.Equal(
            pipeline.CreatedStreamOutputBufferSetCount,
            pipeline.RetainedStreamOutputBufferSetCount);
    }

    [Fact(DisplayName = "Compact VHS prefetch keeps selected blocks alive through span assembly")]
    public void CompactVhsPrefetchKeepsSelectedBlocksAliveThroughSpanAssembly()
    {
        var loader = new CountingSampleLoader();
        using var stream = new MemoryStream();
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            loader,
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 4,
            prefetchBlocks: 2);

        using var referenceStream = new MemoryStream();
        using RfBlockDecodePipeline referencePipeline = BuildPipeline(
            new CountingSampleLoader(),
            weakRfDiagnostics: true,
            retainRfDiagnosticChannels: false,
            float32Chroma: true,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);
        using var referenceDecoder = new RfBlockStreamDecoder(
            referencePipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 1);

        _ = decoder.Read(stream, begin: 0, length: 18 * 12);
        WaitForReadCount(loader, expected: 20);
        Assert.True(SpinWait.SpinUntil(
            () => decoder.CachedPrefetchedBlockCount == 2,
            TimeSpan.FromSeconds(5)));

        RfDecodedSpan actual = decoder.Read(stream, begin: 0, length: 22 * 12)!;
        RfDecodedSpan expected = referenceDecoder.Read(
            referenceStream,
            begin: 0,
            length: 22 * 12)!;

        AssertDoubleBitsEqual(expected.Video, actual.Video);
        AssertDoubleBitsEqual(expected.Envelope!, actual.Envelope!);
        AssertDoubleBitsEqual(expected.VideoLowPass!, actual.VideoLowPass!);
        AssertDoubleBitsEqual(expected.Chroma!, actual.Chroma!);
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
        bool float32Chroma = false,
        RfFmDemodulatorMode? fmDemodulatorMode = null)
    {
        RfBlockDecodePipeline pipeline = BuildPipeline(
            loader,
            weakRfDiagnostics,
            optionalOutputs,
            diagnosticLogger,
            retainRfDiagnosticChannels,
            float32Chroma,
            fmDemodulatorMode);
        return new RfBlockStreamDecoder(
            pipeline,
            TestBlockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads,
            prefetchBlocks);
    }

    private static RfBlockDecodePipeline BuildPipeline(
        IRfSampleLoader loader,
        bool weakRfDiagnostics = false,
        bool optionalOutputs = false,
        Action<string, string>? diagnosticLogger = null,
        bool retainRfDiagnosticChannels = true,
        bool float32Chroma = false,
        RfFmDemodulatorMode? fmDemodulatorMode = null)
    {
        Complex[] identity = RfDemodulator.IdentityFilter(TestBlockLength);
        double[] ones = Enumerable.Repeat(1.0, TestBlockLength).ToArray();
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
            filterOptions: weakRfDiagnostics || fmDemodulatorMode.HasValue
                ? new DecodeFilterOptions(
                    FmDemodulatorMode: fmDemodulatorMode ?? RfFmDemodulatorMode.VhsRustApproximation)
                : null,
            diagnosticLogger: diagnosticLogger,
            retainRfDiagnosticChannels: retainRfDiagnosticChannels);
        return pipeline;
    }

    private static void WaitForReadCount(CountingSampleLoader loader, int expected)
        => Assert.True(SpinWait.SpinUntil(
            () => loader.ReadCount >= expected,
            TimeSpan.FromSeconds(5)));

    private static void WaitForReadCount(FailsAfterReadCountLoader loader, int expected)
        => Assert.True(SpinWait.SpinUntil(
            () => loader.ReadCount >= expected,
            TimeSpan.FromSeconds(5)));

    private static byte[] Pack4x10(int[] samples)
    {
        if (samples.Length % 4 != 0)
        {
            throw new ArgumentException("Sample count must be divisible by four.", nameof(samples));
        }

        byte[] output = new byte[(samples.Length / 4) * 5];
        for (int group = 0; group < samples.Length / 4; group++)
        {
            int s0 = samples[group * 4] & 0x3FF;
            int s1 = samples[(group * 4) + 1] & 0x3FF;
            int s2 = samples[(group * 4) + 2] & 0x3FF;
            int s3 = samples[(group * 4) + 3] & 0x3FF;
            int i = group * 5;
            output[i] = (byte)(s0 >> 2);
            output[i + 1] = (byte)(((s0 & 0x03) << 6) | (s1 >> 4));
            output[i + 2] = (byte)(((s1 & 0x0F) << 4) | (s2 >> 6));
            output[i + 3] = (byte)(((s2 & 0x3F) << 2) | (s3 >> 8));
            output[i + 4] = (byte)(s3 & 0xFF);
        }

        return output;
    }

    private static void AssertDoubleBitsEqual(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Double sequences differ at the bit level.");
    }

    private static void AssertFloatBitsEqual(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Float sequences differ at the bit level.");
    }

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

    private sealed class PolicySampleLoader(bool reuseForSequentialDecode)
        : IReusableRfSampleLoader
    {
        private int _readCount;
        private int _reusableReadCount;
        private int _returnCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public int ReusableReadCount => Volatile.Read(ref _reusableReadCount);

        public int ReturnCount => Volatile.Read(ref _returnCount);

        bool IReusableRfSampleLoader.ReuseForSequentialDecode => reuseForSequentialDecode;

        public double[] Read(Stream stream, long sample, int readLength)
        {
            Interlocked.Increment(ref _readCount);
            return CreateSamples(sample, readLength);
        }

        double[] IReusableRfSampleLoader.ReadReusable(
            Stream stream,
            long sample,
            int readLength)
        {
            Interlocked.Increment(ref _reusableReadCount);
            return CreateSamples(sample, readLength);
        }

        void IReusableRfSampleLoader.ReturnReusable(double[] buffer)
            => Interlocked.Increment(ref _returnCount);

        private static double[] CreateSamples(long sample, int readLength)
            => Enumerable.Range(0, readLength)
                .Select(index => (double)(sample + index))
                .ToArray();
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

    private sealed class BlockingFutureSampleLoader(
        long blockedSample,
        bool returnZeros = false) : IRfSampleLoader, IDisposable
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

                return returnZeros
                    ? new double[readLength]
                    : Enumerable.Range(0, readLength)
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
