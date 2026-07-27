using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class FallbackVSyncNumericsCompatibilityTests
{
    [Fact(DisplayName = "VHS fallback VSync search uses the upstream nominal line length")]
    public void VhsFallbackVSyncSearchUsesNominalLineLength()
    {
        const double measuredLineLength = 100.0;
        // Both candidates are 0.5 nominal lines, but 1.5 measured lines, from the prediction.
        Pulse[] rawPulses =
        [
            new(100, 10),
            new(400, 10)
        ];
        ClassifiedSyncPulse[] validPulses =
        [
            new(SyncPulseKind.HSync, rawPulses[0], false),
            new(SyncPulseKind.HSync, rawPulses[1], true)
        ];

        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 300.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 40.0,
            numPulses: 6);
        Assert.Equal(300.0, analyzer.NominalLineLength);
        var spec = new TbcFrameSpec(
            "NTSC",
            OutputLineLength: 4,
            OutputLineCount: 12,
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
            analyzer,
            new TbcFieldRenderer(spec, converter),
            converter,
            "NTSC",
            TbcDropoutDetectionOptions.Disabled,
            syncDetectionOptions: new SyncDetectionOptions(
                DetectLevels: true,
                LevelDetectDivisor: 1,
                UseFallbackVSync: true),
            decodeType: "vhs");

        const long previousReadLocation = 0;
        const double previousFirstHSyncLocation = 1_000.0;
        const double targetExpectedLine0 = 250.0;
        const long spanStartSample = 1_100;
        TbcFieldDecodeState state = pipeline.CaptureState() with
        {
            PreviousFirstHSyncLocation = previousFirstHSyncLocation,
            PreviousFirstHSyncReadLocation = previousReadLocation,
            PreviousDetectedFirstField = false
        };
        pipeline.RestoreStateForRetry(state);

        var timing = new SyncTiming(
            analyzer.NominalLineLength,
            10.0,
            0.0,
            new SyncRange(8.0, 12.0),
            new SyncRange(4.0, 6.0),
            new SyncRange(35.0, 60.0));
        Line0FallbackCandidate resolution = pipeline.TryResolveFallbackLine0(
            validPulses,
            rawPulses,
            new double[500],
            timing,
            spanStartSample,
            measuredLineLength)!;

        Assert.Equal(targetExpectedLine0, resolution.ExpectedLocation);
        Assert.Equal(100.0, resolution.Location);
        FallbackVSyncResolution? measuredLengthResolution = FallbackVSyncResolver.Resolve(
            validPulses,
            rawPulses,
            new double[500],
            timing.VSync,
            measuredLineLength,
            numEqualizingPulses: 6,
            frameLines: 525,
            expectedLine0: resolution.ExpectedLocation,
            expectedFirstField: true);
        Assert.Null(measuredLengthResolution);
    }

    [Fact(DisplayName = "VHS fallback VSync content check uses NumPy float64 reductions")]
    public void VhsFallbackVSyncContentCheckUsesNumpyFloat64Reductions()
    {
        Pulse[] pulses =
        [
            new(100, 96),
            new(1_300, 96),
            new(2_500, 66),
            new(3_100, 48),
            new(3_700, 48),
            new(4_300, 48)
        ];
        var demodLowPass = new double[5_000];
        Array.Fill(demodLowPass, 16.0, 1_436, 1_024);

        double boundaryValue = BitConverter.Int64BitsToDouble(0x402CF3CF3CF3CF3B);
        Array.Fill(demodLowPass, boundaryValue, 2_606, 454);
        for (int i = 3_188; i < 3_660; i++)
        {
            demodLowPass[i] = ((i - 3_188) & 1) == 0 ? 4.0 : 12.0;
        }

        (double mean, double standardDeviation) =
            NumpyReduction.MeanStandardDeviationFloat64(demodLowPass.AsSpan(2_606, 454));
        Assert.Equal(0x402CF3CF3CF3CF3CUL, BitConverter.DoubleToUInt64Bits(mean));
        Assert.Equal(0x3CE0000000000000UL, BitConverter.DoubleToUInt64Bits(standardDeviation));

        FallbackVSyncResolution? resolution = FallbackVSyncResolver.Resolve(
            validPulses: [],
            rawPulses: pulses,
            demodLowPass,
            vSyncRange: new SyncRange(300.0, 400.0),
            meanLineLength: 1_200.0,
            numEqualizingPulses: 6,
            frameLines: 525);

        Assert.Null(resolution);
    }
}
