using System.Text.Json;
using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Tbc;

public sealed class VideoOutputConverter
{
    public const int PalOutputScaleDividend = 0xD300 - 0x0100;
    public const int NtscOutputScaleDividend = 0xC800 - 0x0400;

    public VideoOutputConverter(double ire0, double hzIre, int outputZero, double vsyncIre, double outputScale)
    {
        if (!double.IsFinite(ire0))
        {
            throw new ArgumentOutOfRangeException(nameof(ire0));
        }

        if (!double.IsFinite(hzIre) || hzIre == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hzIre));
        }

        if (!double.IsFinite(vsyncIre))
        {
            throw new ArgumentOutOfRangeException(nameof(vsyncIre));
        }

        if (!double.IsFinite(outputScale))
        {
            throw new ArgumentOutOfRangeException(nameof(outputScale));
        }

        Ire0 = ire0;
        HzIre = hzIre;
        OutputZero = outputZero;
        VSyncIre = vsyncIre;
        OutputScale = outputScale;
        Scale = outputScale / hzIre;
        Offset = outputZero - (vsyncIre * outputScale) - (ire0 * Scale);
    }

    public double Ire0 { get; }

    public double HzIre { get; }

    public int OutputZero { get; }

    public double VSyncIre { get; }

    public double OutputScale { get; }

    public double Scale { get; }

    public double Offset { get; }

    public static VideoOutputConverter FromParameters(FormatParameterSet parameters)
    {
        bool isPal = FormatCatalog.ParentSystem(parameters.System) == "PAL";
        double ire0 = GetDecoderParameter(parameters, "ire0");
        double hzIre = GetDecoderParameter(parameters, "hz_ire");
        double vsyncIre = GetDecoderParameter(parameters, "vsync_ire");
        int outputZero = (int)GetParameter(parameters.SysParams, "outputZero");
        return new VideoOutputConverter(
            ire0,
            hzIre,
            outputZero,
            vsyncIre,
            ComputeOutputScale(isPal, vsyncIre));
    }

    public static double ComputeOutputScale(bool isPal, double vsyncIre)
    {
        if (!double.IsFinite(vsyncIre) || vsyncIre >= 100.0)
        {
            throw new ArgumentOutOfRangeException(nameof(vsyncIre));
        }

        int dividend = isPal ? PalOutputScaleDividend : NtscOutputScaleDividend;
        return dividend / (100.0 - vsyncIre);
    }

    public double IreToHz(double ire) => Ire0 + (HzIre * ire);

    public double HzToIre(double hz) => (hz - Ire0) / HzIre;

    public double OutputToIre(ushort output) => ((output - OutputZero) / OutputScale) + VSyncIre;

    public ushort ConvertHz(double hz)
    {
        double output = (hz * Scale) + Offset + 0.5;
        if (output <= 0.0)
        {
            return 0;
        }

        if (output >= ushort.MaxValue)
        {
            return ushort.MaxValue;
        }

        return (ushort)output;
    }

    public ushort[] ConvertHz(ReadOnlySpan<double> input)
    {
        var output = new ushort[input.Length];
        ConvertHz(input, output);
        return output;
    }

    public void ConvertHz(ReadOnlySpan<double> input, Span<ushort> output)
    {
        if (output.Length != input.Length)
        {
            throw new ArgumentException("Output length must match input length.", nameof(output));
        }

        double inverseHzIre = 1.0 / HzIre;
        double fastMathOffset = (OutputZero + 0.5) - (VSyncIre * OutputScale);
        for (int i = 0; i < input.Length; i++)
        {
            // Upstream's ndarray conversion is Numba fastmath code, which
            // reassociates around ire0 and contracts the final multiply-add.
            double scaledDelta = OutputScale * (input[i] - Ire0);
            double value = Math.FusedMultiplyAdd(scaledDelta, inverseHzIre, fastMathOffset);
            if (value <= 0.0)
            {
                output[i] = 0;
            }
            else if (value >= ushort.MaxValue)
            {
                output[i] = ushort.MaxValue;
            }
            else
            {
                output[i] = (ushort)value;
            }
        }
    }

    private static double GetDecoderParameter(FormatParameterSet parameters, string propertyName)
    {
        if (TryGetNumber(parameters.RfParams, propertyName, out double value))
        {
            return value;
        }

        return GetParameter(parameters.SysParams, propertyName);
    }

    private static double GetParameter(JsonElement element, string propertyName)
    {
        if (TryGetNumber(element, propertyName, out double value))
        {
            return value;
        }

        throw new FormatParameterException($"Format parameters did not contain numeric '{propertyName}'.");
    }

    private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0.0;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetDouble();
            return true;
        }

        return false;
    }
}
