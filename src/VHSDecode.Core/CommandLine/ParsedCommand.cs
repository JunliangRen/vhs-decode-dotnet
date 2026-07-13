namespace VHSDecode.Core.CommandLine;

public sealed class ParsedCommand
{
    public ParsedCommand(
        DecodeCommandSpec spec,
        Dictionary<string, object?> values,
        List<string> positionals,
        string? programName = null,
        IReadOnlyDictionary<string, ParsedOptionSource>? optionSources = null)
    {
        Spec = spec;
        Values = values;
        Positionals = positionals;
        ProgramName = string.IsNullOrWhiteSpace(programName)
            ? spec.Aliases.FirstOrDefault() ?? spec.Name
            : programName;
        OptionSources = optionSources ?? values.Keys.ToDictionary(
            destination => destination,
            _ => ParsedOptionSource.Default);
    }

    public DecodeCommandSpec Spec { get; }

    public IReadOnlyDictionary<string, object?> Values { get; }

    public IReadOnlyList<string> Positionals { get; }

    public string ProgramName { get; }

    public IReadOnlyDictionary<string, ParsedOptionSource> OptionSources { get; }

    public string InputFile => Positionals.Count > 0 ? Positionals[0] : string.Empty;

    public string OutputBase => Positionals.Count > 1 ? Positionals[1] : string.Empty;

    public T Get<T>(string destination)
    {
        if (!Values.TryGetValue(destination, out object? value))
        {
            throw new KeyNotFoundException($"No parsed value named '{destination}'.");
        }

        return value is null ? default! : (T)value;
    }

    public ParsedOptionSource GetSource(string destination)
        => OptionSources.TryGetValue(destination, out ParsedOptionSource source)
            ? source
            : ParsedOptionSource.Default;
}
