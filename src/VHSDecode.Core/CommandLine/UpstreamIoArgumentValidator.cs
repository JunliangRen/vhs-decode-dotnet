namespace VHSDecode.Core.CommandLine;

public static class UpstreamIoArgumentValidator
{
    public static bool ValidateInput(string inputFile, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(inputFile);
        ArgumentNullException.ThrowIfNull(output);

        if (inputFile == "-")
        {
            return true;
        }

        if (inputFile.Length == 0)
        {
            output.WriteLine("WARN: input file not specified");
            return false;
        }

        if (!File.Exists(inputFile))
        {
            output.WriteLine($"WARN: input file '{inputFile}' not found");
            return false;
        }

        return true;
    }

    public static bool ValidateOutput(string outputFile, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(outputFile);
        ArgumentNullException.ThrowIfNull(output);

        string directory = Path.GetDirectoryName(outputFile) ?? string.Empty;
        if (directory.Length == 0)
        {
            directory = ".";
        }

        if (!Directory.Exists(directory))
        {
            output.WriteLine($"Error: output file directory '{directory}' is not writable");
            return false;
        }

        try
        {
            using (new FileStream(outputFile, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
            }

            if (new FileInfo(outputFile).Length == 0)
            {
                File.Delete(outputFile);
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or FileNotFoundException)
        {
            output.WriteLine($"WARN: output file '{outputFile}' not found");
            return false;
        }
    }
}
