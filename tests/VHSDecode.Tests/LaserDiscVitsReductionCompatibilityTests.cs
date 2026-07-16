using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscVitsReductionCompatibilityTests
{
    [Fact(DisplayName = "NTSC LD white VITS gate uses NumPy float64 pairwise mean")]
    public void NtscLaserDiscWhiteVitsGateUsesNumpyFloat64PairwiseMean()
    {
        using DecodeSession session = CreateSession("--NTSC");
        ushort baseline = session.VideoOutput.ConvertHz(session.VideoOutput.IreToHz(50.0));
        ushort[] samples = Enumerable.Repeat(baseline, session.TbcFrameSpec.FieldSampleCount).ToArray();
        (int start, int length) = TbcSlice(session, line: 20, startUsec: 52.0, lengthUsec: 8.0);
        Assert.Equal(114, length);

        ulong state = 30;
        long deltaSum = 0;
        for (int i = 0; i < length - 1; i++)
        {
            state = NextState(state);
            int delta = (int)((state >> 32) % 10_001) - 5_000;
            samples[start + i] = (ushort)(47_616 + delta);
            deltaSum += delta;
        }

        int lastDelta = checked((int)-deltaSum);
        Assert.InRange(lastDelta, -5_000, 5_000);
        samples[start + length - 1] = (ushort)(47_616 + lastDelta);

        IReadOnlyDictionary<string, double> metrics = TbcOutputMetadataWriter.ComputeBasicVitsMetrics(
            session,
            CreateField(samples, isFirstField: true),
            isFirstField: true);

        Assert.Equal(90.0, metrics["whiteIRE"]);
        Assert.True(metrics.ContainsKey("wSNR"));
    }

    [Fact(DisplayName = "NTSC LD post-TBC black mean converts after NumPy uint16 reduction")]
    public void NtscLaserDiscPostTbcBlackMeanConvertsAfterNumpyUint16Reduction()
    {
        using DecodeSession session = CreateSession("--NTSC");
        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        (int start, int length) = TbcSlice(session, line: 1, startUsec: 10.0, lengthUsec: 20.0);
        Assert.Equal(287, length);
        Array.Fill(samples, (ushort)14_909, start, 143);
        samples[start + 143] = 14_912;
        Array.Fill(samples, (ushort)14_915, start + 144, 143);

        IReadOnlyDictionary<string, double> metrics = TbcOutputMetadataWriter.ComputeBasicVitsMetrics(
            session,
            CreateField(samples, isFirstField: true),
            isFirstField: true);

        Assert.Equal(-1.2, metrics["blackLinePostTBCIRE"]);
    }

    [Fact(DisplayName = "PAL LD burst VITS level uses Numba sequential RMS")]
    public void PalLaserDiscBurstVitsLevelUsesNumbaSequentialRms()
    {
        using DecodeSession session = CreateSession("--PAL");
        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];
        (int start, int length) = TbcSlice(session, line: 13, startUsec: 36.0, lengthUsec: 20.0);
        Assert.Equal(355, length);
        Array.Fill(samples, (ushort)294, start, 71);

        IReadOnlyDictionary<string, double> metrics = TbcOutputMetadataWriter.ComputeBasicVitsMetrics(
            session,
            CreateField(samples, isFirstField: false),
            isFirstField: false);

        Assert.Equal(0.313, metrics["palVITSBurst50Level"]);
    }

    [Fact(DisplayName = "PAL LD grey VITS preserves NumPy uint16 subtraction wrap")]
    public void PalLaserDiscGreyVitsPreservesNumpyUInt16SubtractionWrap()
    {
        using DecodeSession session = CreateSession("--PAL");
        ushort[] samples = new ushort[session.TbcFrameSpec.FieldSampleCount];

        IReadOnlyDictionary<string, double> metrics = TbcOutputMetadataWriter.ComputeBasicVitsMetrics(
            session,
            CreateField(samples, isFirstField: true),
            isFirstField: true);

        Assert.Equal(130.6, metrics["greyIRE"]);
    }

    private static DecodeSession CreateSession(string systemOption)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc,
        [
            systemOption,
            "--verboseVITS",
            "--noEFM",
            "--disable_analog_audio",
            "input.s16",
            "outbase"
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static (int Start, int Length) TbcSlice(
        DecodeSession session,
        int line,
        double startUsec,
        double lengthUsec)
    {
        double samplesPerUsec = session.TbcFrameSpec.OutputSampleRateHz / 1_000_000.0;
        double begin = ((line - 1) * session.TbcFrameSpec.OutputLineLength)
            + (startUsec * samplesPerUsec);
        int start = (int)Math.Round(begin, MidpointRounding.ToEven);
        int end = (int)Math.Round(
            begin + (lengthUsec * samplesPerUsec),
            MidpointRounding.ToEven);
        return (start, end - start);
    }

    private static TbcDecodedField CreateField(ushort[] samples, bool isFirstField)
        => new(
            StartSample: 0,
            Samples: samples,
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                NominalLineLength: 1.0,
                HSyncMedian: 1.0,
                HSyncOffset: 0.0,
                HSync: new SyncRange(0.0, 0.0),
                Equalizing: new SyncRange(0.0, 0.0),
                VSync: new SyncRange(0.0, 0.0)),
            SyncThresholdHz: 0.0,
            MeanLineLength: 1.0,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: isFirstField,
            FieldPhaseId: 1);

    private static ulong NextState(ulong state)
        => unchecked(
            (state * 6_364_136_223_846_793_005UL)
            + 1_442_695_040_888_963_407UL);
}
