namespace VHSDecode.Core.HiFi;

internal readonly record struct HiFiFirstOrderCoefficients(
    double B0,
    double B1,
    double A1);

internal sealed class HiFiFirstOrderFilter
{
    private double _previousInput;
    private double _previousOutput;

    internal HiFiFirstOrderFilter(HiFiFirstOrderCoefficients coefficients)
    {
        Coefficients = coefficients;
    }

    internal HiFiFirstOrderCoefficients Coefficients { get; }
    internal double PreviousInput => _previousInput;
    internal double PreviousOutput => _previousOutput;

    internal void Process(Span<float> audio)
    {
        double b0 = Coefficients.B0;
        double b1 = Coefficients.B1;
        double a1 = Coefficients.A1;
        double previousInput = _previousInput;
        double previousOutput = _previousOutput;
        for (int i = 0; i < audio.Length; i++)
        {
            double input = audio[i];
            double accumulator = a1 * previousOutput;
            accumulator = Math.FusedMultiplyAdd(-b1, previousInput, accumulator);
            double output = Math.FusedMultiplyAdd(b0, input, -accumulator);
            audio[i] = (float)output;
            previousInput = input;
            previousOutput = output;
        }

        _previousInput = previousInput;
        _previousOutput = previousOutput;
    }
}

internal sealed class HiFiDcBlocker
{
    private const int StageCount = 3;
    private double _x1;
    private double _y1;
    private double _x2;
    private double _y2;
    private double _x3;
    private double _y3;

    internal HiFiDcBlocker(int sampleRateHz, double cutoffHz)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(cutoffHz) || cutoffHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(cutoffHz));
        }

        double scale = 1.0 / Math.Sqrt(Math.Pow(2.0, 1.0 / StageCount) - 1.0);
        double stageCutoff = cutoffHz * scale;
        R = Math.Exp(-2.0 * Math.PI * stageCutoff / sampleRateHz);
    }

    internal double R { get; }
    internal double X1 => _x1;
    internal double Y1 => _y1;
    internal double X2 => _x2;
    internal double Y2 => _y2;
    internal double X3 => _x3;
    internal double Y3 => _y3;

    internal void Process(Span<float> audio)
    {
        double x1 = _x1;
        double y1 = _y1;
        double x2 = _x2;
        double y2 = _y2;
        double x3 = _x3;
        double y3 = _y3;
        for (int i = 0; i < audio.Length; i++)
        {
            double input = audio[i];
            double nextY1 = Math.FusedMultiplyAdd(R, y1, input - x1);
            x1 = input;
            y1 = nextY1;

            double nextY2 = Math.FusedMultiplyAdd(R, y2, nextY1 - x2);
            x2 = nextY1;
            y2 = nextY2;

            double nextY3 = Math.FusedMultiplyAdd(R, y3, nextY2 - x3);
            x3 = nextY2;
            y3 = nextY3;
            audio[i] = (float)nextY3;
        }

        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
        _x3 = x3;
        _y3 = y3;
    }
}

internal static class HiFiShelfFilterDesign
{
    internal static HiFiFirstOrderCoefficients Low(
        double lowTau,
        double highTau,
        int sampleRateHz)
        => Build(lowTau, highTau, sampleRateHz, lowShelf: true);

    internal static HiFiFirstOrderCoefficients High(
        double lowTau,
        double highTau,
        int sampleRateHz)
        => Build(lowTau, highTau, sampleRateHz, lowShelf: false);

    private static HiFiFirstOrderCoefficients Build(
        double lowTau,
        double highTau,
        int sampleRateHz,
        bool lowShelf)
    {
        if (!double.IsFinite(lowTau) || lowTau <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lowTau));
        }

        if (!double.IsFinite(highTau) || highTau <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(highTau));
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        double secondAnalog = 1.0 / (lowTau / highTau);
        double firstAnalog;
        double gain;
        if (lowShelf)
        {
            firstAnalog = Math.Pow(highTau, 2.0) / lowTau;
            gain = secondAnalog;
        }
        else
        {
            firstAnalog = highTau;
            gain = firstAnalog / highTau;
        }

        firstAnalog /= gain;
        secondAnalog /= gain;
        double denominatorFirst = lowShelf ? lowTau : highTau;
        return BilinearFirstOrder(
            firstAnalog,
            secondAnalog,
            denominatorFirst,
            1.0,
            sampleRateHz);
    }

    private static HiFiFirstOrderCoefficients BilinearFirstOrder(
        double numeratorFirst,
        double numeratorSecond,
        double denominatorFirst,
        double denominatorSecond,
        int sampleRateHz)
    {
        double factor = Math.Sqrt(sampleRateHz * 2.0);
        double numeratorSecondScaled = numeratorSecond / factor;
        double numeratorFirstScaled = numeratorFirst * factor;
        double denominatorSecondScaled = denominatorSecond / factor;
        double denominatorFirstScaled = denominatorFirst * factor;

        double numeratorLeading = numeratorSecondScaled + numeratorFirstScaled;
        double numeratorTrailing = numeratorSecondScaled - numeratorFirstScaled;
        double denominatorLeading = denominatorSecondScaled + denominatorFirstScaled;
        double denominatorTrailing = denominatorSecondScaled - denominatorFirstScaled;
        return new HiFiFirstOrderCoefficients(
            numeratorLeading / denominatorLeading,
            numeratorTrailing / denominatorLeading,
            denominatorTrailing / denominatorLeading);
    }
}
