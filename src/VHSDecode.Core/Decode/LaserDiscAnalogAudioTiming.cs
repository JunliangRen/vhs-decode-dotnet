namespace VHSDecode.Core.Decode;

public static class LaserDiscAnalogAudioTiming
{
    public static long EstimateFieldNumber(
        long previousFieldNumber,
        long previousReadLocation,
        long currentReadLocation,
        double sampleRateHz,
        double framesPerSecond)
    {
        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (framesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        double samplesPerField = sampleRateHz / framesPerSecond / 2.0;
        double readGap = (currentReadLocation - previousReadLocation) / samplesPerField;
        return checked((long)Math.Round(previousFieldNumber + readGap, MidpointRounding.ToEven));
    }

    public static double ComputeTimeOffset(
        long fieldNumber,
        bool isFirstField,
        int firstFieldLines,
        int secondFieldLines,
        double linePeriodUs,
        double outputFrequency)
    {
        if (firstFieldLines <= 0 || secondFieldLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstFieldLines));
        }

        if (linePeriodUs <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(linePeriodUs));
        }

        if (outputFrequency < 16_000.0)
        {
            return 0.0;
        }

        long lineCount = checked((long)(firstFieldLines + secondFieldLines) * (fieldNumber / 2));
        if (!isFirstField)
        {
            lineCount = checked(lineCount + firstFieldLines);
        }

        double samplesPerLine = (linePeriodUs / 1_000_000.0) * outputFrequency;
        double audioSampleCount = lineCount * samplesPerLine;
        double sampleOffset = audioSampleCount - Math.Floor(audioSampleCount);
        return sampleOffset > 0.5
            ? (1.0 - sampleOffset) / outputFrequency
            : -sampleOffset / outputFrequency;
    }
}
