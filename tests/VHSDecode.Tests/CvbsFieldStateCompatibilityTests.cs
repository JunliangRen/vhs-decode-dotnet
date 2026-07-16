using System.Buffers.Binary;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CvbsFieldStateCompatibilityTests
{
    [Fact(DisplayName = "CVBS previous-field line zero uses the exact end-line projection")]
    public void PreviousFieldLineZeroUsesExactEndLineProjection()
    {
        double projected = TbcFieldDecodePipeline.TryProjectCvbsPreviousLine0(
                decodeType: "cvbs",
                previousEndLineAbsoluteSample: 1_624_576.1816440187,
                spanStartSample: 1_566_720)
            ?? throw new InvalidOperationException("Expected a CVBS previous-field projection.");

        Assert.Equal(57_856.18164401874, projected);
        Assert.Null(TbcFieldDecodePipeline.TryProjectCvbsPreviousLine0(
            decodeType: "ld",
            previousEndLineAbsoluteSample: 1_624_576.1816440187,
            spanStartSample: 1_566_720));
        Assert.Null(TbcFieldDecodePipeline.TryProjectCvbsPreviousLine0(
            decodeType: "cvbs",
            previousEndLineAbsoluteSample: double.NaN,
            spanStartSample: 1_566_720));
    }

    [Fact(DisplayName = "CVBS PAL burst levels retain the upstream float64 demod precision")]
    public void PalBurstLevelsRetainFloat64DemodPrecision()
    {
        TbcFieldDecodePipeline cvbs = CreatePipeline(out _, decodeType: "cvbs");
        TbcFieldDecodePipeline laserDisc = CreatePipeline(out _, decodeType: "ld");
        double[] video = Enumerable.Repeat(10_000.0, 1_000).ToArray();
        video[605] = 10_000.0;
        video[606] = 10_000.0000000001;
        video[607] = 9_999.9999999999;
        double[] lineLocations = Enumerable.Range(0, 10).Select(line => line * 100.0).ToArray();

        double cvbsBurst = cvbs.ComputeLaserDiscPalBurstLevel(video, lineLocations, line: 6, hzIre: 1.0)
            ?? throw new InvalidOperationException("Expected a CVBS PAL burst level.");
        double laserDiscBurst = laserDisc.ComputeLaserDiscPalBurstLevel(
                video,
                lineLocations,
                line: 6,
                hzIre: 1.0)
            ?? throw new InvalidOperationException("Expected a LaserDisc PAL burst level.");

        Assert.Equal(0x3DDFC1177F8FD7D9UL, BitConverter.DoubleToUInt64Bits(cvbsBurst));
        Assert.Equal(0.0, laserDiscBurst);
    }

    [Fact(DisplayName = "CVBS insufficient fields retain cadence but do not commit sync history")]
    public void InsufficientFieldsDoNotCommitSyncHistory()
    {
        TbcFieldDecodePipeline pipeline = CreatePipeline(out SyncAnalyzer analyzer);
        double[] video = BuildPalCvbsField(analyzer, line0: 3_500, sampleCount: 5_000);

        TbcFieldDecodeRecoveryException exception = Assert.Throws<TbcFieldDecodeRecoveryException>(
            () => pipeline.Decode(new RfDecodedSpan(
                StartSample: 900_000,
                Input: video,
                Video: video,
                DemodRaw: video,
                VideoLowPass: video)));

        Assert.Equal(TbcFieldDecodeRecoveryKind.InsufficientData, exception.Kind);
        TbcFieldDecodeState state = pipeline.CaptureState();
        Assert.Null(state.PreviousFirstHSyncLocation);
        Assert.Null(state.PreviousFirstHSyncReadLocation);
        Assert.Null(state.PreviousSyncConfidence);
        Assert.Null(state.PreviousCvbsEndLineAbsoluteSample);
        Assert.NotNull(state.PreviousDetectedFirstField);
    }

    [Fact(DisplayName = "CVBS successful fields commit their exact end-line history")]
    public void SuccessfulFieldsCommitExactEndLineHistory()
    {
        TbcFieldDecodePipeline pipeline = CreatePipeline(out SyncAnalyzer analyzer);
        double[] video = BuildPalCvbsField(analyzer, line0: 1_000, sampleCount: 5_000);

        TbcDecodedField field = pipeline.Decode(new RfDecodedSpan(
            StartSample: 100_000,
            Input: video,
            Video: video,
            DemodRaw: video,
            VideoLowPass: video));

        int fieldLineCount = field.DetectedFirstField == true ? 19 : 20;
        double expected = field.StartSample + field.LineLocations.Locations[fieldLineCount];
        Assert.Equal(expected, pipeline.CaptureState().PreviousCvbsEndLineAbsoluteSample);
    }

    [Fact(DisplayName = "CVBS recovery discards prevfield context and logs v0.4.0 diagnostics")]
    public void RecoveryDiscardsPreviousFieldContextAndLogsReleaseFourMessage()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBase = Path.Combine(tempDirectory, "cvbs-insufficient");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Cvbs, [
                "--pal",
                "input.s16",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            TbcFieldDecodeState initialState = session.TbcFieldDecoder.CaptureState();
            session.TbcFieldDecoder.RestoreStateForRetry(initialState with
            {
                PreviousFirstHSyncLocation = 1_000.0,
                PreviousFirstHSyncReadLocation = 100_000,
                PreviousSyncConfidence = 90,
                PreviousCvbsEndLineAbsoluteSample = 900_000.0,
                PreviousDetectedFirstField = true,
                PreviousHSyncDifference = 0.25,
                LaserDiscNtscPhaseAdjustMedian = 0.5,
                PreviousLaserDiscPalFieldPhaseId = 6,
                PreviousLaserDiscPalPhaseAdjustments = new Dictionary<int, double>
                {
                    [7] = 0.125
                },
                PreviousLaserDiscSkipCheckScore = 75
            });
            int attempts = 0;
            TbcFieldDecodeState? recoveredState = null;

            TbcDecodedField? ReadField(DecodeSession _, Stream __, long ___, int ____, int _____)
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new TbcFieldDecodeRecoveryException(
                        TbcFieldDecodeRecoveryKind.InsufficientData,
                        suggestedOffsetSamples: 2_560,
                        message: "insufficient field");
                }

                recoveredState = session.TbcFieldDecoder.CaptureState();
                return null;
            }

            IReadOnlyList<TbcDecodedField> fields = new TbcFieldSequenceDecodeEngine(
                readField: ReadField).DecodeFields(session, Stream.Null, maxFields: 1);

            Assert.Empty(fields);
            Assert.Equal(2, attempts);
            Assert.NotNull(recoveredState);
            Assert.Null(recoveredState.PreviousFirstHSyncLocation);
            Assert.Null(recoveredState.PreviousFirstHSyncReadLocation);
            Assert.Null(recoveredState.PreviousSyncConfidence);
            Assert.Null(recoveredState.PreviousCvbsEndLineAbsoluteSample);
            Assert.True(recoveredState.PreviousDetectedFirstField);
            Assert.Equal(-1.0, recoveredState.PreviousHSyncDifference);
            Assert.Equal(0.0, recoveredState.LaserDiscNtscPhaseAdjustMedian);
            Assert.Null(recoveredState.PreviousLaserDiscPalFieldPhaseId);
            Assert.Null(recoveredState.PreviousLaserDiscPalPhaseAdjustments);
            Assert.Equal(0, recoveredState.PreviousLaserDiscSkipCheckScore);
            string log = File.ReadAllText(outputBase + ".log");
            Assert.Contains(
                "ERROR - Missing data at the end of field, possibly dropped samples skipping a little.",
                log,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "CVBS failed serial lookahead publishes levels before pending-field rendering")]
    public void FailedSerialLookaheadPublishesLevelsBeforePendingFieldRendering()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "cvbs-lookahead-recovery");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Cvbs, [
                "--pal",
                "--threads",
                "0",
                "--length",
                "1",
                "input.s16",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            VideoOutputConverter firstConverter = BuildCvbsTestConverter(session, ire0: 10.0);
            VideoOutputConverter secondConverter = BuildCvbsTestConverter(session, ire0: 20.0);
            VideoOutputConverter failedConverter = BuildCvbsTestConverter(session, ire0: 30.0);
            TbcDecodedField first = BuildDeferredCvbsField(
                session,
                startSample: 0,
                fieldNumber: 0,
                videoLevel: 50.0,
                firstConverter,
                detectedFirstField: true,
                fieldPhaseId: 5);
            TbcDecodedField second = BuildDeferredCvbsField(
                session,
                startSample: 100,
                fieldNumber: 1,
                videoLevel: 60.0,
                secondConverter,
                detectedFirstField: false,
                fieldPhaseId: 6);
            ushort expectedFirst = RenderDeferredCvbsFirstSample(session, first, secondConverter);
            ushort expectedSecond = RenderDeferredCvbsFirstSample(session, second, failedConverter);
            int reads = 0;

            TbcDecodedField? ReadField(
                DecodeSession activeSession,
                Stream _,
                long begin,
                int __,
                int fieldNumber)
            {
                reads++;
                if (fieldNumber == 0)
                {
                    activeSession.TbcFieldDecoder.CurrentCvbsOutputConverter = firstConverter;
                    return first with { StartSample = begin };
                }

                if (fieldNumber == 1)
                {
                    activeSession.TbcFieldDecoder.CurrentCvbsOutputConverter = secondConverter;
                    return second with { StartSample = begin };
                }

                activeSession.TbcFieldDecoder.CurrentCvbsOutputConverter = failedConverter;
                throw new TbcFieldDecodeRecoveryException(
                    TbcFieldDecodeRecoveryKind.InsufficientData,
                    suggestedOffsetSamples: 100,
                    message: "insufficient field");
            }

            TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine(readField: ReadField)
                .TryDecodeAndWrite(session, Stream.Null);

            Assert.True(result.Success);
            Assert.Equal(3, reads);
            Assert.Equal(2, result.WrittenFieldCount);
            Assert.NotNull(result.Paths);
            byte[] tbc = File.ReadAllBytes(result.Paths.TbcPath);
            int fieldBytes = checked(session.TbcFrameSpec.FieldSampleCount * sizeof(ushort));
            Assert.Equal(expectedFirst, BinaryPrimitives.ReadUInt16LittleEndian(tbc));
            Assert.Equal(expectedSecond, BinaryPrimitives.ReadUInt16LittleEndian(tbc.AsSpan(fieldBytes)));
            string[] logLines = File.ReadAllLines(outputBase + ".log");
            Assert.Equal(2, logLines.Length);
            Assert.EndsWith(
                "ERROR - Missing data at the end of field, possibly dropped samples skipping a little.",
                logLines[0],
                StringComparison.Ordinal);
            Assert.EndsWith(
                "DEBUG - File Frame 0: CAV Pulldown/Telecine Frame",
                logLines[1],
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "CVBS skipped fields preserve v0.4.0 diagnostics without a false frame status")]
    public void SkippedFieldsPreserveDiagnosticsWithoutFalseFrameStatus()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string outputBase = Path.Combine(tempDirectory, "cvbs-skipped-field");
            ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Cvbs, [
                "--pal",
                "--threads",
                "0",
                "--length",
                "2",
                "input.s16",
                outputBase
            ]);
            using DecodeSession session = DecodeSessionFactory.Create(command);
            TbcDecodedField[] sourceFields =
            [
                BuildOutputCvbsField(session, 0, detectedFirstField: true, fieldPhaseId: 5, diskLocation: 0.0),
                BuildOutputCvbsField(session, 100, detectedFirstField: false, fieldPhaseId: 6, diskLocation: 1.0),
                BuildOutputCvbsField(session, 300, detectedFirstField: false, fieldPhaseId: 8, diskLocation: 2.9)
            ];
            int reads = 0;

            TbcDecodedField? ReadField(DecodeSession _, Stream __, long begin, int ___, int fieldNumber)
            {
                reads++;
                return fieldNumber < sourceFields.Length
                    ? sourceFields[fieldNumber] with { StartSample = begin }
                    : null;
            }

            TbcFieldSequenceDecodeResult result = new TbcFieldSequenceDecodeEngine(readField: ReadField)
                .TryDecodeAndWrite(session, Stream.Null);

            Assert.True(result.Success);
            Assert.Equal(3, reads);
            Assert.Equal(4, result.WrittenFieldCount);
            string[] logLines = File.ReadAllLines(outputBase + ".log");
            Assert.Equal(3, logLines.Length);
            Assert.EndsWith(
                "DEBUG - File Frame 0: CAV Pulldown/Telecine Frame",
                logLines[0],
                StringComparison.Ordinal);
            Assert.EndsWith(
                "WARNING - At field #2, Field phaseID sequence mismatch (6->8) (player may be paused)",
                logLines[1],
                StringComparison.Ordinal);
            Assert.EndsWith("ERROR - Skipped field", logLines[2], StringComparison.Ordinal);
            Assert.DoesNotContain("File Frame 1:", File.ReadAllText(outputBase + ".log"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static TbcFieldDecodePipeline CreatePipeline(
        out SyncAnalyzer analyzer,
        string decodeType = "cvbs")
    {
        analyzer = new SyncAnalyzer(
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
        return new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(frameSpec, converter),
            converter,
            "PAL",
            TbcDropoutDetectionOptions.Disabled,
            syncDetectionOptions: SyncDetectionOptions.Disabled,
            decodeType: decodeType);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static VideoOutputConverter BuildCvbsTestConverter(DecodeSession session, double ire0)
    {
        return new VideoOutputConverter(
            ire0,
            hzIre: 1.0,
            session.VideoOutput.OutputZero,
            session.VideoOutput.VSyncIre,
            session.VideoOutput.OutputScale);
    }

    private static TbcDecodedField BuildDeferredCvbsField(
        DecodeSession session,
        long startSample,
        int fieldNumber,
        double videoLevel,
        VideoOutputConverter converter,
        bool detectedFirstField,
        int fieldPhaseId)
    {
        int lineLength = session.TbcFrameSpec.OutputLineLength;
        var lineLocations = new double[session.TbcFrameSpec.OutputLineCount + 1];
        for (int line = 0; line < lineLocations.Length; line++)
        {
            lineLocations[line] = (double)line * lineLength;
        }

        double[] video = Enumerable.Repeat(
            videoLevel,
            checked((lineLocations.Length * lineLength) + 16)).ToArray();
        return BuildOutputCvbsField(
                session,
                startSample,
                detectedFirstField,
                fieldPhaseId,
                diskLocation: startSample / 100.0)
            with
            {
                Samples = [],
                LineLocations = new LineLocationResult(lineLocations, new bool[lineLocations.Length]),
                OutputConverter = converter,
                DeferredRenderSource = new TbcDeferredRenderSource(
                    video,
                    lineLocations,
                    FirstLine: 0,
                    fieldNumber)
            };
    }

    private static TbcDecodedField BuildOutputCvbsField(
        DecodeSession session,
        long startSample,
        bool detectedFirstField,
        int fieldPhaseId,
        double diskLocation)
    {
        return new TbcDecodedField(
            StartSample: startSample,
            Samples: new ushort[session.TbcFrameSpec.FieldSampleCount],
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                0,
                0,
                0,
                new SyncRange(0, 0),
                new SyncRange(0, 0),
                new SyncRange(0, 0)),
            SyncThresholdHz: 0,
            MeanLineLength: 0,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: detectedFirstField,
            DetectedFirstFieldConfidence: 100,
            DiskLocation: diskLocation,
            MedianBurstIre: 4.0,
            FieldPhaseId: fieldPhaseId,
            VitsMetrics: new Dictionary<string, double>
            {
                ["bPSNR"] = 30.0
            },
            VbiData: [],
            NextFieldOffsetSamples: 100.0,
            NominalFieldLengthSamples: 100.0);
    }

    private static ushort RenderDeferredCvbsFirstSample(
        DecodeSession session,
        TbcDecodedField field,
        VideoOutputConverter converter)
    {
        TbcDeferredRenderSource source = field.DeferredRenderSource
            ?? throw new InvalidOperationException("Expected deferred CVBS render data.");
        return session.TbcRenderer.RenderFieldPayload(
            source.VideoHz,
            source.LineLocations,
            firstLine: source.FirstLine,
            fieldNumber: source.FieldNumber,
            converterOverride: converter).Samples[0];
    }

    private static double[] BuildPalCvbsField(
        SyncAnalyzer analyzer,
        int line0,
        int sampleCount)
    {
        double[] data = Enumerable.Repeat(100.0, sampleCount).ToArray();
        int lineLength = checked((int)analyzer.NominalLineLength);
        int hSyncLength = checked((int)Math.Round(analyzer.UsecToSamples(analyzer.HSyncPulseUs)));
        for (int lineStart = 0; lineStart < data.Length; lineStart += lineLength)
        {
            PaintPulse(data, lineStart, hSyncLength, -40.0);
        }

        PaintVBlank(data, analyzer, line0, isFirstField: true);
        return data;
    }

    private static void PaintVBlank(
        double[] data,
        SyncAnalyzer analyzer,
        int line0,
        bool isFirstField)
    {
        double halfLine = analyzer.NominalLineLength / 2.0;
        double firstHSyncHalfLines = isFirstField ? 1.0 : 2.0;
        double secondEqualizingHalfLines = isFirstField ? 1.0 : 2.0;
        int hSyncLength = checked((int)Math.Round(analyzer.UsecToSamples(analyzer.HSyncPulseUs)));
        int equalizingLength = checked((int)Math.Round(analyzer.UsecToSamples(analyzer.EqualizingPulseUs)));
        int vSyncLength = checked((int)Math.Round(analyzer.UsecToSamples(analyzer.VSyncPulseUs)));
        int AtHalfLine(double halfLines) => line0 + checked((int)Math.Round(halfLines * halfLine));

        double followingHSyncHalfLines = firstHSyncHalfLines
            + (3.0 * analyzer.NumPulses)
            - 1.0
            + secondEqualizingHalfLines;
        int regionEnd = Math.Min(
            data.Length,
            AtHalfLine(followingHSyncHalfLines + 2.0));
        if (regionEnd > line0)
        {
            Array.Fill(data, 100.0, line0, regionEnd - line0);
        }

        PaintPulse(data, line0, hSyncLength, -40.0);
        int equalizing1Start = AtHalfLine(firstHSyncHalfLines);
        for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
        {
            PaintPulse(
                data,
                equalizing1Start + checked((int)Math.Round(pulse * halfLine)),
                equalizingLength,
                -40.0);
        }

        int vSyncStart = equalizing1Start
            + checked((int)Math.Round(analyzer.NumPulses * halfLine));
        for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
        {
            PaintPulse(
                data,
                vSyncStart + checked((int)Math.Round(pulse * halfLine)),
                vSyncLength,
                -40.0);
        }

        int equalizing2Start = vSyncStart
            + checked((int)Math.Round(analyzer.NumPulses * halfLine));
        for (int pulse = 0; pulse < analyzer.NumPulses; pulse++)
        {
            PaintPulse(
                data,
                equalizing2Start + checked((int)Math.Round(pulse * halfLine)),
                equalizingLength,
                -40.0);
        }

        int followingHSync = equalizing2Start
            + checked((int)Math.Round(
                (analyzer.NumPulses - 1 + secondEqualizingHalfLines) * halfLine));
        PaintPulse(data, followingHSync, hSyncLength, -40.0);
    }

    private static void PaintPulse(double[] data, int start, int length, double value)
    {
        int begin = Math.Max(0, start);
        int end = Math.Min(data.Length, checked(start + length));
        if (end > begin)
        {
            Array.Fill(data, value, begin, end - begin);
        }
    }
}
