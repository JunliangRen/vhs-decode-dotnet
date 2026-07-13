namespace VHSDecode.Core.CommandLine;

public static class Ire0AdjustParser
{
    private static readonly HashSet<string> SupportedValues = new(StringComparer.Ordinal)
    {
        "hsync",
        "backporch"
    };

    public static bool IsValidRaw(string value)
    {
        return value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .All(part => SupportedValues.Contains(part.ToLowerInvariant()));
    }

    public static string Normalize(string value)
    {
        if (!IsValidRaw(value))
        {
            throw new ArgumentException("Allowed values: hsync, backporch");
        }

        return string.Join(',', value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.ToLowerInvariant()));
    }
}
