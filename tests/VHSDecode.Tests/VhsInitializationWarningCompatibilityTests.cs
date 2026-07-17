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

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private void AssertWarning(string expectedWarning, params string[] options)
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
        Assert.Contains(expectedWarning, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "WARNING - " + expectedWarning,
            File.ReadAllText(outputBase + ".log"),
            StringComparison.Ordinal);
    }
}
