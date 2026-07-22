using System.Numerics;
using VHSDecode.Core.Compute.Cuda;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CudaRfBlockComputeBackendTests
{
    [Fact]
    public void DeviceMemoryLimitUsesConservativeTwoStreamWorkspaceBudget()
    {
        const ulong MiB = 1024UL * 1024UL;

        Assert.Equal(
            32,
            CudaRfBlockComputeBackend.CalculateMemoryLimitedBatchSize(
                requestedBlockCount: 32,
                sampleCount: 32_768,
                includesLdFrequencyWorkspace: false,
                availableBytes: 8UL * 1024 * MiB,
                totalBytes: 12UL * 1024 * MiB));
        Assert.Equal(
            4,
            CudaRfBlockComputeBackend.CalculateMemoryLimitedBatchSize(
                requestedBlockCount: 32,
                sampleCount: 32_768,
                includesLdFrequencyWorkspace: false,
                availableBytes: 600UL * MiB,
                totalBytes: 8UL * 1024 * MiB));
        Assert.Equal(
            3,
            CudaRfBlockComputeBackend.CalculateMemoryLimitedBatchSize(
                requestedBlockCount: 32,
                sampleCount: 32_768,
                includesLdFrequencyWorkspace: true,
                availableBytes: 600UL * MiB,
                totalBytes: 8UL * 1024 * MiB));
        Assert.Equal(
            0,
            CudaRfBlockComputeBackend.CalculateMemoryLimitedBatchSize(
                requestedBlockCount: 1,
                sampleCount: 32_768,
                includesLdFrequencyWorkspace: false,
                availableBytes: 512UL * MiB,
                totalBytes: 8UL * 1024 * MiB));
    }

    [Fact]
    public void StandardBatchMapsNativeOutputsWithoutCpuFallback()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities);
        CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        Assert.True(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason), reason);
        CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);
        using (selectedBackend)
        {
            double[][] inputs =
            [
                Enumerable.Range(0, blockLength).Select(value => (double)value).ToArray(),
                Enumerable.Range(blockLength, blockLength).Select(value => (double)value).ToArray()
            ];
            RfPipelineBlock[] results = selectedBackend.DecodeBatch(pipeline, inputs, reportDiagnostics: false);

            Assert.Equal(2, results.Length);
            Assert.Equal(CudaRfMode.StandardConjugate, api.LastJob!.Mode);
            Assert.Equal(2, api.LastJob.BatchCount);
            Assert.Equal((blockLength / 2) + 1, api.LastJob.RfVideoFilter!.Length);
            Assert.Equal(inputs[0], results[0].Input);
            Assert.Equal(1_000.0, results[0].Demodulated.Video[0]);
            Assert.Equal(2_000.0, results[0].Demodulated.DemodRaw[0]);
            Assert.Equal(3_000.0, results[0].Demodulated.Envelope[0]);
            Assert.Equal(4_000.0, results[0].Demodulated.VideoLowPass[0]);
            Assert.Equal(5_000.0, results[0].Demodulated.RfHighPass[0]);
            Assert.Equal(new Complex(6_000.0, 7_000.0), results[0].Demodulated.Analytic[0]);
        }

        Assert.True(api.Destroyed);
    }

    [Fact]
    public void RuntimeNativeFailureIsFatalAfterBackendSelection()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities) { ExecuteStatus = CudaNativeStatus.CudaError };
        CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        Assert.True(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason), reason);
        CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);
        using (selectedBackend)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => selectedBackend.DecodeBatch(
                    pipeline,
                    [new double[blockLength]],
                    reportDiagnostics: false));
            Assert.Contains("failed after the CUDA backend was selected", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, api.ExecuteCalls);
        }
    }

    [Fact]
    public void MissingVhsModeCapabilityProducesPrecisePreflightFailure()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities & ~CudaBackendCapabilities.RfModeVhsRustApproximation);
        using CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            new DecodeFilterOptions(FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation));

        Assert.False(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason));
        Assert.Null(backend);
        Assert.Contains(nameof(CudaBackendCapabilities.RfModeVhsRustApproximation), reason, StringComparison.Ordinal);
        Assert.False(context.IsDisposed);
    }

    [Fact]
    public void MissingCvbsModeCapabilityProducesPrecisePreflightFailure()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities & ~CudaBackendCapabilities.RfModeCvbs);
        using CudaBackendContext context = CreateContext(api, blockLength);
        var converter = new VideoOutputConverter(
            ire0: 8_100_000.0,
            hzIre: 10_000.0,
            outputZero: 0,
            vsyncIre: -40.0,
            outputScale: 1.0);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            cvbsOptions: new CvbsDecodeOptions(AutoSync: false, converter));

        CudaRfBackendCompatibility compatibility =
            CudaRfBlockComputeBackend.CheckCompatibility(context, pipeline);
        Assert.False(compatibility.IsSupported);
        Assert.Contains(nameof(CudaBackendCapabilities.RfModeCvbs), compatibility.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardModeRejectsNonHermitianCombinedRfResponse()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities);
        using CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            configureRfVideo: response => response[blockLength - 1] = new Complex(0.5, 0.25));

        CudaRfBackendCompatibility compatibility =
            CudaRfBlockComputeBackend.CheckCompatibility(context, pipeline);

        Assert.False(compatibility.IsSupported);
        Assert.Contains("non-Hermitian RF video/MTF response", compatibility.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardModeRefreshesMtfChangedAfterBackendSelection()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities);
        CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(blockLength);
        Assert.True(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason), reason);
        using CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);

        CudaRfPipelineDescriptor descriptor = pipeline.DescribeCudaRfPipeline();
        descriptor.RfMtfFilter[1] = new Complex(2.0, 0.0);
        descriptor.RfMtfFilter[blockLength - 1] = new Complex(2.0, 0.0);
        _ = selectedBackend.DecodeBatch(
            pipeline,
            [new double[blockLength]],
            reportDiagnostics: false);

        Assert.Equal(new Complex(2.0, 0.0), api.LastJob!.MtfFilter![1]);
    }

    [Fact]
    public void CvbsModeRejectsStatefulSharpnessDuringPreflight()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities);
        using CudaBackendContext context = CreateContext(api, blockLength);
        var converter = new VideoOutputConverter(
            ire0: 8_100_000.0,
            hzIre: 10_000.0,
            outputZero: 0,
            vsyncIre: -40.0,
            outputScale: 1.0);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            options: new DecodeFilterOptions(
                SharpnessEq: new SharpnessEqOptions(0.5, 1.0, 0.0, 8)),
            cvbsOptions: new CvbsDecodeOptions(AutoSync: false, converter));

        CudaRfBackendCompatibility compatibility =
            CudaRfBlockComputeBackend.CheckCompatibility(context, pipeline);

        Assert.False(compatibility.IsSupported);
        Assert.Contains("stateful sharpness", compatibility.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CvbsBatchRequestsOnlyNativeCvbsOutputs()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities);
        CudaBackendContext context = CreateContext(api, blockLength);
        var converter = new VideoOutputConverter(
            ire0: 8_100_000.0,
            hzIre: 10_000.0,
            outputZero: 0,
            vsyncIre: -40.0,
            outputScale: 1.0);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            cvbsOptions: new CvbsDecodeOptions(AutoSync: true, converter));
        Assert.True(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason), reason);
        CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);
        using (selectedBackend)
        {
            RfPipelineBlock block = Assert.Single(selectedBackend.DecodeBatch(
                pipeline,
                [new double[blockLength]],
                reportDiagnostics: false));

            Assert.Equal(CudaRfMode.Cvbs, api.LastJob!.Mode);
            Assert.Equal(
                CudaRfBatchFlags.OutputEnvelope |
                CudaRfBatchFlags.OutputDemodRaw |
                CudaRfBatchFlags.OutputVideoLowPass,
                api.LastJob.Flags);
            Assert.Null(api.LastJob.RfHighPassOutput);
            Assert.Null(api.LastJob.AnalyticRealOutput);
            Assert.Equal(2_000.0, block.Demodulated.Video[0]);
            Assert.Equal(2_000.0, block.Demodulated.RfHighPass[0]);
            Assert.Equal(3_000.0, block.Demodulated.Envelope[0]);
            Assert.Equal(4_000.0, block.Demodulated.VideoLowPass[0]);
        }
    }

    [Fact]
    public void MissingLdEfmCapabilityRejectsTheWholeCudaConfiguration()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities & ~CudaBackendCapabilities.LdEfmFrequency);
        using CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            ldEfm: BuildEfmFilter(blockLength));

        Assert.False(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason));
        Assert.Null(backend);
        Assert.Contains(nameof(CudaBackendCapabilities.LdEfmFrequency), reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLdAnalogAudioCapabilityRejectsTheWholeCudaConfiguration()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities & ~CudaBackendCapabilities.LdAnalogAudioFrequency);
        using CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            ldAnalogAudio: BuildAnalogAudioFilters(blockLength));

        Assert.False(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason));
        Assert.Null(backend);
        Assert.Contains(
            nameof(CudaBackendCapabilities.LdAnalogAudioFrequency),
            reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LdFrequencyBranchesUseNativeOutputsWithoutRepeatingTheInputFft()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities);
        CudaBackendContext context = CreateContext(api, blockLength);
        LaserDiscAnalogAudioFilterSet analogFilters = BuildAnalogAudioFilters(blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            ldEfm: BuildEfmFilter(blockLength),
            ldAnalogAudio: analogFilters);
        Assert.True(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason), reason);
        using CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);

        RfPipelineBlock block = Assert.Single(selectedBackend.DecodeBatch(
            pipeline,
            [new double[blockLength]],
            reportDiagnostics: false));

        Assert.True((api.LastJob!.Flags & CudaRfBatchFlags.OutputLdEfm) != 0);
        Assert.True((api.LastJob.Flags & CudaRfBatchFlags.OutputLdAnalogAudio) != 0);
        CudaLdFrequencyOptions options = Assert.IsType<CudaLdFrequencyOptions>(
            api.LastJob.LdFrequencyOptions);
        Assert.Equal((blockLength / 2) + 1, options.EfmFilter!.Length);
        Assert.Equal(analogFilters.Left.LowBin, options.AudioLeftLowBin);
        Assert.Equal(analogFilters.Left.BinCount, options.AudioLeftFilter!.Length);
        Assert.Equal(analogFilters.Right.LowBin, options.AudioRightLowBin);
        Assert.Equal(analogFilters.Right.BinCount, options.AudioRightFilter!.Length);

        Assert.Equal(
            [short.MaxValue, short.MinValue, (short)123, (short)-123],
            block.Demodulated.Efm![..4]);
        LaserDiscAnalogAudioBlock audio = Assert.IsType<LaserDiscAnalogAudioBlock>(
            block.Demodulated.AnalogAudio);
        Assert.Equal(analogFilters.DecimationFactor, audio.DecimationFactor);
        Assert.Equal(100.0, audio.Left[0]);
        Assert.Equal(1_100.0, audio.Left[1]);
        Assert.Equal(200.0, audio.Right[0]);
        Assert.Equal(2_200.0, audio.Right[1]);
    }

    [Fact]
    public void VhsBatchKeepsRecursiveAndVideoStagesOnCpu()
    {
        const int blockLength = 16;
        var api = new StubCudaApi(AllCapabilities);
        CudaBackendContext context = CreateContext(api, blockLength);
        using RfBlockDecodePipeline pipeline = BuildPipeline(
            blockLength,
            new DecodeFilterOptions(FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation),
            retainRfDiagnosticChannels: false);
        Assert.True(CudaRfBlockComputeBackend.TryCreate(
            context,
            pipeline,
            out CudaRfBlockComputeBackend? backend,
            out string reason), reason);
        CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);
        using (selectedBackend)
        {
            RfPipelineBlock block = Assert.Single(selectedBackend.DecodeBatch(
                pipeline,
                [new double[blockLength]],
                reportDiagnostics: false));

            Assert.Equal(CudaRfMode.VhsRustApproximation, api.LastJob!.Mode);
            Assert.Equal(
                CudaRfBatchFlags.OutputAnalytic |
                CudaRfBatchFlags.OutputDemodRaw |
                CudaRfBatchFlags.ApplyMtf,
                api.LastJob.Flags);
            Assert.Null(api.LastJob.EnvelopeOutput);
            Assert.Null(api.LastJob.VideoOutput);
            Assert.Empty(block.Input);
            Assert.Empty(block.Demodulated.DemodRaw);
            Assert.Empty(block.Demodulated.Analytic);
            Assert.Empty(block.Demodulated.RfHighPass);
            Assert.Equal(blockLength, block.Demodulated.Video.Length);
            Assert.Equal(blockLength, block.Demodulated.Envelope.Length);
        }
    }

    [Fact]
    public void StandardCpuVideoStagePreservesLegacyClipAndReferenceSource()
    {
        const int blockLength = 16;
        const double sampleRateHz = 40_000_000.0;
        Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
        double[] input = BuildWave(blockLength);
        var references = new RfVideoReferenceFilterSet(
            VideoBurst: identity,
            VideoBurstOffset: 0,
            VideoPilot: identity,
            ClipDemodForVideo: true);
        var demodulator = new RfDemodulator(sampleRateHz);
        RfDemodulatedBlock expected = demodulator.Demodulate(
            input,
            identity,
            identity,
            identity,
            identity,
            identity,
            referenceFilters: references);

        RfDemodulatedBlock actual = demodulator.CompleteStandardConjugateFirstStage(
            expected.DemodRaw.ToArray(),
            expected.Analytic.ToArray(),
            expected.Envelope.ToArray(),
            expected.RfHighPass.ToArray(),
            identity,
            identity,
            videoLowPassOffset: 0,
            references);

        Assert.Equal(expected.Video, actual.Video);
        Assert.Equal(expected.VideoLowPass, actual.VideoLowPass);
        Assert.Equal(expected.VideoBurst, actual.VideoBurst);
        Assert.Equal(expected.VideoPilot, actual.VideoPilot);
        Assert.Equal(expected.DemodRaw, actual.DemodRaw);
    }

    [Fact]
    public void VhsCpuPostStagePreservesLegacySecondStage()
    {
        const int blockLength = 16;
        const double sampleRateHz = 40_000_000.0;
        Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
        double[] input = BuildWave(blockLength);
        var demodulator = new RfDemodulator(sampleRateHz);
        RfDemodulatedBlock expected = demodulator.Demodulate(
            input,
            identity,
            identity,
            identity,
            identity,
            identity,
            fmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation);

        RfDemodulatedBlock actual = demodulator.CompleteVhsRustFirstStage(
            expected.DemodRaw.ToArray(),
            expected.Analytic.ToArray(),
            expected.RfHighPass.ToArray(),
            identity,
            identity,
            videoLowPassOffset: 0,
            diffDemodRepair: null,
            chromaTrap: null,
            nonlinearDeemphasis: null,
            subDeemphasis: null,
            betamaxFscNotchHz: null,
            vhsEnvelopeFilter: null,
            referenceFilters: null);

        Assert.Equal(expected.Video, actual.Video);
        Assert.Equal(expected.VideoLowPass, actual.VideoLowPass);
        Assert.Equal(expected.Envelope, actual.Envelope);
        Assert.Equal(expected.DemodRaw, actual.DemodRaw);
    }

    private static CudaBackendContext CreateContext(StubCudaApi api, int blockLength)
    {
        var device = new CudaDeviceInfo(
            Ordinal: 0,
            Name: "test-gpu",
            ComputeCapabilityMajor: 8,
            ComputeCapabilityMinor: 9,
            TotalGlobalMemoryBytes: 12UL << 30,
            DriverVersion: 13_000,
            RuntimeVersion: 13_000,
            CufftVersion: 13_000,
            SupportsFp64: true,
            SupportsConcurrentCopyAndCompute: true);
        var context = new CudaBackendContext(api, (nint)1, device, blockLength);
        Assert.Equal(CudaNativeStatus.Success, context.InitializeCapabilities());
        return context;
    }

    private static double[] BuildWave(int length) =>
        Enumerable.Range(0, length)
            .Select(index =>
                (Math.Sin(index * 0.41) * 12_000.0)
                + (Math.Cos(index * 0.17) * 4_000.0))
            .ToArray();

    private static RfBlockDecodePipeline BuildPipeline(
        int blockLength,
        DecodeFilterOptions? options = null,
        CvbsDecodeOptions? cvbsOptions = null,
        bool retainRfDiagnosticChannels = true,
        Action<Complex[]>? configureRfVideo = null,
        Complex[]? ldEfm = null,
        LaserDiscAnalogAudioFilterSet? ldAnalogAudio = null)
    {
        Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
        Complex[] rfVideo = identity.ToArray();
        configureRfVideo?.Invoke(rfVideo);
        double[] ones = Enumerable.Repeat(1.0, blockLength).ToArray();
        var filters = new DecodeFilterSet(
            rfVideo,
            identity,
            identity,
            identity,
            identity,
            identity,
            ldEfm,
            ones,
            ones,
            ones,
            ones,
            ones,
            ones,
            ldEfm?.Select(Complex.Abs).ToArray(),
            LdAnalogAudio: ldAnalogAudio);
        return new RfBlockDecodePipeline(
            new Pcm16StreamSampleLoader(),
            filters,
            sampleRateHz: 40_000_000.0,
            options,
            cvbsOptions,
            retainRfDiagnosticChannels: retainRfDiagnosticChannels);
    }

    private static Complex[] BuildEfmFilter(int blockLength)
    {
        var filter = new Complex[blockLength];
        for (int i = 0; i <= blockLength / 2; i++)
        {
            filter[i] = Complex.One;
        }

        return filter;
    }

    private static LaserDiscAnalogAudioFilterSet BuildAnalogAudioFilters(int blockLength)
    {
        const int binCount = 8;
        var stage1 = Enumerable.Repeat(Complex.One, binCount).ToArray();
        Complex[] stage2 = RfDemodulator.IdentityFilter(blockLength);
        var left = new LaserDiscAnalogAudioChannelFilter(
            LowBin: 1,
            BinCount: binCount,
            SliceSampleRateHz: 8_000.0,
            LowFrequencyHz: 100.0,
            CenterFrequencyHz: 1_100.0,
            Stage1Filter: stage1,
            Stage2Filter: stage2);
        var right = new LaserDiscAnalogAudioChannelFilter(
            LowBin: 2,
            BinCount: binCount,
            SliceSampleRateHz: 16_000.0,
            LowFrequencyHz: 200.0,
            CenterFrequencyHz: 2_200.0,
            Stage1Filter: stage1.ToArray(),
            Stage2Filter: stage2.ToArray());
        return new LaserDiscAnalogAudioFilterSet(left, right, DecimationFactor: 4);
    }

    private const CudaBackendCapabilities AllCapabilities =
        CudaBackendCapabilities.RfBatchTwoStage |
        CudaBackendCapabilities.RfHighPass |
        CudaBackendCapabilities.RfMtf |
        CudaBackendCapabilities.RfAnalytic |
        CudaBackendCapabilities.RfEnvelope |
        CudaBackendCapabilities.RfConjugateDemod |
        CudaBackendCapabilities.RfVideo |
        CudaBackendCapabilities.RfVideoLowPass |
        CudaBackendCapabilities.RfModeStandard |
        CudaBackendCapabilities.RfModeVhsRustApproximation |
        CudaBackendCapabilities.RfModeCvbs |
        CudaBackendCapabilities.VhsRustDemodKernel |
        CudaBackendCapabilities.LdEfmFrequency |
        CudaBackendCapabilities.LdAnalogAudioFrequency;

    private sealed class StubCudaApi(CudaBackendCapabilities capabilities) : ICudaNativeApi
    {
        public CudaNativeStatus ExecuteStatus { get; init; } = CudaNativeStatus.Success;

        public int ExecuteCalls { get; private set; }

        public CudaRfBatchJob? LastJob { get; private set; }

        public bool Destroyed { get; private set; }

        public uint GetAbiVersion() => CudaNativeAbi.Version;

        public CudaNativeStatus GetDeviceCount(out int deviceCount)
        {
            deviceCount = 1;
            return CudaNativeStatus.Success;
        }

        public CudaNativeStatus GetDeviceInfo(int deviceIndex, out CudaDeviceInfo deviceInfo)
        {
            deviceInfo = null!;
            return CudaNativeStatus.NotSupported;
        }

        public CudaNativeStatus CreateContext(int deviceIndex, out nint context)
        {
            context = (nint)1;
            return CudaNativeStatus.Success;
        }

        public void DestroyContext(nint context) => Destroyed = true;

        public CudaNativeStatus RunSelfTest(nint context, out CudaSelfTestMetrics metrics)
        {
            metrics = default;
            return CudaNativeStatus.Success;
        }

        public CudaNativeStatus GetCapabilities(
            nint context,
            out CudaBackendCapabilities reportedCapabilities)
        {
            reportedCapabilities = capabilities;
            return CudaNativeStatus.Success;
        }

        public CudaNativeStatus ExecuteRfBatch(nint context, CudaRfBatchJob job)
        {
            ExecuteCalls++;
            LastJob = job;
            if (ExecuteStatus != CudaNativeStatus.Success)
            {
                return ExecuteStatus;
            }

            Fill(job.VideoOutput, 1_000.0);
            Fill(job.DemodRawOutput, 2_000.0);
            Fill(job.EnvelopeOutput, 3_000.0);
            Fill(job.VideoLowPassOutput, 4_000.0);
            Fill(job.RfHighPassOutput, 5_000.0);
            Fill(job.AnalyticRealOutput, 6_000.0);
            Fill(job.AnalyticImaginaryOutput, 7_000.0);
            FillLdFrequencyOutputs(job.LdFrequencyOptions);
            return CudaNativeStatus.Success;
        }

        public string GetLastError(nint context) => "injected execution failure";

        public void Dispose()
        {
        }

        private static void Fill(double[]? output, double value)
        {
            if (output is not null)
            {
                Array.Fill(output, value);
            }
        }

        private static void FillLdFrequencyOutputs(CudaLdFrequencyOptions? options)
        {
            if (options?.EfmOutput is { } efm)
            {
                double[] pattern = [40_000.0, -40_000.0, 123.75, -123.75];
                for (int i = 0; i < efm.Length; i++)
                {
                    efm[i] = pattern[i % pattern.Length];
                }
            }

            FillAnalytic(options?.AudioLeftOutput, options?.AudioLeftBinCount ?? 0);
            FillAnalytic(options?.AudioRightOutput, options?.AudioRightBinCount ?? 0);
        }

        private static void FillAnalytic(Complex[]? output, int binCount)
        {
            if (output is null || binCount <= 0)
            {
                return;
            }

            for (int i = 0; i < output.Length; i++)
            {
                int sample = i % binCount;
                output[i] = Complex.FromPolarCoordinates(1.0, sample * (Math.PI / 4.0));
            }
        }
    }
}
