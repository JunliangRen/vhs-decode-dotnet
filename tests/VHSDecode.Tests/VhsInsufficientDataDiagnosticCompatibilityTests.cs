using System.Reflection;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsInsufficientDataDiagnosticCompatibilityTests : IDisposable
{
    private const string DetailsMessage =
        "lastline = 89.0, proclines = 100, meanlinelen = 10.0, line0loc = 100.0)";
    private const string SkipMessage =
        "Did not find the expected number of lines (lastline < proclines) , skipping a tiny bit";

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public VhsInsufficientDataDiagnosticCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Theory(DisplayName = "VHS short-field diagnostics require an upstream prevfield")]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortFieldDiagnosticsMatchPreviousFieldCondition(bool hasPreviousField)
    {
        using DecodeSession session = CreateSession(hasPreviousField ? "with-previous" : "initial");
        if (hasPreviousField)
        {
            TbcFieldDecodeState state = session.TbcFieldDecoder.CaptureState();
            session.TbcFieldDecoder.RestoreStateForRetry(state with
            {
                PreviousFirstHSyncLocation = 100.0,
                PreviousFirstHSyncReadLocation = 0
            });
        }

        var error = new StringWriter();
        session.RuntimeReporter = new DecodeRuntimeReporter(TextWriter.Null, error);

        TbcFieldDecodeRecoveryException exception = InvokeInsufficientDataCheck(session.TbcFieldDecoder);

        Assert.Equal(TbcFieldDecodeRecoveryKind.InsufficientData, exception.Kind);
        if (!hasPreviousField)
        {
            Assert.Empty(error.ToString());
            Assert.False(File.Exists(session.OutputBase + ".log"));
            return;
        }

        Assert.Equal(
            DetailsMessage + Environment.NewLine + SkipMessage + Environment.NewLine,
            error.ToString());
        string log = File.ReadAllText(session.OutputBase + ".log");
        Assert.Contains("INFO - " + DetailsMessage, log, StringComparison.Ordinal);
        Assert.Contains("INFO - " + SkipMessage, log, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private DecodeSession CreateSession(string outputName)
    {
        string outputBase = Path.Combine(_tempDirectory, outputName);
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            "--pal",
            "--threads",
            "0",
            "--no_resample",
            "input.s16",
            outputBase
        ]);
        return DecodeSessionFactory.Create(command);
    }

    private static TbcFieldDecodeRecoveryException InvokeInsufficientDataCheck(
        TbcFieldDecodePipeline pipeline)
    {
        MethodInfo? method = typeof(TbcFieldDecodePipeline).GetMethod(
            "ThrowIfInsufficientFieldData",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var span = new RfDecodedSpan(
            StartSample: 0,
            Input: new double[1_000],
            Video: [],
            DemodRaw: []);
        var pulses = new Pulse[] { new(900, 10) };

        TargetInvocationException wrapper = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(pipeline, [span, pulses, 100.0, 10.0, 100]));
        return Assert.IsType<TbcFieldDecodeRecoveryException>(wrapper.InnerException);
    }
}
