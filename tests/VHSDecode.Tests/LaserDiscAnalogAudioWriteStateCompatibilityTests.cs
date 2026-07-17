using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscAnalogAudioWriteStateCompatibilityTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public LaserDiscAnalogAudioWriteStateCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact(DisplayName = "LD filler writes preserve the prior analog-audio anchor like v0.4.0")]
    public void FillerWritesPreservePriorAnalogAudioAnchor()
    {
        using DecodeSession session = CreateSession();
        TbcDecodedField[] sourceFields =
        [
            BuildField(startSample: 0, diskLocation: 0.0, isFirstField: true, nextOffset: 100),
            BuildField(startSample: 100, diskLocation: 1.0, isFirstField: false, nextOffset: 100),
            BuildField(
                startSample: 200,
                diskLocation: 2.0,
                isFirstField: true,
                nextOffset: 240,
                hasAudio: false),
            BuildField(startSample: 440, diskLocation: 4.4, isFirstField: true, nextOffset: 100),
            BuildField(startSample: 540, diskLocation: 5.4, isFirstField: false, nextOffset: 100)
        ];
        var observedAnchors = new List<(long? StartSample, long FieldNumber)>();
        int readIndex = 0;
        var engine = new TbcFieldSequenceDecodeEngine(
            readField: (activeSession, _, _, _, _) =>
            {
                TbcFieldDecodeState state = activeSession.TbcFieldDecoder.CaptureState();
                observedAnchors.Add((
                    state.PreviousAnalogAudioStartSample,
                    state.PreviousAnalogAudioFieldNumber));
                return sourceFields[readIndex++];
            });

        IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
            session,
            Stream.Null,
            maxFields: sourceFields.Length);

        Assert.Equal(sourceFields.Length, fields.Count);
        Assert.Equal(
            [
                (StartSample: (long?)null, FieldNumber: 0L),
                (StartSample: (long?)0, FieldNumber: 0L),
                (StartSample: (long?)100, FieldNumber: 1L),
                (StartSample: (long?)200, FieldNumber: 2L),
                (StartSample: (long?)200, FieldNumber: 2L)
            ],
            observedAnchors);

        TbcFieldDecodeState finalState = session.TbcFieldDecoder.CaptureState();
        Assert.Equal(540, finalState.PreviousAnalogAudioStartSample);
        Assert.Equal(5, finalState.PreviousAnalogAudioFieldNumber);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private DecodeSession CreateSession()
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--threads",
            "0",
            "--noEFM",
            "input.s16",
            Path.Combine(_tempDirectory, "audio-anchor")
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField(
        long startSample,
        double diskLocation,
        bool isFirstField,
        double nextOffset,
        bool hasAudio = true)
    {
        return new TbcDecodedField(
            StartSample: startSample,
            Samples: [],
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                0,
                0,
                0,
                new SyncRange(0, 0),
                new SyncRange(0, 0),
                new SyncRange(0, 0)),
            SyncThresholdHz: 0,
            MeanLineLength: 100,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: isFirstField,
            DetectedFirstFieldConfidence: 100,
            AudioPcm: hasAudio ? [0, 0] : null,
            AudioSampleCount: hasAudio ? 2 : 0,
            DiskLocation: diskLocation,
            NextFieldOffsetSamples: nextOffset,
            NominalFieldLengthSamples: nextOffset);
    }
}
