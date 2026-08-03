using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppRealDft32Tests
{
    [Theory(DisplayName = "IPP real DFT32 rejects odd and undersized lengths before probing native runtime")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(13)]
    public void RejectsUnsupportedLengthsBeforeNativeProbe(int length)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new IppRealDft32(length));
        Assert.Contains("even integer", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP mixed-radix real DFT32 agrees numerically and round-trips")]
    [InlineData(14)]
    [InlineData(90)]
    [InlineData(3_564)]
    public void AgreesNumericallyAndRoundTrips(int length)
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        float[] input = BuildInput(length);
        Complex32[] expected = PocketFftReal32.ForwardAnyLength(input);
        var actual = new IppComplex32[expected.Length];
        var reconstructed = new float[length];

        using var dft = new IppRealDft32(length);
        Assert.Equal(length, dft.Length);
        Assert.Equal(expected.Length, dft.SpectrumLength);
        dft.Forward(input, actual);
        dft.Inverse(actual, reconstructed);

        for (int index = 0; index < expected.Length; index++)
        {
            double realTolerance = 2.5e-4 * Math.Max(1.0, Math.Abs(expected[index].Real));
            double imaginaryTolerance = 2.5e-4 * Math.Max(1.0, Math.Abs(expected[index].Imaginary));
            Assert.InRange(
                Math.Abs(expected[index].Real - actual[index].Real),
                0.0,
                realTolerance);
            Assert.InRange(
                Math.Abs(expected[index].Imaginary - actual[index].Imaginary),
                0.0,
                imaginaryTolerance);
        }

        for (int index = 0; index < input.Length; index++)
        {
            double tolerance = 2.5e-5 * Math.Max(1.0, Math.Abs(input[index]));
            Assert.InRange(
                Math.Abs(input[index] - reconstructed[index]),
                0.0,
                tolerance);
        }
    }

    [Fact(DisplayName = "IPP mixed-radix real DFT32 is deterministic across dirty reuse")]
    public void IsDeterministicAcrossDirtyReuse()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 3_564;
        float[] input = BuildInput(Length);
        var firstSpectrum = new IppComplex32[(Length / 2) + 1];
        var secondSpectrum = new IppComplex32[firstSpectrum.Length];
        var firstOutput = new float[Length];
        var secondOutput = new float[Length];

        using var dft = new IppRealDft32(Length);
        dft.Forward(input, firstSpectrum);
        dft.Inverse(firstSpectrum, firstOutput);
        Array.Fill(secondSpectrum, new IppComplex32(float.NaN, float.NegativeInfinity));
        Array.Fill(secondOutput, float.NaN);
        dft.Forward(input, secondSpectrum);
        dft.Inverse(secondSpectrum, secondOutput);

        Assert.Equal(firstSpectrum, secondSpectrum);
        Assert.Equal(firstOutput, secondOutput);
    }

    [Fact(DisplayName = "IPP current Super-Gaussian filter remains numerically close to Exact")]
    public void SuperGaussianFilterRemainsNumericallyCloseToExact()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 355_255;
        const double FscHz = 4_406_000.0;
        const double CarrierHz = 732_400.0;
        double[] input = BuildInput(Length)
            .Select(static value => (double)(value * 16_384.0F))
            .ToArray();
        double[] exact = new ChromaSuperGaussianFinalFilter(
                Length,
                FscHz,
                CarrierHz,
                DspBackend.Exact)
            .ApplyInPlace((double[])input.Clone(), workerThreads: 20);
        double[] accelerated = new ChromaSuperGaussianFinalFilter(
                Length,
                FscHz,
                CarrierHz,
                DspBackend.IppFast)
            .ApplyInPlace((double[])input.Clone(), workerThreads: 20);

        double sumSquared = 0.0;
        double maximum = 0.0;
        for (int index = 0; index < exact.Length; index++)
        {
            double difference = Math.Abs(exact[index] - accelerated[index]);
            maximum = Math.Max(maximum, difference);
            sumSquared += difference * difference;
        }

        double rootMeanSquare = Math.Sqrt(sumSquared / exact.Length);
        Assert.InRange(rootMeanSquare, 0.0, 0.1);
        Assert.InRange(maximum, 0.0, 2.0);
    }

    [Fact(DisplayName = "IPP Super-Gaussian contexts are released by repeated pipeline disposal")]
    public void PipelineDisposalReleasesRepeatedSuperGaussianContexts()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 3_564;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var filter = new ChromaSuperGaussianFinalFilter(
                Length,
                fscHz: 4_406_000.0,
                colorUnderCarrierHz: 732_400.0,
                dspBackend: DspBackend.IppFast);
            using TbcFieldDecodePipeline pipeline = BuildPipeline(filter);

            pipeline.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => filter.Apply(new double[Length]));
        }
    }

    private static float[] BuildInput(int length)
    {
        var input = new float[length];
        for (int index = 0; index < input.Length; index++)
        {
            input[index] = (float)(
                Math.Sin(index * 0.031)
                + (0.25 * Math.Cos(index * 0.173))
                + (((index % 17) - 8) * 0.0003));
        }

        return input;
    }

    private static TbcFieldDecodePipeline BuildPipeline(
        ChromaSuperGaussianFinalFilter filter)
    {
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0,
            numPulses: 5);
        var frameSpec = new TbcFrameSpec(
            "PAL",
            OutputLineLength: 4,
            OutputLineCount: 20,
            OutputSampleRateHz: 4_000_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 100.0,
            hzIre: 3.5,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var chromaOptions = new VhsChromaFieldOptions(
            ColorSystem: "PAL",
            OutputLineLength: 99,
            OutputLineCount: 36,
            OutputSampleRateHz: 17_734_475.0,
            FscMHz: 4.433_618_75,
            ColorUnderCarrierHz: 732_400.0,
            BurstStart: 0,
            BurstEnd: 1,
            BurstAbsRef: 1.0,
            ChromaRotation: null,
            DisableComb: false,
            DisablePhaseCorrection: false,
            EnableColorKiller: false,
            DetectChromaTrackPhase: false)
        {
            SuperGaussianFinalFilter = filter
        };

        return new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(frameSpec, converter),
            converter,
            "PAL",
            TbcDropoutDetectionOptions.Disabled,
            syncDetectionOptions: SyncDetectionOptions.Disabled,
            chromaFieldOptions: chromaOptions,
            decodeType: "vhs");
    }
}
