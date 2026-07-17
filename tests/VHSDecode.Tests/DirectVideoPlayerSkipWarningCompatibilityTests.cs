using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class DirectVideoPlayerSkipWarningCompatibilityTests : IDisposable
{
    private const string Warning = "WARNING: Possible player skip detected - check output";
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public DirectVideoPlayerSkipWarningCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Theory(DisplayName = "LD/CVBS player-skip warning uses v0.4.0 confidence and inclusive length bounds")]
    [InlineData("ld", 49, -2.0, false)]
    [InlineData("ld", 49, 2.0, false)]
    [InlineData("ld", 49, 2.001, true)]
    [InlineData("cvbs", 49, -2.001, true)]
    [InlineData("ld", 50, 3.0, false)]
    [InlineData("vhs", 49, 3.0, false)]
    [InlineData("ld", 49, double.NaN, true)]
    public void WarningConditionMatchesV040(
        string decoderName,
        int syncConfidence,
        double lengthOffset,
        bool expected)
    {
        using DecodeSession session = CreateSession(decoderName, "condition");
        double normalizedLength = double.IsNaN(lengthOffset)
            ? double.NaN
            : session.TbcFrameSpec.OutputLineCount + lengthOffset;
        TbcDecodedField field = BuildField(session, normalizedLength, syncConfidence);

        Assert.Equal(
            expected,
            TbcFieldSequenceDecodeEngine.ShouldWarnDirectVideoPlayerSkip(session, field));
    }

    [Theory(DisplayName = "LD/CVBS sequence emits the exact v0.4.0 player-skip warning")]
    [InlineData("ld")]
    [InlineData("cvbs")]
    public void SequenceEmitsExactWarning(string decoderName)
    {
        using DecodeSession session = CreateSession(decoderName, "sequence");
        var error = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);
        TbcDecodedField field = BuildField(
            session,
            session.TbcFrameSpec.OutputLineCount + 3.0,
            syncConfidence: 49);
        var engine = new TbcFieldSequenceDecodeEngine(
            readField: (_, _, _, _, _) => field);

        IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
            session,
            Stream.Null,
            maxFields: 1);

        Assert.Single(fields);
        Assert.Equal(Warning + Environment.NewLine, error.ToString());
        Assert.Contains(
            "WARNING - " + Warning,
            File.ReadAllText(session.OutputBase + ".log"),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private DecodeSession CreateSession(string decoderName, string outputName)
    {
        string outputBase = Path.Combine(
            _tempDirectory,
            outputName + "-" + decoderName + "-" + Guid.NewGuid().ToString("N"));
        ParsedCommand command = decoderName switch
        {
            "ld" => new CommandLineParser().Parse(CliSpecs.LaserDisc, [
                "--PAL",
                "--threads",
                "0",
                "--disable_analog_audio",
                "--noEFM",
                "input.s16",
                outputBase
            ]),
            "cvbs" => new CommandLineParser().Parse(CliSpecs.Cvbs, [
                "--system",
                "PAL",
                "--threads",
                "0",
                "input.s16",
                outputBase
            ]),
            "vhs" => new CommandLineParser().Parse(CliSpecs.Vhs, [
                "--pal",
                "--threads",
                "0",
                "input.s16",
                outputBase
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(decoderName))
        };
        return DecodeSessionFactory.Create(command);
    }

    private static TbcDecodedField BuildField(
        DecodeSession session,
        double normalizedLength,
        int syncConfidence)
    {
        const double meanLineLength = 100.0;
        int outputLines = session.TbcFrameSpec.OutputLineCount;
        var locations = new double[outputLines + 1];
        for (int i = 0; i < locations.Length; i++)
        {
            locations[i] = i * meanLineLength;
        }

        locations[outputLines] = normalizedLength * meanLineLength;
        return new TbcDecodedField(
            StartSample: 0,
            Samples: [],
            LineLocations: new LineLocationResult(locations, new bool[locations.Length]),
            Timing: new SyncTiming(
                0,
                0,
                0,
                new SyncRange(0, 0),
                new SyncRange(0, 0),
                new SyncRange(0, 0)),
            SyncThresholdHz: 0,
            MeanLineLength: meanLineLength,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: true,
            DetectedFirstFieldConfidence: syncConfidence,
            DiskLocation: 0,
            NextFieldOffsetSamples: meanLineLength,
            NominalFieldLengthSamples: meanLineLength,
            SyncConfidence: syncConfidence);
    }
}
