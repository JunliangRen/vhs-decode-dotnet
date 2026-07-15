using System.Globalization;
using System.Numerics;
using VHSDecode.Core.CommandLine;

namespace VHSDecode.Core.Decode;

public readonly record struct DecodeStartPosition(long Sample, double? NonFiniteSample = null)
{
    public long ResolveForRead()
    {
        if (!NonFiniteSample.HasValue)
        {
            return Sample;
        }

        double value = NonFiniteSample.Value;
        Exception conversionError = double.IsNaN(value)
            ? new ArgumentException("cannot convert float NaN to integer")
            : new OverflowException("cannot convert float infinity to integer");
        throw new DecodeFieldReadException(FormatForReport(), conversionError);
    }

    public string FormatForReport()
    {
        if (!NonFiniteSample.HasValue)
        {
            return Sample.ToString(CultureInfo.InvariantCulture);
        }

        double value = NonFiniteSample.Value;
        if (double.IsNaN(value))
        {
            return "nan";
        }

        return double.IsNegativeInfinity(value) ? "-inf" : "inf";
    }

    internal static DecodeStartPosition FromInteger(BigInteger sample)
    {
        if (sample <= BigInteger.Zero)
        {
            return new DecodeStartPosition(0);
        }

        return sample >= long.MaxValue
            ? new DecodeStartPosition(long.MaxValue)
            : new DecodeStartPosition((long)sample);
    }

    internal static DecodeStartPosition FromFloat(double sample)
    {
        if (!double.IsFinite(sample))
        {
            return new DecodeStartPosition(0, sample);
        }

        if (sample <= 0.0)
        {
            return new DecodeStartPosition(0);
        }

        return sample >= long.MaxValue
            ? new DecodeStartPosition(long.MaxValue)
            : new DecodeStartPosition((long)Math.Truncate(sample));
    }
}

public readonly record struct DecodeRunBounds(
    DecodeStartPosition StartPosition,
    DecodeStartPosition StartFramePosition,
    bool HasExplicitStartFrame,
    BigInteger RequestedFieldCount)
{
    public long StartSample => StartPosition.Sample;

    public static DecodeRunBounds FromCommand(ParsedCommand command, int nominalFieldSampleCount)
    {
        if (nominalFieldSampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalFieldSampleCount));
        }

        object? startValue = command.Values.TryGetValue("start", out object? parsedStart)
            ? parsedStart
            : 0;
        DecodeStartPosition startFramePosition = BuildStartFramePosition(
            startValue,
            nominalFieldSampleCount);
        double startFileLocation = command.Get<double>("start_fileloc");
        DecodeStartPosition startPosition = startFileLocation != -1.0
            ? DecodeStartPosition.FromFloat(startFileLocation)
            : startFramePosition;

        BigInteger frames = BigInteger.Max(BigInteger.Zero, command.Get<BigInteger>("length"));
        BigInteger fields = frames * 2;
        return new DecodeRunBounds(
            startPosition,
            startFramePosition,
            IsExplicitStartFrame(startValue),
            fields);
    }

    private static DecodeStartPosition BuildStartFramePosition(
        object? value,
        int nominalFieldSampleCount)
    {
        return value switch
        {
            int intValue => DecodeStartPosition.FromInteger(
                new BigInteger(intValue) * 2 * nominalFieldSampleCount),
            BigInteger integerValue => DecodeStartPosition.FromInteger(
                integerValue * 2 * nominalFieldSampleCount),
            double doubleValue => DecodeStartPosition.FromFloat(
                doubleValue * 2.0 * nominalFieldSampleCount),
            null => new DecodeStartPosition(0),
            _ => DecodeStartPosition.FromFloat(
                Convert.ToDouble(value, CultureInfo.InvariantCulture)
                * 2.0
                * nominalFieldSampleCount)
        };
    }

    private static bool IsExplicitStartFrame(object? value)
        => value switch
        {
            int intValue => intValue != 0,
            BigInteger integerValue => !integerValue.IsZero,
            double doubleValue => doubleValue != 0.0,
            null => false,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0.0
        };
}
