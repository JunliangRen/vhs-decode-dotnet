using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsInitializationWarningCompatibilityTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public VhsInitializationWarningCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact(DisplayName = "VHS invalid level divisor emits the exact v0.4.0 warning")]
    public void InvalidLevelDivisorEmitsExactWarning()
    {
        AssertWarning(
            "Invalid level detect divisor value 11, using default.",
            "--pal",
            "-f",
            "40",
            "--no_resample",
            "--level_detect_divisor",
            "11");
    }

    [Fact(DisplayName = "VHS high level divisor emits the exact v0.4.0 limit warning")]
    public void HighLevelDivisorEmitsExactWarning()
    {
        AssertWarning(
            "Level detect divisor too high (10) for input frequency (28.636363) mhz. Limiting to 7",
            "--pal",
            "-f",
            "28.636363",
            "--no_resample",
            "--level_detect_divisor",
            "10");
    }

    [Fact(DisplayName = "VHS missing FM audio frequencies emit the exact v0.4.0 warning")]
    public void MissingFmAudioFrequenciesEmitExactWarning()
    {
        AssertWarning(
            "Audio frequencies are not specified for this format, audio fm notch filters not enabled!",
            "--pal",
            "-f",
            "40",
            "--no_resample",
            "--tape_format",
            "EIAJ",
            "--fm_audio_notch",
            "10");
    }

    [Fact(DisplayName = "VHS unsupported PAL format emits parameter and field-class fallback diagnostics")]
    public void UnsupportedPalFormatEmitsExactFallbackDiagnostics()
    {
        RunDecode(
            out string error,
            out string log,
            "--pal",
            "-f",
            "40",
            "--no_resample",
            "--tape_format",
            "BETAMAX_HIFI");

        const string parameterWarning = "Tape format \"BETAMAX_HIFI\" not supported for PAL yet";
        const string fieldClassInfo = "Tape format unimplemented for PAL, using VHS field class.";
        AssertOrdered(
            error,
            parameterWarning,
            fieldClassInfo,
            "Completed without handling any frames.");
        AssertOrdered(
            log,
            "WARNING - " + parameterWarning,
            "INFO - " + fieldClassInfo,
            "DEBUG - Sys Parameters:");
    }

    [Fact(DisplayName = "VHS initialization warnings retain v0.4.0 construction order")]
    public void InitializationWarningsRetainConstructionOrder()
    {
        RunDecode(
            out _,
            out string log,
            "--pal",
            "-f",
            "40",
            "--no_resample",
            "--tape_format",
            "EIAJ",
            "--fm_audio_notch",
            "10",
            "--level_detect_divisor",
            "11");

        AssertOrdered(
            log,
            "WARNING - Audio frequencies are not specified for this format, audio fm notch filters not enabled!",
            "WARNING - Invalid level detect divisor value 11, using default.",
            "DEBUG - Sys Parameters:");
    }

    [Theory(DisplayName = "VHS field-class fallback diagnostics match v0.4.0 format dispatch")]
    [InlineData("PAL", "BETAMAX_HIFI", "Tape format unimplemented for PAL, using VHS field class.")]
    [InlineData("PAL", "VHD", "Tape format unimplemented for PAL, using VHS field class.")]
    [InlineData("PAL", "VIDEO2000", null)]
    [InlineData("PAL", "QUADRUPLEX", null)]
    [InlineData("NTSC", "QUADRUPLEX", "Tape format unimplemented for NTSC, using VHS field class.")]
    [InlineData("NTSC", "VCR", "Tape format unimplemented for NTSC, using VHS field class.")]
    [InlineData("NTSC", "BETAMAX_HIFI", null)]
    [InlineData("NTSC", "VHD", null)]
    public void FieldClassFallbackDiagnosticsMatchDispatch(
        string system,
        string tapeFormat,
        string? expected)
    {
        Assert.Equal(expected, DecodeRunner.GetVhsFieldClassFallbackMessage(system, tapeFormat));
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private void AssertWarning(string expectedWarning, params string[] options)
    {
        RunDecode(out string error, out string log, options);

        Assert.Contains(expectedWarning, error, StringComparison.Ordinal);
        Assert.Contains("WARNING - " + expectedWarning, log, StringComparison.Ordinal);
        AssertOrdered(log, "WARNING - " + expectedWarning, "DEBUG - Sys Parameters:");
    }

    private void RunDecode(out string errorText, out string logText, params string[] options)
    {
        string inputPath = Path.Combine(
            _tempDirectory,
            "input-" + Guid.NewGuid().ToString("N") + ".s16");
        string outputBase = Path.Combine(
            _tempDirectory,
            "output-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(inputPath, []);
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            .. options,
            "--length",
            "0",
            "--threads",
            "0",
            inputPath,
            outputBase
        ]);
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = new DecodeRunner().Run(command, output, error);

        Assert.Equal(0, exitCode);
        errorText = error.ToString();
        logText = File.ReadAllText(outputBase + ".log");
    }

    private static void AssertOrdered(string text, params string[] values)
    {
        int previousIndex = -1;
        foreach (string value in values)
        {
            int index = text.IndexOf(value, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{value}' after index {previousIndex}.\n{text}");
            previousIndex = index;
        }
    }
}
