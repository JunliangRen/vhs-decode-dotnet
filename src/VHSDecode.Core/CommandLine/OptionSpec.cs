namespace VHSDecode.Core.CommandLine;

public sealed class OptionSpec
{
    public required string Destination { get; init; }

    public required OptionValueKind ValueKind { get; init; }

    public required OptionArity Arity { get; init; }

    public required string[] Names { get; init; }

    public object? DefaultValue { get; init; }

    public object? ConstValue { get; init; }

    public object? PythonDefaultValue { get; init; }

    public object? PythonConstValue { get; init; }

    public string[]? Choices { get; init; }

    public Func<string, bool>? IsValidOptionalValue { get; init; }

    public string[]? OptionalValueDisambiguationNames { get; init; }

    public Func<string, string>? NormalizeString { get; init; }

    public Func<string, double>? ParseFrequencyMHz { get; init; }

    public Func<string, string?>? ValidationError { get; init; }

    public string? ParseErrorTypeName { get; init; }

    public bool Hidden { get; init; }

    public bool IncludeInPythonNamespace { get; init; } = true;

    public string PrimaryName => Names[0];

    public string DisplayName => string.Join('/', Names);

    public object? ConvertValue(string raw)
    {
        if (ValidationError?.Invoke(raw) is { } validationError)
        {
            throw new ArgumentException(validationError);
        }

        object? value = ValueKind switch
        {
            OptionValueKind.Boolean => bool.Parse(raw),
            OptionValueKind.String => NormalizeString is null ? raw : NormalizeString(raw),
            OptionValueKind.Integer => PythonNumericParser.ParseInteger(raw),
            OptionValueKind.Double => PythonNumericParser.ParseFloat(raw),
            OptionValueKind.FrequencyMHz => ParseFrequencyMHz is null
                ? FrequencyParser.ParseMHz(raw)
                : ParseFrequencyMHz(raw),
            _ => throw new InvalidOperationException($"Unsupported option type {ValueKind}")
        };

        if (Choices is { Length: > 0 } && value is string text)
        {
            bool allowed = Choices.Any(choice => string.Equals(choice, text, StringComparison.Ordinal));
            if (!allowed)
            {
                throw new ArgumentException(
                    $"invalid choice: '{text}' (choose from {string.Join(", ", Choices)})");
            }
        }

        return value;
    }
}
