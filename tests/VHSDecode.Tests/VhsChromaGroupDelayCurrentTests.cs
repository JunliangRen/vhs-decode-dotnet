using System.Buffers.Binary;
using System.Security.Cryptography;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaGroupDelayCurrentTests
{
    private const string ShiftedOracleHash =
        "D7370B06FFB6AA869C8A88FB612E82D433FD1683369341F1FDB1C7BE4A5EFEEE";

    [Theory(DisplayName = "Current NTSC chroma group delay preserves pinned phase truthiness")]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    public void CurrentNtscChromaGroupDelayPreservesPinnedPhaseTruthiness(
        bool isFirstField,
        int fieldNumber)
    {
        VhsChromaFieldOptions options = CreateOptions();

        double shift = VhsChromaDecoder.CurrentNtscChromaGroupDelayShiftSamples(
            options,
            isFirstField,
            fieldNumber);

        Assert.Equal(
            unchecked((long)0x400CF758DC1AE919UL),
            BitConverter.DoubleToInt64Bits(shift));
    }

    [Fact(DisplayName = "Current chroma group delay follows upstream phase-correction gates")]
    public void CurrentChromaGroupDelayFollowsUpstreamPhaseCorrectionGates()
    {
        VhsChromaFieldOptions pal = CreateOptions(colorSystem: "PAL");
        VhsChromaFieldOptions disabled = CreateOptions(disablePhaseCorrection: true);

        Assert.Equal(
            0.0,
            VhsChromaDecoder.CurrentNtscChromaGroupDelayShiftSamples(
                pal,
                isFirstField: true,
                fieldNumber: 0));
        Assert.Equal(
            0.0,
            VhsChromaDecoder.CurrentNtscChromaGroupDelayShiftSamples(
                disabled,
                isFirstField: true,
                fieldNumber: 0));
    }

    [Theory(DisplayName = "Current shifted TBC resampling matches the pinned Numba oracle")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    public void CurrentShiftedTbcResamplingMatchesPinnedNumbaOracle(int workerThreads)
    {
        double[] source = BuildOracleSource();
        var resampler = new TbcLineResampler(
            outputLineLength: 16,
            nominalInputLineLength: 32.5,
            workerThreads: workerThreads);
        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            [40.25, 72.75, 105.25, 137.75],
            firstLine: 1,
            lineCount: 2);
        double shift = VhsChromaDecoder.CurrentNtscChromaGroupDelayShiftSamples(
            CreateOptions(),
            isFirstField: true,
            fieldNumber: 0);

        double[] actual = resampler.ResamplePreparedShifted(source, plan, shift);

        Assert.Equal(ShiftedOracleHash, Float32BitsSha256(actual));
    }

    [Fact(DisplayName = "Zero chroma shift preserves the legacy prepared path")]
    public void ZeroChromaShiftPreservesLegacyPreparedPath()
    {
        double[] source = BuildOracleSource();
        var resampler = new TbcLineResampler(
            outputLineLength: 16,
            nominalInputLineLength: 32.5);
        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            [40.25, 72.75, 105.25, 137.75],
            firstLine: 1,
            lineCount: 2);
        double[] expected = resampler.ResamplePrepared(source, plan);
        var actual = new double[expected.Length];

        resampler.ResamplePreparedShifted(source, plan, 0.0, actual);

        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "Shifted TBC resampling is deterministic across parallel workers")]
    public void ShiftedTbcResamplingIsDeterministicAcrossParallelWorkers()
    {
        const int OutputLineLength = 1_024;
        const int LineCount = 70;
        const double InputLineLength = 1_200.0;
        double[] source = BuildLargeSource(100_000);
        double[] lineLocations = Enumerable.Range(0, LineCount + 2)
            .Select(line => 128.25 + (line * InputLineLength))
            .ToArray();
        var serial = new TbcLineResampler(
            OutputLineLength,
            nominalInputLineLength: InputLineLength,
            workerThreads: 1);
        var parallel = new TbcLineResampler(
            OutputLineLength,
            nominalInputLineLength: InputLineLength,
            workerThreads: 20);
        using TbcLineResampler.ResamplingPlan serialPlan = serial.PrepareLineResampling(
            lineLocations,
            firstLine: 1,
            LineCount);
        using TbcLineResampler.ResamplingPlan parallelPlan = parallel.PrepareLineResampling(
            lineLocations,
            firstLine: 1,
            LineCount);
        double shift = VhsChromaDecoder.CurrentNtscChromaGroupDelayShiftSamples(
            CreateOptions(),
            isFirstField: false,
            fieldNumber: 2);
        double[] expected = serial.ResamplePreparedShifted(source, serialPlan, shift);
        double[] actual = parallel.ResamplePreparedShifted(source, parallelPlan, shift);
        var destination = new double[actual.Length];

        parallel.ResamplePreparedShifted(source, parallelPlan, shift, destination);

        Assert.Equal(expected, actual);
        Assert.Equal(expected, destination);
    }

    [Fact(DisplayName = "Current chroma group delay is profile and output gated")]
    public void CurrentChromaGroupDelayIsProfileAndOutputGated()
    {
        using DecodeSession current = CreateSession(
            "--ntsc",
            "--compat-version",
            "current");
        using DecodeSession v040 = CreateSession(
            "--ntsc",
            "--compat-version",
            "v0.4.0");
        using DecodeSession skipped = CreateSession(
            "--ntsc",
            "--compat-version",
            "current",
            "--skip_chroma");

        Assert.True(current.TbcFieldDecoder.CurrentVhsChromaGroupDelayEnabled);
        Assert.False(v040.TbcFieldDecoder.CurrentVhsChromaGroupDelayEnabled);
        Assert.False(skipped.TbcFieldDecoder.CurrentVhsChromaGroupDelayEnabled);
    }

    [Fact(DisplayName = "Shifted TBC resampling rejects non-finite shifts")]
    public void ShiftedTbcResamplingRejectsNonFiniteShifts()
    {
        var resampler = new TbcLineResampler(outputLineLength: 16);
        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            [40.0, 72.0],
            firstLine: 0,
            lineCount: 1);
        double[] source = BuildOracleSource();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => resampler.ResamplePreparedShifted(source, plan, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => resampler.ResamplePreparedShifted(source, plan, double.PositiveInfinity));
    }

    private static VhsChromaFieldOptions CreateOptions(
        string colorSystem = "NTSC",
        bool disablePhaseCorrection = false)
        => new(
            colorSystem,
            OutputLineLength: 16,
            OutputLineCount: 2,
            OutputSampleRateHz: 14_318_181.818181818,
            FscMHz: 3.5795454545454546,
            ColorUnderCarrierHz: 629_370.6293706294,
            BurstStart: 2,
            BurstEnd: 8,
            BurstAbsRef: 4_416.0,
            ChromaRotation: [-1, 1],
            DisableComb: false,
            DisablePhaseCorrection: disablePhaseCorrection,
            EnableColorKiller: false,
            DetectChromaTrackPhase: false);

    private static DecodeSession CreateSession(params string[] options)
    {
        string[] arguments = [.. options, "input.s16", "output"];
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
        return DecodeSessionFactory.Create(command);
    }

    private static double[] BuildOracleSource()
    {
        var source = new double[192];
        for (int index = 0; index < source.Length; index++)
        {
            int signed = ((index * 73) % 257) - 128;
            source[index] = (float)(
                (signed * 0.125)
                + (((index % 7) - 3) * 0.03125));
        }

        return source;
    }

    private static double[] BuildLargeSource(int length)
    {
        var source = new double[length];
        for (int index = 0; index < source.Length; index++)
        {
            int signed = ((index * 47) % 509) - 254;
            source[index] = (float)(
                (signed * 0.0625)
                + (((index % 11) - 5) * 0.015625));
        }

        return source;
    }

    private static string Float32BitsSha256(ReadOnlySpan<double> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(int))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(int), sizeof(int)),
                BitConverter.SingleToInt32Bits((float)values[index]));
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
