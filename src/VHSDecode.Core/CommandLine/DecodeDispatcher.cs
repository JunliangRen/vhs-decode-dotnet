namespace VHSDecode.Core.CommandLine;

public static class DecodeDispatcher
{
    private static readonly IReadOnlyDictionary<string, string> StandaloneCommands =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vhs-decode"] = "vhs",
            ["cvbs-decode"] = "cvbs",
            ["ld-decode"] = "ld",
            ["hifi-decode"] = "hifi"
        };

    public static string[] UpstreamTopLevelCommands { get; } =
    [
        "vhs",
        "cvbs",
        "ld",
        "hifi",
        "filter-tune",
        "decode-launcher"
    ];

    public static bool TryDispatch(IReadOnlyList<string> args, out DecodeCommandSpec? spec, out string[] remaining)
    {
        spec = null;
        remaining = [];
        if (args.Count == 0)
        {
            return false;
        }

        string requested = args[0].ToLowerInvariant();
        spec = CliSpecs.AllCommands.FirstOrDefault(command =>
            command.Name == requested || command.Aliases.Any(alias => alias == requested));

        if (spec is null)
        {
            return false;
        }

        remaining = args.Skip(1).ToArray();
        return true;
    }

    public static string[] NormalizeInvocation(IReadOnlyList<string> args, string? executableName)
    {
        string name = Path.GetFileNameWithoutExtension(executableName ?? string.Empty);
        if (!StandaloneCommands.TryGetValue(name, out string? command))
        {
            return args.ToArray();
        }

        var normalized = new string[args.Count + 1];
        normalized[0] = command;
        for (int i = 0; i < args.Count; i++)
        {
            normalized[i + 1] = args[i];
        }

        return normalized;
    }

    public static string InvocationProgramName(string? executableName)
    {
        string name = Path.GetFileNameWithoutExtension(executableName ?? string.Empty);
        return StandaloneCommands.ContainsKey(name) ? name : "decode.py";
    }
}
