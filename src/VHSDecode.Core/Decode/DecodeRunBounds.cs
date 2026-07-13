using VHSDecode.Core.CommandLine;

namespace VHSDecode.Core.Decode;

public readonly record struct DecodeRunBounds(long StartSample, int RequestedFieldCount)
{
    public static DecodeRunBounds FromCommand(ParsedCommand command, int nominalFieldSampleCount)
    {
        if (nominalFieldSampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalFieldSampleCount));
        }

        double startFileLocation = command.Get<double>("start_fileloc");
        long startSample = startFileLocation != -1.0
            ? Math.Max(0L, (long)Math.Floor(startFileLocation))
            : Math.Max(0L, (long)Math.Floor(ReadStartFrame(command) * 2.0 * nominalFieldSampleCount));

        int frames = Math.Max(0, command.Get<int>("length"));
        int fields = checked(frames * 2);
        return new DecodeRunBounds(startSample, fields);
    }

    private static double ReadStartFrame(ParsedCommand command)
    {
        object? value = command.Values.TryGetValue("start", out object? parsed) ? parsed : 0;
        return value switch
        {
            int intValue => intValue,
            double doubleValue => doubleValue,
            null => 0.0,
            _ => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }
}
