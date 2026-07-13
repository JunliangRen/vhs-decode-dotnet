namespace VHSDecode.Core.CommandLine;

public sealed class DecodeCommandSpec
{
    private readonly Dictionary<string, OptionSpec> _optionsByName;

    public DecodeCommandSpec(
        string name,
        string description,
        IEnumerable<string> aliases,
        IEnumerable<OptionSpec> options,
        int minimumPositionals,
        int maximumPositionals)
    {
        Name = name;
        Description = description;
        Aliases = aliases.ToArray();
        Options = options.ToArray();
        MinimumPositionals = minimumPositionals;
        MaximumPositionals = maximumPositionals;
        _optionsByName = Options
            .SelectMany(option => option.Names.Select(name => (name, option)))
            .ToDictionary(item => item.name, item => item.option, StringComparer.Ordinal);
    }

    public string Name { get; }

    public string Description { get; }

    public string[] Aliases { get; }

    public OptionSpec[] Options { get; }

    public int MinimumPositionals { get; }

    public int MaximumPositionals { get; }

    public bool TryGetOption(string name, out OptionSpec option) => _optionsByName.TryGetValue(name, out option!);
}
