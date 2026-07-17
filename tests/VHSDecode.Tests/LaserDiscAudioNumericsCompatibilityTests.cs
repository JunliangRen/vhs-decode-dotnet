using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Formats;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscAudioNumericsCompatibilityTests
{
    [Fact(DisplayName = "SciPy 32768-point float32 real FFT matches v0.4.0")]
    public void ScipyFloat32RealFftMatchesV040()
    {
        float[] input = Enumerable.Range(0, 32_768)
            .Select(index => (float)(((index * 7_919) % 65_521) - 32_760))
            .ToArray();

        Complex32[] spectrum = PocketFftReal32.ForwardDucc(input);

        Assert.Equal(
            "592EE58B19B8AAA305F6412284488191C5BD2B9BB97AB879006720DF51BD1C56",
            Sha256(MemoryMarshal.AsBytes(spectrum.AsSpan())));
    }

    [Fact(DisplayName = "PAL LD analog audio phase 2 matches v0.4.0")]
    public void PalLdAnalogAudioPhase2MatchesV040()
    {
        FormatParameterSet parameters = FormatCatalog.Default.GetLaserDiscParameters(
            "PAL",
            lowBand: false);
        DecodeFilterSet filterSet = DecodeFilterSetBuilder.BuildBasic(
            parameters,
            sampleRateHz: 40_000_000.0,
            blockLength: 32_768,
            new DecodeFilterOptions(LdDecodeAnalogAudio: true));
        LaserDiscAnalogAudioFilterSet filters = Assert.IsType<LaserDiscAnalogAudioFilterSet>(
            filterSet.LdAnalogAudio);
        var input = new LaserDiscAnalogAudioBlock(
            BuildChannel(filters.Left.CenterFrequencyHz, 7_919),
            BuildChannel(filters.Right.CenterFrequencyHz, 15_427),
            DecimationFactor: 32);

        LaserDiscAnalogAudioBlock output = LaserDiscAnalogAudioPhase2.Apply(
            input,
            filters);

        Assert.True(output.UsesFloat32Storage);
        Assert.Equal(
            "68CB22D86AD3C6C85FFE3986CB2DC197C9DE6EA9ECF308E926E3480BC627C8DE",
            Sha256Float32(output.Left));
        Assert.Equal(
            "0A4B5F027D0072C3F50527544744A516336A1D9701438B32A953564064FE3091",
            Sha256Float32(output.Right));
    }

    [Fact(DisplayName = "LD phase 2 retains the upstream short-field dtype")]
    public void LdPhase2RetainsUpstreamShortFieldDtype()
    {
        Complex[] identity = Enumerable.Repeat(Complex.One, 1_024).ToArray();
        var channel = new LaserDiscAnalogAudioChannelFilter(
            LowBin: 0,
            BinCount: identity.Length,
            SliceSampleRateHz: 1_000_000.0,
            LowFrequencyHz: 0.0,
            CenterFrequencyHz: 1_000.0,
            Stage1Filter: identity,
            Stage2Filter: identity);
        var filters = new LaserDiscAnalogAudioFilterSet(
            channel,
            channel,
            DecimationFactor: 1);
        double[] shortInput = Enumerable.Range(0, 8)
            .Select(index => 1_000.0 + index)
            .ToArray();
        double[] longInput = Enumerable.Range(0, 1_500)
            .Select(index => 1_000.0 + index)
            .ToArray();

        LaserDiscAnalogAudioBlock shortOutput = LaserDiscAnalogAudioPhase2.Apply(
            new LaserDiscAnalogAudioBlock(shortInput, shortInput, 1),
            filters);
        LaserDiscAnalogAudioBlock longOutput = LaserDiscAnalogAudioPhase2.Apply(
            new LaserDiscAnalogAudioBlock(longInput, longInput, 1),
            filters);

        Assert.False(shortOutput.UsesFloat32Storage);
        Assert.True(longOutput.UsesFloat32Storage);
    }

    [Theory(DisplayName = "PAL LD EFM equalizer spline matches SciPy")]
    [InlineData(12_345.0, 0.01918416964171457, -0.12909647945121386)]
    [InlineData(234_567.0, 0.25384801751662245, -1.2286080519402898)]
    [InlineData(950_000.0, 1.03, -1.5)]
    [InlineData(1_234_567.0, 0.9155640451686032, -1.5167878058309545)]
    [InlineData(1_899_000.0, 0.0035608267140546782, -1.0016023203990299)]
    public void PalLdEfmEqualizerSplineMatchesScipy(
        double position,
        double expectedAmplitude,
        double expectedPhase)
    {
        double[] frequencies = Enumerable.Range(0, 11)
            .Select(index => index * 190_000.0)
            .ToArray();
        double[] amplitudes =
            [0.0, 0.215, 0.41, 0.73, 0.98, 1.03, 0.99, 0.81, 0.59, 0.42, 0.0];
        double[] phases =
            [0.0, -0.92, -1.03, -1.11, -1.2, -1.2, -1.2, -1.2, -1.05, -0.95, -0.8];
        for (int i = 0; i < phases.Length; i++)
        {
            phases[i] *= 1.25;
        }

        var amplitudeSpline = new ScipyCubicNotAKnotInterpolator(
            frequencies,
            amplitudes);
        var phaseSpline = new ScipyCubicNotAKnotInterpolator(
            frequencies,
            phases);

        Assert.Equal(expectedAmplitude, amplitudeSpline.Evaluate(position));
        Assert.Equal(expectedPhase, phaseSpline.Evaluate(position));
    }

    [Theory(DisplayName = "LD audio mean uses Numba float32 accumulation")]
    [InlineData(28, 7_919, 683_593.75, 0x4926714B)]
    [InlineData(29, 15_427, 1_066_406.25, 0x4981B78C)]
    [InlineData(31, 3_571, 683_593.75, 0x49240F0C)]
    public void LdAudioMeanUsesNumbaFloat32Accumulation(
        int length,
        int multiplier,
        double centerFrequencyHz,
        int expectedBits)
    {
        double[] values = BuildChannel(
            centerFrequencyHz,
            multiplier,
            length);

        float mean = TbcFieldDecodePipeline.MeanAudioFloat32(values);

        Assert.Equal(expectedBits, BitConverter.SingleToInt32Bits(mean));
    }

    [Fact(DisplayName = "LD analog audio reports muted TBC failures like v0.4.0")]
    public void LdAnalogAudioReportsMutedTbcFailures()
    {
        var diagnostics = new List<(string Level, string Message)>();
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: 4,
            OutputLineCount: 2,
            OutputSampleRateHz: 14_318_180.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 0.0,
            hzIre: 1.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var pipeline = new TbcFieldDecodePipeline(
            new SyncAnalyzer(
                sampleRateHz: 1_000_000.0,
                linePeriodUs: 100.0,
                hsyncPulseUs: 10.0,
                equalizingPulseUs: 5.0,
                vsyncPulseUs: 20.0),
            new TbcFieldRenderer(spec, converter),
            converter,
            "NTSC",
            TbcDropoutDetectionOptions.Disabled,
            analogAudioOptions: new LaserDiscAnalogAudioOutputOptions(
                LinePeriodUs: 100.0,
                LineCount: 2,
                OutputFrequency: 10_000.0,
                LeftCarrierHz: 1_000.0,
                RightCarrierHz: 2_000.0),
            diagnosticLogger: (level, message) => diagnostics.Add((level, message)));
        var audio = new LaserDiscAnalogAudioBlock(
            Left: [1_000.0],
            Right: [2_000.0],
            DecimationFactor: 1);

        short[] output = Assert.IsType<short[]>(pipeline.DownscaleAnalogAudio(
            audio,
            lineLocations: [0.0, 100.0, 200.0],
            fieldLineCount: 2,
            fieldStartSample: 0,
            decodedFieldNumber: 0,
            isFirstField: true));

        Assert.Equal([0, 0, 0, 0], output);
        Assert.Equal(
            [("WARNING", "Analog audio processing error, muting samples")],
            diagnostics);
    }

    private static double[] BuildChannel(
        double centerFrequencyHz,
        int multiplier,
        int length = 34_685)
    {
        float center = (float)centerFrequencyHz;
        var values = new double[length];
        for (int i = 0; i < values.Length; i++)
        {
            float offset = (((i * multiplier) % 200_003) - 100_001) * 0.25f;
            values[i] = center + offset;
        }

        return values;
    }

    private static string Sha256Float32(double[] values)
    {
        var floatValues = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            floatValues[i] = (float)values[i];
        }

        return Sha256(MemoryMarshal.AsBytes(floatValues.AsSpan()));
    }

    private static string Sha256(ReadOnlySpan<byte> values)
        => Convert.ToHexString(SHA256.HashData(values));
}
