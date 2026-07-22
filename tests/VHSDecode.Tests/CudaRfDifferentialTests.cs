using System.Numerics;
using VHSDecode.Core.Compute.Cuda;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

/// <summary>
/// Hardware differential coverage for the optional CUDA sidecar. Ordinary
/// CPU-only CI leaves VHSDECODE_CUDA_TEST_COMPONENT_DIR unset; the dedicated
/// GPU job sets it to the packaged sidecar directory and executes every case.
/// </summary>
public sealed class CudaRfDifferentialTests
{
    private const double MaximumNormalizedAbsoluteError = 1e-9;
    private const double MaximumNrmse = 1e-11;

    [Theory]
    [InlineData(4_096)]
    [InlineData(7_040)]
    [InlineData(20_000)]
    [InlineData(32_768)]
    public void BatchedRfGraphsMatchCpuReferenceWithinFp64Tolerance(int blockLength)
    {
        string? componentDirectory = Environment.GetEnvironmentVariable(
            "VHSDECODE_CUDA_TEST_COMPONENT_DIR");
        if (string.IsNullOrWhiteSpace(componentDirectory))
        {
            Assert.False(
                string.Equals(
                    Environment.GetEnvironmentVariable("VHSDECODE_CUDA_REQUIRE_GPU_TESTS"),
                    "1",
                    StringComparison.Ordinal),
                "VHSDECODE_CUDA_TEST_COMPONENT_DIR is required on the CUDA GPU runner.");
            return;
        }

        if ((blockLength & (blockLength - 1)) != 0)
        {
            // The current CPU PocketFFT reference deliberately accepts only
            // power-of-two RF blocks. Exercise cuFFT's arbitrary even-size
            // path against the analytically exact CVBS identity graph instead.
            CompareCvbsIdentityGraph(componentDirectory, blockLength);
            return;
        }

        foreach (CudaRfMode mode in new[]
                 {
                     CudaRfMode.StandardConjugate,
                     CudaRfMode.VhsRustApproximation,
                     CudaRfMode.Cvbs
                 })
        {
            CompareMode(componentDirectory, blockLength, mode);
        }

        CompareLaserDiscFrequencyBranches(componentDirectory, blockLength);
    }

    private static void CompareLaserDiscFrequencyBranches(
        string componentDirectory,
        int blockLength)
    {
        const double sampleRateHz = 40_000_000.0;
        const int audioBinCount = 64;
        int leftLowBin = blockLength / 10;
        int rightLowBin = blockLength / 5;
        Complex[] efmFilter = new Complex[blockLength];
        for (int i = 1; i <= blockLength / 2; i++)
        {
            double amplitude = i < blockLength / 3 ? 0.75 : 0.0;
            efmFilter[i] = Complex.FromPolarCoordinates(amplitude, i * 0.0007);
        }

        static Complex[] BuildAnalyticSliceFilter(int length, double phaseStep)
        {
            var filter = new Complex[length];
            for (int i = 0; i < length / 2; i++)
            {
                filter[i] = 2.0 * Complex.FromPolarCoordinates(1.0, i * phaseStep);
            }

            return filter;
        }

        LaserDiscAnalogAudioChannelFilter BuildChannel(int lowBin, double phaseStep) =>
            new(
                lowBin,
                audioBinCount,
                sampleRateHz * audioBinCount / blockLength,
                sampleRateHz * lowBin / blockLength,
                sampleRateHz * (lowBin + (audioBinCount / 4.0)) / blockLength,
                BuildAnalyticSliceFilter(audioBinCount, phaseStep),
                RfDemodulator.IdentityFilter(blockLength));

        var analogFilters = new LaserDiscAnalogAudioFilterSet(
            BuildChannel(leftLowBin, 0.013),
            BuildChannel(rightLowBin, -0.009),
            blockLength / audioBinCount);
        DecodeFilterSet filters = BuildIdentityFilters(blockLength) with
        {
            LdEfm = efmFilter,
            LdAnalogAudio = analogFilters
        };
        using var cpuPipeline = new RfBlockDecodePipeline(
            new Pcm16StreamSampleLoader(),
            filters,
            sampleRateHz,
            retainRfDiagnosticChannels: true);
        using var cudaPipeline = new RfBlockDecodePipeline(
            new Pcm16StreamSampleLoader(),
            filters,
            sampleRateHz,
            retainRfDiagnosticChannels: true);
        using CudaBackendProbeResult probeResult = new CudaBackendProbe().Probe(
            CudaBackendProbeRequest.ForBackend(
                RfComputeBackend.Cuda,
                deviceIndex: 0,
                blockLength,
                componentDirectory: componentDirectory));
        Assert.True(probeResult.IsReady, probeResult.Message);
        CudaBackendContext context = probeResult.TakeContext();
        Assert.True(
            CudaRfBlockComputeBackend.TryCreate(
                context,
                cudaPipeline,
                out CudaRfBlockComputeBackend? backend,
                out string reason),
            reason);
        using CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);

