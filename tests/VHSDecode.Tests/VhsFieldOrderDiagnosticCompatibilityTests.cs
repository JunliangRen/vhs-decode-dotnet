using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsFieldOrderDiagnosticCompatibilityTests : IDisposable
{
    private const string ManualFlipMessage =
        "Possibly skipped field (Two fields with same isFirstField in a row), manually flipping the field order to compensate";
    private const string DuplicateMessage =
        "Possibly skipped field (Two fields with same isFirstField in a row), duplicating the last field to compensate...";
    private const string DropMessage =
        "Possibly skipped field (Two fields with same isFirstField in a row), dropping the last field to compensate...";
    private const string ProgressiveMessage =
        "Detected progressive video content..., manually flipping the field order to compensate";

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public VhsFieldOrderDiagnosticCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Theory(DisplayName = "VHS field-order corrections emit exact v0.4.0 diagnostics")]
    [InlineData("none", ManualFlipMessage)]
    [InlineData("duplicate", DuplicateMessage)]
    [InlineData("drop", DropMessage)]
    public void FieldOrderCorrectionEmitsExactDiagnostic(string action, string expectedMessage)
    {
        using DecodeSession session = CreateSession(action, tapeFormat: null);
        var error = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);

        Decode(session, [true, true]);

        Assert.Equal(expectedMessage + Environment.NewLine, error.ToString());
        Assert.Contains(
            "ERROR - " + expectedMessage,
            File.ReadAllText(session.OutputBase + ".log"),
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "VHS progressive field detection emits exact v0.4.0 diagnostic")]
    public void ProgressiveDetectionEmitsExactDiagnostic()
    {
        using DecodeSession session = CreateSession("none", tapeFormat: null);
        var error = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);

        Decode(session, [true, true, true, true]);

        Assert.Equal(
            ManualFlipMessage + Environment.NewLine + ProgressiveMessage + Environment.NewLine,
            error.ToString());
        string log = File.ReadAllText(session.OutputBase + ".log");
        Assert.Contains("ERROR - " + ManualFlipMessage, log, StringComparison.Ordinal);
        Assert.Contains("ERROR - " + ProgressiveMessage, log, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "TYPEC field-order correction remains silent like v0.4.0")]
    public void TypeCManualFlipDoesNotEmitDiagnostic()
    {
        using DecodeSession session = CreateSession("duplicate", "TYPEC");
        var error = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);

        Decode(session, [true, true]);

        Assert.Empty(error.ToString());
        Assert.False(File.Exists(session.OutputBase + ".log"));
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private DecodeSession CreateSession(string action, string? tapeFormat)
    {
        string outputBase = Path.Combine(
            _tempDirectory,
            action + "-" + (tapeFormat ?? "VHS") + "-" + Guid.NewGuid().ToString("N"));
        var arguments = new List<string>
        {
            "--pal",
            "--threads",
            "0",
            "--no_resample",
            "--field_order_action",
            action
        };
        if (tapeFormat is not null)
        {
            arguments.Add("--tf");
            arguments.Add(tapeFormat);
        }

        arguments.Add("input.s16");
        arguments.Add(outputBase);
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [.. arguments]);
        return DecodeSessionFactory.Create(command);
    }

    private static void Decode(DecodeSession session, IReadOnlyList<bool> detectedFirstFields)
    {
        int index = 0;
        var engine = new TbcFieldSequenceDecodeEngine(
            readField: (activeSession, _, begin, _, _) => BuildField(
                activeSession,
                begin,
                detectedFirstFields[index],
                index++));

        IReadOnlyList<TbcDecodedField> fields = engine.DecodeFields(
            session,
            Stream.Null,
            maxFields: detectedFirstFields.Count);

        Assert.NotEmpty(fields);
    }

    private static TbcDecodedField BuildField(
        DecodeSession session,
        long startSample,
        bool detectedFirstField,
        int diskLocation)
    {
        const double meanLineLength = 100.0;
        var locations = new double[session.TbcFrameSpec.OutputLineCount + 1];
        for (int i = 0; i < locations.Length; i++)
        {
            locations[i] = i * meanLineLength;
        }

        return new TbcDecodedField(
            StartSample: startSample,
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
            DetectedFirstField: detectedFirstField,
            DetectedFirstFieldConfidence: 100,
            DiskLocation: diskLocation,
            NextFieldOffsetSamples: meanLineLength,
            NominalFieldLengthSamples: meanLineLength);
    }
}
