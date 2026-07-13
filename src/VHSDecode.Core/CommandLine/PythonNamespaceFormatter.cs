using System.Globalization;
using System.Text;

namespace VHSDecode.Core.CommandLine;

internal static class PythonNamespaceFormatter
{
    public static string Format(ParsedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var attributes = new List<string>
        {
            $"infile={FormatString(command.InputFile)}",
            $"outfile={FormatString(command.OutputBase)}"
        };
        string? suppressedNoAgc = null;

        foreach (OptionSpec option in command.Spec.Options)
        {
            if (option.Destination is "help" or "version")
            {
                continue;
            }

            ParsedOptionSource source = command.GetSource(option.Destination);
            if (command.Spec.Name is "vhs" or "cvbs"
                && option.Destination == "noAGC")
            {
                if (source != ParsedOptionSource.Default)
                {
                    suppressedNoAgc = $"noAGC={FormatValue(command.Values[option.Destination])}";
                }

                continue;
            }

            object? value = source switch
            {
                ParsedOptionSource.Default => option.PythonDefaultValue,
                ParsedOptionSource.Constant => option.PythonConstValue,
                _ => command.Values[option.Destination]
            };
            string representation = option.Destination == "params_file" && value is string path
                ? FormatTextFile(path)
                : FormatValue(value);
            attributes.Add($"{option.Destination}={representation}");
        }

        if (suppressedNoAgc is not null)
        {
            attributes.Add(suppressedNoAgc);
        }

        return $"Namespace({string.Join(", ", attributes)})";
    }

    internal static string FormatValue(object? value)
        => value switch
        {
            null => "None",
            bool boolean => boolean ? "True" : "False",
            string text => FormatString(text),
            double number => FormatFloat(number),
            float number => FormatFloat(number),
            sbyte or byte or short or ushort or int or uint or long or ulong
                => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ => FormatString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };

    internal static string FormatString(string value)
    {
        char quote = value.Contains('\'')
            && !value.Contains('"')
            ? '"'
            : '\'';
        var result = new StringBuilder(value.Length + 2);
        result.Append(quote);

        for (int index = 0; index < value.Length;)
        {
            char current = value[index];
            if (current == quote || current == '\\')
            {
                result.Append('\\').Append(current);
                index++;
                continue;
            }

            string? escape = current switch
            {
                '\a' => "\\a",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                _ => null
            };
            if (escape is not null)
            {
                result.Append(escape);
                index++;
                continue;
            }

            if (!Rune.TryGetRuneAt(value, index, out Rune rune))
            {
                AppendEscapedCodePoint(result, current);
                index++;
                continue;
            }

            if (IsPythonPrintable(rune))
            {
                result.Append(rune.ToString());
            }
            else
            {
                AppendEscapedCodePoint(result, rune.Value);
            }

            index += rune.Utf16SequenceLength;
        }

        return result.Append(quote).ToString();
    }

    private static string FormatFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        if (value == 0.0)
        {
            return BitConverter.DoubleToInt64Bits(value) < 0 ? "-0.0" : "0.0";
        }

        string formatted = value.ToString("R", CultureInfo.InvariantCulture)
            .Replace('E', 'e');
        if (formatted.Contains('e'))
        {
            return formatted;
        }

        double absolute = Math.Abs(value);
        if (absolute >= 1e16)
        {
            return FixedToScientific(formatted);
        }

        return formatted.Contains('.')
            ? formatted
            : formatted + ".0";
    }

    private static string FixedToScientific(string value)
    {
        string sign = value.StartsWith('-') ? "-" : string.Empty;
        string unsigned = sign.Length == 0 ? value : value[1..];
        int decimalIndex = unsigned.IndexOf('.');
        string integer = decimalIndex < 0 ? unsigned : unsigned[..decimalIndex];
        string fraction = decimalIndex < 0 ? string.Empty : unsigned[(decimalIndex + 1)..];
        string digits = (integer + fraction).TrimEnd('0');
        int exponent = integer.Length - 1;
        string mantissa = digits.Length == 1
            ? digits
            : digits[0] + "." + digits[1..];
        return $"{sign}{mantissa}e+{exponent}";
    }

    private static string FormatTextFile(string path)
    {
        bool standardInput = path == "-";
        string name = standardInput ? "<stdin>" : path;
        string encoding = GetPythonTextEncodingName(standardInput);
        return $"<_io.TextIOWrapper name={FormatString(name)} mode='r' encoding={FormatString(encoding)}>";
    }

    private static string GetPythonTextEncodingName(bool standardInput)
    {
        int codePage = standardInput
            ? Console.InputEncoding.CodePage
            : OperatingSystem.IsWindows()
                ? CultureInfo.CurrentCulture.TextInfo.ANSICodePage
                : Encoding.Default.CodePage;
        return codePage switch
        {
            65001 => "utf-8",
            936 when standardInput => "gbk",
            _ => $"cp{codePage}"
        };
    }

    private static bool IsPythonPrintable(Rune rune)
    {
        if (rune.Value == 0x20)
        {
            return true;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is not (UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.SpaceSeparator
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator);
    }

    private static void AppendEscapedCodePoint(StringBuilder result, int value)
    {
        if (value <= byte.MaxValue)
        {
            result.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        else if (value <= ushort.MaxValue)
        {
            result.Append("\\u").Append(value.ToString("x4", CultureInfo.InvariantCulture));
        }
        else
        {
            result.Append("\\U").Append(value.ToString("x8", CultureInfo.InvariantCulture));
        }
    }
}
