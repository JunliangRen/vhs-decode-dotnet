using System.Numerics;
using VHSDecode.Core.Compute.Cuda;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class RfComputeBackendSelectorTests
{
    [Fact]
    public void ExplicitCpuBypassesCudaDiscovery()
    {
        var loader = new CountingFailingLoader();
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength: 16);

        RfComputeBackendSelection selection = RfComputeBackendSelector.Select(
            BuildOptions(RfComputeBackend.Cpu),
            pipeline,
            blockLength: 16,
            new CudaBackendProbe(loader),
            automaticCudaPromotionEnabled: true);

        Assert.Null(selection.Backend);
        Assert.Equal(0, loader.LoadCount);
        Assert.Contains("explicit request", selection.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoUsesCpuUntilReleasePerformanceGateIsPromoted()
    {
        var loader = new CountingFailingLoader();
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength: 16);

        RfComputeBackendSelection selection = RfComputeBackendSelector.Select(
            BuildOptions(RfComputeBackend.Auto),
            pipeline,
            blockLength: 16,
            new CudaBackendProbe(loader));

        Assert.False(RfComputeBackendSelector.AutomaticCudaPromotionEnabled);
        Assert.Null(selection.Backend);
        Assert.Equal(0, loader.LoadCount);
        Assert.Contains("1.25x", selection.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotedAutoFallsBackBeforeDecodeWhenCudaPreflightFails()
    {
        var loader = new CountingFailingLoader();
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength: 16);

        RfComputeBackendSelection selection = RfComputeBackendSelector.Select(
            BuildOptions(RfComputeBackend.Auto),
            pipeline,
            blockLength: 16,
            new CudaBackendProbe(loader),
            automaticCudaPromotionEnabled: true);

        Assert.Null(selection.Backend);
        Assert.Equal(1, loader.LoadCount);
        Assert.Contains("CUDA preflight failed", selection.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredCudaFailsBeforeDecodeWhenComponentIsMissing()
    {
        var loader = new CountingFailingLoader();
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength: 16);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            RfComputeBackendSelector.Select(
                BuildOptions(RfComputeBackend.Cuda),
                pipeline,
                blockLength: 16,
                new CudaBackendProbe(loader)));

        Assert.Equal(1, loader.LoadCount);
        Assert.Contains("requested but preflight failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("test sidecar missing", error.Message, StringComparison.Ordinal);
    }

    private static DecodeExecutionOptions BuildOptions(RfComputeBackend backend) =>
        new(
            RequestedThreads: 1,
            WorkerThreads: 1,
            SeekFrame: -BigInteger.One,
            WriteDebugData: false,
            Debug: false,
            DebugPlotPath: null,
            IgnoreLeadOut: false,
            VerboseVits: false,
            UseProfiler: false,
            CxAdcCompatibilityMode: false,
            ComputeBackend: backend,
            CudaDevice: 0);

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

    private sealed class CountingFailingLoader : ICudaNativeApiLoader
    {
        internal int LoadCount { get; private set; }

        public CudaNativeApiLoadResult Load(string? componentDirectory)
        {
            LoadCount++;
            return CudaNativeApiLoadResult.Failed(
                CudaBackendFailure.ComponentMissing,
                "test sidecar missing");
        }
    }
}
