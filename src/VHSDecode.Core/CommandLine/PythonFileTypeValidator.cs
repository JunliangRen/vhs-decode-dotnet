namespace VHSDecode.Core.CommandLine;

internal static class PythonFileTypeValidator
{
    public static string? ValidateReadableTextFile(string optionName, string path)
    {
        if (path == "-")
        {
            return null;
        }

        try
        {
            if (path.Length == 0
                || OperatingSystem.IsWindows() && path.Any(character => character < ' '))
            {
                return Error(optionName, path, path.Length == 0 ? 2 : 22);
            }

            if (Directory.Exists(path))
            {
                return Error(optionName, path, OperatingSystem.IsWindows() ? 13 : 21);
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (FileNotFoundException)
        {
            return Error(optionName, path, 2);
        }
        catch (DirectoryNotFoundException)
        {
            return Error(optionName, path, 2);
        }
        catch (UnauthorizedAccessException)
        {
            return Error(optionName, path, 13);
        }
        catch (PathTooLongException)
        {
            return Error(optionName, path, OperatingSystem.IsWindows() ? 2 : 36);
        }
        catch (IOException)
        {
            return Error(optionName, path, 13);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Error(optionName, path, 22);
        }
    }

    private static string Error(string optionName, string path, int errorNumber)
    {
        string description = errorNumber switch
        {
            2 => "No such file or directory",
            13 => "Permission denied",
            21 => "Is a directory",
            22 => "Invalid argument",
            36 => "File name too long",
            _ => throw new ArgumentOutOfRangeException(nameof(errorNumber))
        };
        return $"argument {optionName}: can't open '{path}': "
            + $"[Errno {errorNumber}] {description}: {PythonNamespaceFormatter.FormatString(path)}";
    }
}
