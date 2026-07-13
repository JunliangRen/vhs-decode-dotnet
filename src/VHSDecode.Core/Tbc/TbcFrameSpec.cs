using System.Text.Json;
using VHSDecode.Core.Formats;

namespace VHSDecode.Core.Tbc;

public sealed record TbcFrameSpec(
    string System,
    int OutputLineLength,
    int OutputLineCount,
    double OutputSampleRateHz,
    int? ColourBurstStart,
    int? ColourBurstEnd,
    int? ActiveVideoStart,
    int? ActiveVideoEnd)
{
    private const double VideoJsonPixelAdjustment = -1.4;

    public int FieldSampleCount => checked(OutputLineLength * OutputLineCount);

    public static TbcFrameSpec FromParameters(FormatParameterSet parameters)
    {
        JsonElement sys = parameters.SysParams;
        int outputLineLength = sys.GetProperty("outlinelen").GetInt32();
        int outputLineCount = (sys.GetProperty("frame_lines").GetInt32() / 2) + 1;
        double outputSampleRateHz = sys.GetProperty("outfreq").GetDouble() * 1_000_000.0;

        return new TbcFrameSpec(
            parameters.System,
            outputLineLength,
            outputLineCount,
            outputSampleRateHz,
            OutputPixelFromUsecRange(sys, "colorBurstUS", 0),
            OutputPixelFromUsecRange(sys, "colorBurstUS", 1),
            OutputPixelFromUsecRange(sys, "activeVideoUS", 0),
            OutputPixelFromUsecRange(sys, "activeVideoUS", 1));
    }

    private static int? OutputPixelFromUsecRange(JsonElement sysParams, string propertyName, int index)
    {
        if (!sysParams.TryGetProperty(propertyName, out JsonElement range)
            || range.ValueKind != JsonValueKind.Array
            || range.GetArrayLength() <= index)
        {
            return null;
        }

        double outputSamplesPerUsec = sysParams.GetProperty("outfreq").GetDouble();
        return (int)Math.Round((range[index].GetDouble() * outputSamplesPerUsec) + VideoJsonPixelAdjustment);
    }
}
