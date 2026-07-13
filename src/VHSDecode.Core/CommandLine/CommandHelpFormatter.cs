using System.Collections.Concurrent;
using System.Reflection;

namespace VHSDecode.Core.CommandLine;

public static class CommandHelpFormatter
{
    private static readonly ConcurrentDictionary<string, string> SnapshotCache = new(StringComparer.Ordinal);

    public static string Format(DecodeCommandSpec spec, string? programName = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        string fileName = IsFacadeProgram(programName)
            ? $"decode-{spec.Name}.txt"
            : $"{spec.Aliases[0]}.txt";
        return SnapshotCache.GetOrAdd(fileName, LoadSnapshot);
    }

    public static string FormatUsage(DecodeCommandSpec spec, string? programName = null)
    {
        string help = Format(spec, programName);
        string separator = Environment.NewLine + Environment.NewLine;
        int end = help.IndexOf(separator, StringComparison.Ordinal);
        return end < 0 ? help : help[..(end + Environment.NewLine.Length)];
    }

    private static bool IsFacadeProgram(string? programName)
    {
        string fileName = Path.GetFileName(programName ?? string.Empty);
        return fileName.Equals("decode.py", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(fileName).Equals("decode", StringComparison.OrdinalIgnoreCase);
    }

    private static string LoadSnapshot(string fileName)
    {
        Assembly assembly = typeof(CommandHelpFormatter).Assembly;
        string suffix = $".CommandLine.Help.{fileName}";
        string resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(suffix, StringComparison.Ordinal));

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded help snapshot '{fileName}' was not found.");
        using var reader = new StreamReader(stream);
        string snapshot = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        return snapshot.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}
