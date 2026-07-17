using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscFieldsWrittenTimingCompatibilityTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public LaserDiscFieldsWrittenTimingCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact(DisplayName = "LD initial reads see the speculative v0.4.0 fields-written count")]
    public void InitialReadsUsePreWriteFieldCount()
    {
        using DecodeSession session = CreateSession("initial");
        var observedFieldNumbers = new List<int>();
        int decodedField = 0;
        var engine = CreateObservableEngine((_, _, begin, _, fieldNumber) =>
        {
            observedFieldNumbers.Add(fieldNumber);
            return BuildField(begin, decodedField++);
        });

        IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
            session,
            Stream.Null,
            maxFields: 3);

        Assert.Equal(3, fields.Count);
        Assert.Equal([0, 0, 1], observedFieldNumbers);
    }

    [Fact(DisplayName = "LD retry reads see the current v0.4.0 fields-written count")]
    public void RetryReadsUseCurrentFieldCount()
    {
        using DecodeSession session = CreateSession("retry");
        var observedFieldNumbers = new List<int>();
        int call = 0;
        var engine = CreateObservableEngine((_, _, begin, _, fieldNumber) =>
        {
            observedFieldNumbers.Add(fieldNumber);
            int currentCall = call++;
            return currentCall switch
            {
                0 => BuildField(begin, logicalField: 0),
                1 => BuildField(begin, logicalField: 1) with { LaserDiscAgcAdjusted = true },
                2 => BuildField(begin, logicalField: 1),
                _ => throw new InvalidOperationException("Unexpected LD field read.")
            };
        });

        IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
            session,
            Stream.Null,
            maxFields: 2);

        Assert.Equal(2, fields.Count);
        Assert.Equal([0, 0, 1], observedFieldNumbers);
    }

    [Fact(DisplayName = "LD recovery realigns the speculative fields-written count")]
    public void RecoveryRealignsPreWriteFieldCount()
    {
        using DecodeSession session = CreateSession("recovery");
        var observedFieldNumbers = new List<int>();
        int call = 0;
        var engine = CreateObservableEngine((_, _, begin, _, fieldNumber) =>
        {
            observedFieldNumbers.Add(fieldNumber);
            return call++ switch
            {
                0 => BuildField(begin, logicalField: 0),
                1 => throw new TbcFieldDecodeRecoveryException(
                    TbcFieldDecodeRecoveryKind.NoFirstHSync,
                    suggestedOffsetSamples: 50,
                    message: "synthetic LD recovery"),
                2 => BuildField(begin, logicalField: 1),
                _ => throw new InvalidOperationException("Unexpected LD field read.")
            };
        });

        IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
            session,
            Stream.Null,
            maxFields: 2);

        Assert.Equal(2, fields.Count);
        Assert.Equal([0, 0, 1], observedFieldNumbers);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private TbcFieldSequenceDecodeEngine CreateObservableEngine(TbcFieldSequenceReadField readField)
        => new(readField: readField)
        {
            UseSessionFieldNumberingForCustomReader = true
        };

    private DecodeSession CreateSession(string outputName)
    {
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.LaserDisc, [
            "--PAL",
            "--threads",
            "0",
            "--disable_analog_audio",
            "--noEFM",
            "input.s16",
            Path.Combine(_tempDirectory, outputName)
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField(long begin, int logicalField)
    {
        return new TbcDecodedField(
            StartSample: begin,
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
            DetectedFirstField: (logicalField & 1) == 0,
            DetectedFirstFieldConfidence: 100,
            DiskLocation: logicalField,
            NextFieldOffsetSamples: 100,
            NominalFieldLengthSamples: 100);
    }
}
