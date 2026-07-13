using VHSDecode.Core.CommandLine;

namespace VHSDecode.Core.Decode;

public static class DecodeOutputPreflight
{
    private static readonly string[] VhsConflictExtensions = [".tbc", "_chroma.tbc", ".log", ".tbc.json"];
    private static readonly string[] CvbsConflictExtensions = [".tbc", ".log", ".tbc.json"];

    public static void Validate(ParsedCommand command)
    {
        if (command.Positionals.Count < 2)
        {
            return;
        }

        ValidateInputFile(command);
        ValidateExistingOutputs(command);
        ValidateOutputDirectory(command);
        ValidateWriteTestLdf(command);
    }

    public static IReadOnlyList<string> FindExistingOutputConflicts(ParsedCommand command)
    {
        if (command.Values.TryGetValue("overwrite", out object? overwriteValue)
            && overwriteValue is bool overwrite
            && overwrite)
        {
            return [];
        }

        string[] extensions = command.Spec.Name switch
        {
            "vhs" => VhsConflictExtensions,
            "cvbs" => CvbsConflictExtensions,
            _ => []
        };

        if (extensions.Length == 0)
        {
            return [];
        }

        return extensions
            .Select(extension => command.OutputBase + extension)
            .Where(File.Exists)
            .ToArray();
    }

    private static void ValidateInputFile(ParsedCommand command)
    {
        if (command.InputFile == "-")
        {
            return;
        }

        if (!File.Exists(command.InputFile))
        {
            throw new ArgumentException($"ERROR: input file '{command.InputFile}' not found");
        }
    }

    private static void ValidateExistingOutputs(ParsedCommand command)
    {
        IReadOnlyList<string> conflicts = FindExistingOutputConflicts(command);
        if (conflicts.Count == 0)
        {
            return;
        }

        throw new ArgumentException(
            "Existing decode files found, remove them or run command with --overwrite" +
            Environment.NewLine +
            string.Join(Environment.NewLine, conflicts.Select(path => "\t " + path)));
    }

    private static void ValidateOutputDirectory(ParsedCommand command)
    {
        if (command.Spec.Name is not ("vhs" or "cvbs"))
        {
            return;
        }

        string? outputDirectory = Path.GetDirectoryName(command.OutputBase);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = ".";
        }

        if (!Directory.Exists(outputDirectory))
        {
            throw new ArgumentException($"ERROR: output file '{command.OutputBase}' is not writable");
        }

        string probePath = Path.Combine(outputDirectory, ".vhsdecode-dotnet-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"ERROR: output file '{command.OutputBase}' is not writable", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateWriteTestLdf(ParsedCommand command)
    {
        if (command.Spec.Name != "ld")
        {
            return;
        }

        string? writeTestLdf = command.Get<string>("write_test_ldf");
        if (string.IsNullOrWhiteSpace(writeTestLdf))
        {
            return;
        }

        string inputPath = Path.GetFullPath(command.InputFile);
        string outputPath = Path.GetFullPath(writeTestLdf);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "ERROR: --write-test-ldf output file cannot be the same as input file" +
                Environment.NewLine +
                $"Input:  {command.InputFile}" +
                Environment.NewLine +
                $"Output: {writeTestLdf}");
        }
    }
}
