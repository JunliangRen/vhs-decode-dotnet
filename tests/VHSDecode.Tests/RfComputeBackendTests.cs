using System.Numerics;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfComputeBackendTests
{
    [Fact]
    public void StreamDecoderUsesOneOrderedBackendBatchForMissingBlocks()
    {
        const int blockLength = 16;
        const int blockCut = 2;
        const int blockCutEnd = 2;
        byte[] input = BuildPcm16Bytes(Enumerable.Range(0, 96).Select(value => (short)value));

        using RfBlockDecodePipeline cpuPipeline = BuildPipeline(blockLength);
        using var cpuDecoder = new RfBlockStreamDecoder(
            cpuPipeline,
            blockLength,
            blockCut,
            blockCutEnd,
            workerThreads: 4);
        RfDecodedSpan expected = cpuDecoder.Read(new MemoryStream(input), begin: 5, length: 25)!;

        using RfBlockDecodePipeline acceleratedPipeline = BuildPipeline(blockLength);
        var backend = new RecordingComputeBackend();
        using var acceleratedDecoder = new RfBlockStreamDecoder(
            acceleratedPipeline,
            blockLength,
            blockCut,
            blockCutEnd,
            workerThreads: 4,
            prefetchBlocks: 0,
            computeBackend: backend);
        RfDecodedSpan actual = acceleratedDecoder.Read(new MemoryStream(input), begin: 5, length: 25)!;

        Assert.Equal("test-accelerator", acceleratedDecoder.ComputeBackendName);
        Assert.True(acceleratedDecoder.UsesHardwareAcceleration);
        Assert.Equal(0, acceleratedDecoder.PrefetchBlocks);
        Assert.Equal([3], backend.BatchSizes);
        Assert.Equal(expected.Input, actual.Input);
        Assert.Equal(expected.Video, actual.Video);
        Assert.Equal(expected.DemodRaw, actual.DemodRaw);
        Assert.Equal(expected.Envelope, actual.Envelope);
        Assert.Equal(expected.VideoLowPass, actual.VideoLowPass);
        Assert.Equal(expected.RfHighPass, actual.RfHighPass);
    }

    [Fact]
    public void StreamDecoderSplitsPreparedBlocksAtBackendMemoryLimit()
    {
        const int blockLength = 16;
        const int blockCut = 2;
        const int blockCutEnd = 2;
        byte[] input = BuildPcm16Bytes(Enumerable.Range(0, 96).Select(value => (short)value));

        using RfBlockDecodePipeline cpuPipeline = BuildPipeline(blockLength);
        using var cpuDecoder = new RfBlockStreamDecoder(
            cpuPipeline,
            blockLength,
            blockCut,
            blockCutEnd,
            workerThreads: 4);
        RfDecodedSpan expected = cpuDecoder.Read(new MemoryStream(input), begin: 5, length: 25)!;

        using RfBlockDecodePipeline acceleratedPipeline = BuildPipeline(blockLength);
        var backend = new RecordingComputeBackend(maximumBatchSize: 2);
        using var acceleratedDecoder = new RfBlockStreamDecoder(
            acceleratedPipeline,
            blockLength,
            blockCut,
            blockCutEnd,
            workerThreads: 4,
            prefetchBlocks: 0,
            computeBackend: backend);
        RfDecodedSpan actual = acceleratedDecoder.Read(
            new MemoryStream(input),
            begin: 5,
            length: 25)!;

        Assert.Equal([3], backend.BatchLimitRequests);
        Assert.Equal([2, 1], backend.BatchSizes);
        Assert.Equal(expected.Input, actual.Input);
        Assert.Equal(expected.Video, actual.Video);
        Assert.Equal(expected.DemodRaw, actual.DemodRaw);
        Assert.Equal(expected.Envelope, actual.Envelope);
        Assert.Equal(expected.VideoLowPass, actual.VideoLowPass);
        Assert.Equal(expected.RfHighPass, actual.RfHighPass);
    }

    [Fact]
    public void StreamDecoderRoutesSingleBlockThroughSelectedBackend()
    {
        const int blockLength = 16;
        byte[] input = BuildPcm16Bytes(Enumerable.Range(0, 32).Select(value => (short)value));
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        var backend = new RecordingComputeBackend();
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 1,
            prefetchBlocks: 0,
            computeBackend: backend);

        Assert.NotNull(decoder.Read(new MemoryStream(input), begin: 2, length: 4));
        Assert.Equal([1], backend.BatchSizes);
    }

    [Fact]
    public void StreamDecoderRejectsBackendResultCountMismatch()
    {
        const int blockLength = 16;
        byte[] input = BuildPcm16Bytes(Enumerable.Range(0, 32).Select(value => (short)value));
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 1,
            prefetchBlocks: 0,
            computeBackend: new WrongCountComputeBackend());

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => decoder.Read(new MemoryStream(input), begin: 2, length: 4));
        Assert.Contains("returned 0 blocks for a 1-block request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwarePrefetchFailureIsNotSuppressedByCacheInvalidation()
    {
        const int blockLength = 16;
        byte[] input = BuildPcm16Bytes(Enumerable.Range(0, 256).Select(value => (short)value));
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        var backend = new FailOnPrefetchComputeBackend();
        using var decoder = new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 2,
            prefetchBlocks: 2,
            computeBackend: backend);

        Assert.NotNull(decoder.Read(new MemoryStream(input), begin: 2, length: 4));
        Assert.True(backend.FailureEntered.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            decoder.InvalidateCachedBlocks);
        Assert.Equal("simulated CUDA prefetch failure", error.Message);
    }

    [Fact]
    public void HardwarePrefetchFailureIsNotSuppressedByDispose()
    {
        const int blockLength = 16;
        byte[] input = BuildPcm16Bytes(Enumerable.Range(0, 256).Select(value => (short)value));
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        var backend = new FailOnPrefetchComputeBackend();
        var decoder = new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 2,
            prefetchBlocks: 2,
            computeBackend: backend);

        Assert.NotNull(decoder.Read(new MemoryStream(input), begin: 2, length: 4));
        Assert.True(backend.FailureEntered.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(decoder.Dispose);
        Assert.Equal("simulated CUDA prefetch failure", error.Message);

        // The first call still releases the backend and marks the decoder as
        // disposed even though it propagates the task-fatal prefetch error.
        decoder.Dispose();
    }

    [Fact]
    public void HardwarePrefetchFailureIsSurfacedBeforeOutputCompletion()
    {
        const int blockLength = 16;
        byte[] input = BuildPcm16Bytes(Enumerable.Range(0, 256).Select(value => (short)value));
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        var backend = new FailOnPrefetchComputeBackend();
        var decoder = new RfBlockStreamDecoder(
            pipeline,
            blockLength,
            blockCut: 2,
            blockCutEnd: 2,
            workerThreads: 2,
            prefetchBlocks: 2,
            computeBackend: backend);

        Assert.NotNull(decoder.Read(new MemoryStream(input), begin: 2, length: 4));
        Assert.True(backend.FailureEntered.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            decoder.CompletePendingHardwareWork);
        Assert.Equal("simulated CUDA prefetch failure", error.Message);

        // The explicit completion boundary consumes and disposes the failed
        // prefetch operation, so ordinary session teardown cannot report a
        // second failure after a caller has already handled it.
        decoder.Dispose();
    }

    private static RfBlockDecodePipeline BuildPipeline(int blockLength)
    {
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
        return new RfBlockDecodePipeline(new Pcm16StreamSampleLoader(), filters, blockLength);
    }

    private static byte[] BuildPcm16Bytes(IEnumerable<short> samples)
    {
        short[] values = samples.ToArray();
        var bytes = new byte[values.Length * sizeof(short)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed class RecordingComputeBackend(int maximumBatchSize = int.MaxValue)
        : IRfBlockComputeBackend
    {
        public string Name => "test-accelerator";

        public bool IsHardwareAccelerated => true;

        public List<int> BatchSizes { get; } = [];

        public List<int> BatchLimitRequests { get; } = [];

        public int GetMaximumBatchSize(int requestedBlockCount)
        {
            BatchLimitRequests.Add(requestedBlockCount);
            return Math.Min(requestedBlockCount, maximumBatchSize);
        }

        public RfPipelineBlock[] DecodeBatch(
            RfBlockDecodePipeline pipeline,
            IReadOnlyList<double[]> preparedInputs,
            bool reportDiagnostics)
        {
            BatchSizes.Add(preparedInputs.Count);
            return preparedInputs
                .Select(input => pipeline.DecodePreparedStreamBlock(input, reportDiagnostics))
                .ToArray();
        }

        public void Dispose()
        {
        }
    }

    private sealed class WrongCountComputeBackend : IRfBlockComputeBackend
    {
        public string Name => "wrong-count";

        public bool IsHardwareAccelerated => true;

        public RfPipelineBlock[] DecodeBatch(
            RfBlockDecodePipeline pipeline,
            IReadOnlyList<double[]> preparedInputs,
            bool reportDiagnostics)
            => [];

        public void Dispose()
        {
        }
    }

    private sealed class FailOnPrefetchComputeBackend : IRfBlockComputeBackend
    {
        private int _calls;

        public string Name => "test-cuda";

        public bool IsHardwareAccelerated => true;

        public ManualResetEventSlim FailureEntered { get; } = new();

        public RfPipelineBlock[] DecodeBatch(
            RfBlockDecodePipeline pipeline,
            IReadOnlyList<double[]> preparedInputs,
            bool reportDiagnostics)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                FailureEntered.Set();
                throw new InvalidOperationException("simulated CUDA prefetch failure");
            }

            return preparedInputs
                .Select(input => pipeline.DecodePreparedStreamBlock(input, reportDiagnostics))
                .ToArray();
        }

        public void Dispose() => FailureEntered.Dispose();
    }
}
