namespace VHSDecode.Core.CommandLine;

public static class FrequencyParser
{
    private static readonly (string Suffix, double Multiplier)[] Suffixes =
    [
        ("ghz", 1.0e9),
        ("mhz", 1.0e6),
        ("khz", 1.0e3),
        ("hz", 1.0),
        ("fscpal", (283.75 * 15625) + 25),
        ("fsc", 315.0e6 / 88.0)
    ];

    public const double DddMHz = 40.0;
    public const double CxAdcMHz = (8.0 * 315.0) / 88.0;
    public const double CxAdcTenBitMHz = CxAdcMHz / 2.0;

    public static double ParseMHz(string text)
    {
        string valueText = text;
        double multiplier = 1.0e6;
        foreach ((string suffix, double mult) in Suffixes)
        {
            if (valueText.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                multiplier = mult;
                valueText = valueText[..^suffix.Length];
                break;
            }
        }

        double value = PythonNumericParser.ParseFloat(valueText);
        return (multiplier * value) / 1.0e6;
    }
}
