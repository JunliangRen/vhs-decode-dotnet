using System.Globalization;
using System.Numerics;
using System.Text;

namespace VHSDecode.Core.CommandLine;

internal static class PythonNumericParser
{
    private const ulong PositiveQuietNaNBits = 0x7ff8_0000_0000_0000UL;
    private const ulong NegativeQuietNaNBits = 0xfff8_0000_0000_0000UL;

    public static object ParseInteger(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        (int start, int end) = PythonStringSemantics.TrimWhitespaceBounds(text);
        var normalized = new StringBuilder(end - start);
        int index = start;
        if (index < end && text[index] is '+' or '-')
        {
            normalized.Append(text[index++]);
        }

        if (!AppendDigitPart(text, ref index, end, normalized) || index != end)
        {
            throw new FormatException();
        }

        BigInteger value = BigInteger.Parse(
            normalized.ToString(),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);
        if (value >= int.MinValue && value <= int.MaxValue)
        {
            return (int)value;
        }

        return value;
    }

    public static double DivideIntegerByPowerOfTen(BigInteger value, int decimalPlaces)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);

        string scaledValue = string.Concat(
            value.ToString(CultureInfo.InvariantCulture),
            "e-",
            decimalPlaces.ToString(CultureInfo.InvariantCulture));
        double result = double.Parse(
            scaledValue,
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
        if (double.IsInfinity(result))
        {
            throw new OverflowException("integer division result too large for a float");
        }

        return result;
    }

    public static double ParseFloat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        (int start, int end) = PythonStringSemantics.TrimWhitespaceBounds(text);
        int index = start;
        bool negative = false;
        var normalized = new StringBuilder(end - start);
        if (index < end && text[index] is '+' or '-')
        {
            negative = text[index] == '-';
            normalized.Append(text[index++]);
        }

        string special = text[index..end];
        if (special.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            ulong bits = negative ? NegativeQuietNaNBits : PositiveQuietNaNBits;
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        if (special.Equals("inf", StringComparison.OrdinalIgnoreCase)
            || special.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        bool integerDigits = AppendDigitPart(text, ref index, end, normalized);
        bool fractionDigits = false;
        if (index < end && text[index] == '.')
        {
            normalized.Append(text[index++]);
            fractionDigits = AppendDigitPart(text, ref index, end, normalized);
        }

        if (!integerDigits && !fractionDigits)
        {
            throw new FormatException();
        }

        if (index < end && text[index] is 'e' or 'E')
        {
            normalized.Append(text[index++]);
            if (index < end && text[index] is '+' or '-')
            {
                normalized.Append(text[index++]);
            }

            if (!AppendDigitPart(text, ref index, end, normalized))
            {
                throw new FormatException();
            }
        }

        if (index != end)
        {
            throw new FormatException();
        }

        return double.Parse(normalized.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static bool LooksLikeArgparseNegativeNumber(string text)
    {
        if (text.Length < 2 || text[0] != '-')
        {
            return false;
        }

        int digitIndex = text[1] == '.' ? 2 : 1;
        return digitIndex < text.Length && DecimalDigitValue(text, digitIndex) >= 0;
    }

    private static bool AppendDigitPart(
        string text,
        ref int index,
        int end,
        StringBuilder normalized)
    {
        if (!AppendDecimalDigit(text, ref index, end, normalized))
        {
            return false;
        }

        while (index < end)
        {
            if (text[index] == '_')
            {
                int next = index + 1;
                if (next >= end || DecimalDigitValue(text, next) < 0)
                {
                    break;
                }

                index = next;
            }

            if (!AppendDecimalDigit(text, ref index, end, normalized))
            {
                break;
            }
        }

        return true;
    }

    private static bool AppendDecimalDigit(
        string text,
        ref int index,
        int end,
        StringBuilder normalized)
    {
        if (index >= end)
        {
            return false;
        }

        int value = DecimalDigitValue(text, index);
        if (value < 0)
        {
            return false;
        }

        normalized.Append((char)('0' + value));
        index += char.IsHighSurrogate(text[index])
            && index + 1 < end
            && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
        return true;
    }

    private static int DecimalDigitValue(string text, int index)
    {
        try
        {
            return CharUnicodeInfo.GetDecimalDigitValue(text, index);
        }
        catch (ArgumentException)
        {
            return -1;
        }
    }

}
