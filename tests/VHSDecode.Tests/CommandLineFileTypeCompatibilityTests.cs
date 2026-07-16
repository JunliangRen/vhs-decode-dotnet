using VHSDecode.Core.CommandLine;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CommandLineFileTypeCompatibilityTests
{
    [Fact(DisplayName = "params_file open errors match argparse FileType")]
    public void ParamsFileOpenErrorsMatchArgparseFileType()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-dotnet-filetype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            int directoryError = OperatingSystem.IsWindows() ? 13 : 21;
            string directoryDescription = OperatingSystem.IsWindows() ? "Permission denied" : "Is a directory";
            AssertError(
                tempDirectory,
                $"argument --params_file: can't open '{tempDirectory}': "
                    + $"[Errno {directoryError}] {directoryDescription}: "
                    + PythonNamespaceFormatter.FormatString(tempDirectory));

            string missing = Path.Combine(tempDirectory, "can't-open.json");
            AssertError(
                missing,
                $"argument --params_file: can't open '{missing}': [Errno 2] No such file or directory: "
                    + PythonNamespaceFormatter.FormatString(missing));

            AssertError(
                string.Empty,
                "argument --params_file: can't open '': [Errno 2] No such file or directory: ''");
        }
        finally
        {
            Directory.Delete(tempDirectory);
        }
    }

    [Fact(DisplayName = "params_file accepts Windows character devices like argparse FileType")]
    public void ParamsFileAcceptsWindowsCharacterDevicesLikeArgparseFileType()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ParsedCommand command = Parse("--params_file", "NUL");
        Assert.Equal("NUL", command.Get<string>("params_file"));
    }

    private static ParsedCommand Parse(params string[] arguments)
        => new CommandLineParser().Parse(CliSpecs.Vhs, arguments);

    private static void AssertError(string path, string expected)
    {
        CommandLineParseException exception = Assert.Throws<CommandLineParseException>(
            () => Parse("--params_file", path));
        Assert.Equal(expected, exception.Message);
    }
}
