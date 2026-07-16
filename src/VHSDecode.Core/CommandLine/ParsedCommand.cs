using System.Numerics;

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

        if (value is null)
        {
            return default!;
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        if (typeof(T) == typeof(BigInteger) && value is int intValue)
        {
            return (T)(object)new BigInteger(intValue);
        }

        if (value is BigInteger integer)
        {
            if (typeof(T) == typeof(int))
            {
                return (T)(object)checked((int)integer);
            }

            if (typeof(T) == typeof(long))
            {
                return (T)(object)checked((long)integer);
            }

            if (typeof(T) == typeof(double))
            {
                return (T)(object)(double)integer;
            }

            throw new InvalidCastException(
                $"Cannot convert parsed Python integer to {typeof(T).Name}.");
        }

        return (T)value;
    }

    public ParsedOptionSource GetSource(string destination)
        => OptionSources.TryGetValue(destination, out ParsedOptionSource source)
            ? source
            : ParsedOptionSource.Default;
}