        double[] BuildLdInput(double phase)
        {
            var input = new double[blockLength];
            for (int i = 0; i < input.Length; i++)
            {
                input[i] =
                    (12_000.0 * Math.Cos(Math.Tau * (leftLowBin + 7) * i / blockLength + phase)) +
                    (8_000.0 * Math.Cos(Math.Tau * (rightLowBin + 11) * i / blockLength - phase)) +
                    (2_000.0 * Math.Sin(Math.Tau * 23 * i / blockLength));
            }

            return input;
        }

        double[][] inputs = [BuildLdInput(0.13), BuildLdInput(1.07)];
        RfPipelineBlock[] expected = inputs
            .Select(input => cpuPipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false))
            .ToArray();
        RfPipelineBlock[] actual = selectedBackend.DecodeBatch(
            cudaPipeline,
            inputs,
            reportDiagnostics: false);

        for (int batch = 0; batch < inputs.Length; batch++)
        {
            string prefix = $"LD-frequency/{blockLength}/batch-{batch}";
            CompareInt16(
                prefix + "/efm",
                Assert.IsType<short[]>(expected[batch].Demodulated.Efm),
                Assert.IsType<short[]>(actual[batch].Demodulated.Efm));
            LaserDiscAnalogAudioBlock expectedAudio = Assert.IsType<LaserDiscAnalogAudioBlock>(
                expected[batch].Demodulated.AnalogAudio);
            LaserDiscAnalogAudioBlock actualAudio = Assert.IsType<LaserDiscAnalogAudioBlock>(
                actual[batch].Demodulated.AnalogAudio);
            Assert.Equal(expectedAudio.DecimationFactor, actualAudio.DecimationFactor);
            Compare(
                prefix + "/audio-left",
                expectedAudio.Left,
                actualAudio.Left,
                maximumNormalizedAbsoluteError: 2e-6,
                maximumNrmse: 2e-7);
            Compare(
                prefix + "/audio-right",
                expectedAudio.Right,
                actualAudio.Right,
                maximumNormalizedAbsoluteError: 2e-6,
                maximumNrmse: 2e-7);
        }
    }

    private static void CompareCvbsIdentityGraph(string componentDirectory, int blockLength)
    {
        DecodeFilterSet filters = BuildIdentityFilters(blockLength);
        using var pipeline = new RfBlockDecodePipeline(
            new Pcm16StreamSampleLoader(),
            filters,
            sampleRateHz: 40_000_000.0,
            new DecodeFilterOptions(),
            new CvbsDecodeOptions(AutoSync: true, VideoOutput: null!),
            retainRfDiagnosticChannels: true);
        using CudaBackendProbeResult probeResult = new CudaBackendProbe().Probe(
            CudaBackendProbeRequest.ForBackend(
                RfComputeBackend.Cuda,
                deviceIndex: 0,
                blockLength,
                componentDirectory: componentDirectory));
        Assert.True(probeResult.IsReady, probeResult.Message);
        CudaBackendContext context = probeResult.TakeContext();
        Assert.True(
            CudaRfBlockComputeBackend.TryCreate(
                context,
                pipeline,
                out CudaRfBlockComputeBackend? backend,
                out string reason),
            reason);
        using CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);

        double[][] inputs =
        [
            BuildInput(blockLength, phaseOffset: 0.17),
            BuildInput(blockLength, phaseOffset: 1.31)
        ];
        RfPipelineBlock[] actual = selectedBackend.DecodeBatch(
            pipeline,
            inputs,
            reportDiagnostics: false);
        for (int batch = 0; batch < inputs.Length; batch++)
        {
            string prefix = $"CVBS-identity/{blockLength}/batch-{batch}";
            double[] envelope = inputs[batch].Select(Math.Abs).ToArray();
            Compare(prefix + "/video", inputs[batch], actual[batch].Demodulated.Video);
            Compare(prefix + "/demod", inputs[batch], actual[batch].Demodulated.DemodRaw);
            Compare(prefix + "/envelope", envelope, actual[batch].Demodulated.Envelope);
            Compare(prefix + "/video-lp", inputs[batch], actual[batch].Demodulated.VideoLowPass);
            Compare(prefix + "/rf-hp", inputs[batch], actual[batch].Demodulated.RfHighPass);
        }
    }

    private static void CompareMode(
        string componentDirectory,
        int blockLength,
        CudaRfMode mode)
    {
        const double sampleRateHz = 40_000_000.0;
        DecodeFilterSet filters = BuildIdentityFilters(blockLength);
        DecodeFilterOptions filterOptions = mode == CudaRfMode.VhsRustApproximation
            ? new DecodeFilterOptions(FmDemodulatorMode: RfFmDemodulatorMode.VhsRustApproximation)
            : new DecodeFilterOptions();
        CvbsDecodeOptions? cvbsOptions = mode == CudaRfMode.Cvbs
            ? new CvbsDecodeOptions(AutoSync: true, VideoOutput: null!)
            : null;

        using var cpuPipeline = new RfBlockDecodePipeline(
            new Pcm16StreamSampleLoader(),
            filters,
            sampleRateHz,
            filterOptions,
            cvbsOptions,
            retainRfDiagnosticChannels: true);
        using var cudaPipeline = new RfBlockDecodePipeline(
            new Pcm16StreamSampleLoader(),
            filters,
            sampleRateHz,
            filterOptions,
            cvbsOptions,
            retainRfDiagnosticChannels: true);

        using CudaBackendProbeResult probeResult = new CudaBackendProbe().Probe(
            CudaBackendProbeRequest.ForBackend(
                RfComputeBackend.Cuda,
                deviceIndex: 0,
                blockLength,
                componentDirectory: componentDirectory));
        Assert.True(probeResult.IsReady, probeResult.Message);
        CudaBackendContext context = probeResult.TakeContext();
        Assert.True(
            CudaRfBlockComputeBackend.TryCreate(
                context,
                cudaPipeline,
                out CudaRfBlockComputeBackend? backend,
                out string reason),
            reason);
        using CudaRfBlockComputeBackend selectedBackend = Assert.IsType<CudaRfBlockComputeBackend>(backend);

        double[][] inputs =
        [
            BuildInput(blockLength, phaseOffset: 0.17),
            BuildInput(blockLength, phaseOffset: 1.31)
        ];
        RfPipelineBlock[] expected = inputs
            .Select(input => cpuPipeline.DecodePreparedStreamBlock(input, reportDiagnostics: false))
            .ToArray();
        RfPipelineBlock[] actual = selectedBackend.DecodeBatch(
            cudaPipeline,
            inputs,
            reportDiagnostics: false);

        Assert.Equal(expected.Length, actual.Length);
        for (int batch = 0; batch < expected.Length; batch++)
        {
            string prefix = $"{mode}/{blockLength}/batch-{batch}";
            Compare(prefix + "/input", expected[batch].Input, actual[batch].Input);
            Compare(prefix + "/video", expected[batch].Demodulated.Video, actual[batch].Demodulated.Video);
            Compare(prefix + "/demod", expected[batch].Demodulated.DemodRaw, actual[batch].Demodulated.DemodRaw);
            Compare(prefix + "/analytic", expected[batch].Demodulated.Analytic, actual[batch].Demodulated.Analytic);
            Compare(prefix + "/envelope", expected[batch].Demodulated.Envelope, actual[batch].Demodulated.Envelope);
            Compare(prefix + "/video-lp", expected[batch].Demodulated.VideoLowPass, actual[batch].Demodulated.VideoLowPass);
            Compare(prefix + "/rf-hp", expected[batch].Demodulated.RfHighPass, actual[batch].Demodulated.RfHighPass);
        }
    }

    private static DecodeFilterSet BuildIdentityFilters(int blockLength)
    {
        Complex[] identity = RfDemodulator.IdentityFilter(blockLength);
        double[] ones = Enumerable.Repeat(1.0, blockLength).ToArray();
        return new DecodeFilterSet(
            identity,
            identity,
            [],
            identity,
            identity,
            identity,
            null,
            ones,
            ones,
            [],
            ones,
            ones,
            ones,
            null)
        {
            VideoLowPass05Offset = 0
        };
    }

    private static double[] BuildInput(int length, double phaseOffset)
    {
        var input = new double[length];
        uint noiseState = 0x9E3779B9u;
        for (int i = 0; i < input.Length; i++)
        {
            noiseState = unchecked((noiseState * 1_664_525u) + 1_013_904_223u);
            double noise = ((noiseState >> 8) / 16_777_216.0) - 0.5;
            double x = i + phaseOffset;
            double modulation = 0.0025 * Math.Sin(Math.Tau * x / 503.0);
            input[i] =
                (0.73 * Math.Cos(Math.Tau * ((0.117 * x) + modulation))) +
                (0.19 * Math.Sin(Math.Tau * 0.023 * x)) +
                (noise * 1e-4);
        }

        return input;
    }

    private static void Compare(
        string channel,
        ReadOnlySpan<double> expected,
        ReadOnlySpan<double> actual,
        double maximumNormalizedAbsoluteError = MaximumNormalizedAbsoluteError,
        double maximumNrmse = MaximumNrmse)
    {
        Assert.Equal(expected.Length, actual.Length);
        double maximumReference = 0.0;
        double maximumError = 0.0;
        double squaredReference = 0.0;
        double squaredError = 0.0;
        for (int i = 0; i < expected.Length; i++)
        {
            double reference = expected[i];
            double candidate = actual[i];
            Assert.Equal(double.IsNaN(reference), double.IsNaN(candidate));
            Assert.Equal(double.IsInfinity(reference), double.IsInfinity(candidate));
            if (!double.IsFinite(reference) || !double.IsFinite(candidate))
            {
                Assert.Equal(reference, candidate);
                continue;
            }

            double error = Math.Abs(candidate - reference);
            maximumReference = Math.Max(maximumReference, Math.Abs(reference));
            maximumError = Math.Max(maximumError, error);
            squaredReference += reference * reference;
            squaredError += error * error;
        }

        double normalizedMaximum = maximumReference == 0.0
            ? maximumError
            : maximumError / maximumReference;
        double nrmse = squaredReference == 0.0
            ? Math.Sqrt(squaredError / Math.Max(1, expected.Length))
            : Math.Sqrt(squaredError / squaredReference);
        Assert.True(
            normalizedMaximum <= maximumNormalizedAbsoluteError,
            $"{channel}: normalized max abs {normalizedMaximum:R} exceeds {maximumNormalizedAbsoluteError:R}.");
        Assert.True(
            nrmse <= maximumNrmse,
            $"{channel}: NRMSE {nrmse:R} exceeds {maximumNrmse:R}.");
    }

    private static void CompareInt16(
        string channel,
        ReadOnlySpan<short> expected,
        ReadOnlySpan<short> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int different = 0;
        int maximumDifference = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int difference = Math.Abs((int)actual[i] - expected[i]);
            maximumDifference = Math.Max(maximumDifference, difference);
            different += difference == 0 ? 0 : 1;
        }

        double differenceRate = different / (double)Math.Max(1, expected.Length);
        Assert.True(maximumDifference <= 1, $"{channel}: maximum integer difference is {maximumDifference} LSB.");
        Assert.True(differenceRate <= 0.0001, $"{channel}: differing sample rate is {differenceRate:P6}.");
    }

    private static void Compare(string channel, ReadOnlySpan<Complex> expected, ReadOnlySpan<Complex> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        double maximumReference = 0.0;
        double maximumError = 0.0;
        double squaredReference = 0.0;
        double squaredError = 0.0;
        for (int i = 0; i < expected.Length; i++)
        {
            Complex reference = expected[i];
            Complex candidate = actual[i];
            Assert.Equal(double.IsNaN(reference.Real), double.IsNaN(candidate.Real));
            Assert.Equal(double.IsNaN(reference.Imaginary), double.IsNaN(candidate.Imaginary));
            Assert.Equal(double.IsInfinity(reference.Real), double.IsInfinity(candidate.Real));
            Assert.Equal(double.IsInfinity(reference.Imaginary), double.IsInfinity(candidate.Imaginary));
            if (!double.IsFinite(reference.Real) || !double.IsFinite(reference.Imaginary)
                || !double.IsFinite(candidate.Real) || !double.IsFinite(candidate.Imaginary))
            {
                Assert.Equal(reference, candidate);
                continue;
            }

            double referenceMagnitude = reference.Magnitude;
            double errorMagnitude = Complex.Abs(candidate - reference);
            maximumReference = Math.Max(maximumReference, referenceMagnitude);
            maximumError = Math.Max(maximumError, errorMagnitude);
            squaredReference += referenceMagnitude * referenceMagnitude;
            squaredError += errorMagnitude * errorMagnitude;
        }

        double normalizedMaximum = maximumReference == 0.0
            ? maximumError
            : maximumError / maximumReference;
        double nrmse = squaredReference == 0.0
            ? Math.Sqrt(squaredError / Math.Max(1, expected.Length))
            : Math.Sqrt(squaredError / squaredReference);
        Assert.True(
            normalizedMaximum <= MaximumNormalizedAbsoluteError,
            $"{channel}: normalized complex max abs {normalizedMaximum:R} exceeds {MaximumNormalizedAbsoluteError:R}.");
        Assert.True(
            nrmse <= MaximumNrmse,
            $"{channel}: complex NRMSE {nrmse:R} exceeds {MaximumNrmse:R}.");
    }
}
