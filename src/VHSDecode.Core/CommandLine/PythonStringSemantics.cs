namespace VHSDecode.Core.CommandLine;

internal static class PythonStringSemantics
{
    public static string TrimWhitespace(string text)
    {
        (int start, int end) = TrimWhitespaceBounds(text);
        return text[start..end];
    }

    public static (int Start, int End) TrimWhitespaceBounds(string text)
    {
        int start = 0;
        while (start < text.Length && IsWhitespace(text[start]))
        {
            start++;
        }

        int end = text.Length;
        while (end > start && IsWhitespace(text[end - 1]))
        {
            end--;
        }

        return (start, end);
    }

    private static bool IsWhitespace(char value)
        => value is >= '\u0009' and <= '\u000d'
            or >= '\u001c' and <= '\u0020'
            or '\u0085'
            or '\u00a0'
            or '\u1680'
            or >= '\u2000' and <= '\u200a'
            or '\u2028'
            or '\u2029'
            or '\u202f'
            or '\u205f'
            or '\u3000';
}
