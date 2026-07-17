using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Formats;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsParamsFileDiagnosticCompatibilityTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "vhsdecode-dotnet-tests-" + Guid.NewGuid().ToString("N"));

    public VhsParamsFileDiagnosticCompatibilityTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact(DisplayName = "VHS params-file diagnostics match v0.4.0 text and order")]
    public void ParamsFileDiagnosticsMatchTextAndOrder()
    {
        string paramsFile = WriteParamsFile("diagnostics.json", """
            {
              "sys_params": {
                "outlinelen": 999,
                "hz_ire": 12345.0,
                "not_a_sys_param": 1
              },
              "rf_params": {
                "video_lpf_freq": 3000000.0,
                "not_a_rf_param": true
              }
            }
            """);
        string inputPath = Path.Combine(_tempDirectory, "input.s16");
        string outputBase = Path.Combine(_tempDirectory, "output");
        File.WriteAllBytes(inputPath, []);
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, [
            "--pal",
            "--params_file",
            paramsFile,
            "--length",
            "0",
            "--threads",
            "0",
            inputPath,
            outputBase
        ]);
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = new DecodeRunner().Run(
            command,
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        const string unknownSys =
            "Item not_a_sys_param in params json not in group sys_params. Not changed!";
        const string changedSys =
            "Changed {'outlinelen': 999, 'hz_ire': 12345.0} in sys_params";
        const string unknownRf =
            "Item not_a_rf_param in params json not in group rf_params. Not changed!";
        const string changedRf =
            "Changed {'video_lpf_freq': 3000000.0} in rf_params";
        string errorText = error.ToString();
        AssertOrdered(errorText, unknownSys, unknownRf, "Completed without handling any frames.");
        Assert.DoesNotContain(changedSys, errorText, StringComparison.Ordinal);
        Assert.DoesNotContain(changedRf, errorText, StringComparison.Ordinal);

        string log = File.ReadAllText(outputBase + ".log");
        AssertOrdered(
            log,
            "INFO - " + unknownSys,
            "DEBUG - " + changedSys,
            "INFO - " + unknownRf,
            "DEBUG - " + changedRf,
            "DEBUG - Sys Parameters:");
    }

    [Fact(DisplayName = "VHS params-file changed values use Python v0.4.0 repr")]
    public void ChangedValuesUsePythonRepresentation()
    {
        string paramsFile = WriteParamsFile("python-repr.json", """
            {
              "rf_params": {
                "video_custom_luma_filters": [
                  {
                    "type": "highshelf",
                    "freq": 1000000.0,
                    "gain": 1.5,
                    "q": null,
                    "enabled": true
                  }
                ]
              }
            }
            """);
        FormatParameterSet parameters = FormatCatalog.Default.GetTapeParameters(
            "PAL",
            "SVHS",
            "sp");

        VhsParamsFileOverrideResult result = VhsParamsFileOverride.ApplyWithDiagnostics(
            parameters,
            paramsFile);

        DecodeInitializationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DEBUG", diagnostic.Level);
        Assert.Equal(
            "Changed {'video_custom_luma_filters': [{'type': 'highshelf', "
            + "'freq': 1000000.0, 'gain': 1.5, 'q': None, 'enabled': True}]} in rf_params",
            diagnostic.Message);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private string WriteParamsFile(string name, string contents)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, contents);
        return path;
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
