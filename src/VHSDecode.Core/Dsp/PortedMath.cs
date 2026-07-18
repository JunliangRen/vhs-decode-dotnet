using System.Numerics;

namespace VHSDecode.Core.Dsp;

public static class PortedMath
{
    public static double[] ComplexAngle(ReadOnlySpan<Complex> input)
    {
        var output = new double[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = Math.Atan2(input[i].Imaginary, input[i].Real);
        }

        return output;
    }

    public static double[] UnwrapHilbert(ReadOnlySpan<Complex> input, double frequencyHz)
    {
        return UnwrapHilbertConjugateProduct(input, frequencyHz);
    }

    public static double[] UnwrapHilbertConjugateProduct(
        ReadOnlySpan<Complex> input,
        double frequencyHz)
    {
        if (input.IsEmpty)
        {
            throw new ArgumentException("Input must not be empty.", nameof(input));
        }

        var output = new double[input.Length];
        double scale = frequencyHz / Math.Tau;
        for (int i = 1; i < input.Length; i++)
        {
            Complex previous = input[i - 1];
            Complex current = input[i];
            double real = (current.Real * previous.Real)
                + (current.Imaginary * previous.Imaginary);
            double imaginary = (current.Imaginary * previous.Real)
                - (current.Real * previous.Imaginary);
            double difference = Math.Atan2(imaginary, real);
            if (difference < 0.0)
            {
                difference += Math.Tau;
            }

            output[i] = difference * scale;
        }

        return output;
    }

    public static double[] UnwrapHilbertVhsRustApproximation(
        ReadOnlySpan<Complex> input,
        double frequencyHz)
    {
        if (input.IsEmpty)
        {
            throw new ArgumentException("Input must not be empty.", nameof(input));
        }

        var output = new double[input.Length];
        float frequency = (float)frequencyHz;
        float previous = VhsRustAtan2Approximation(
            (float)input[0].Imaginary,
            (float)input[0].Real);
        for (int i = 1; i < input.Length; i++)
        {
            float current = VhsRustAtan2Approximation(
                (float)input[i].Imaginary,
                (float)input[i].Real);
            float difference = current - previous;
            difference -= MathF.Floor(difference / MathF.Tau) * MathF.Tau;
            output[i] = (float)((difference * frequency) / MathF.Tau);
            previous = current;
        }

        return output;
    }

    public static double[] UnwrapAngles(ReadOnlySpan<double> input)
    {
        if (input.IsEmpty)
        {
            throw new ArgumentException("Input must not be empty.", nameof(input));
        }

        var output = input.ToArray();
        UnwrapAnglesInPlace(output);
        return output;
    }

    public static void UnwrapAnglesInPlace(Span<double> output)
    {
        if (output.IsEmpty)
        {
            throw new ArgumentException("Input must not be empty.", nameof(output));
        }

        const double discont = Math.PI;
        const double period = Math.Tau;
        const double intervalHigh = discont;
        const double intervalLow = -discont;

        for (int i = 1; i < output.Length; i++)
        {
            double diff = output[i] - output[i - 1];
            double diffMod = Mod(diff - intervalLow, period) + intervalLow;

            if (diffMod == intervalLow && diff > 0.0)
            {
                diffMod = intervalHigh;
            }

            double correction = diffMod - diff;
            if (Math.Abs(diff) < discont)
            {
                correction = 0.0;
            }

            output[i] += correction;
        }
    }

    public static void DiffForwardInPlace(Span<double> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        for (int i = input.Length - 1; i >= 2; i--)
        {
            input[i] -= input[i - 1];
        }

        if (input.Length > 1)
        {
            input[1] -= input[0];
        }

        input[0] = 0.0;
    }

    public static double[] BuildHilbertMultiplier(int fftSize)
    {
        if (fftSize <= 0 || fftSize % 2 != 0)
        {
            throw new ArgumentException("Hilbert FFT size must be a positive even value.", nameof(fftSize));
        }

        var output = new double[fftSize];
        output[0] = 1.0;
        output[fftSize / 2] = 1.0;
        for (int i = 1; i < fftSize / 2; i++)
        {
            output[i] = 2.0;
        }

        return output;
    }

    public static double SuperGaussian(double x, double frequency, int order = 1, double centerFrequency = 0.0)
    {
        double scale = 2.0 * (x - centerFrequency) * Math.Pow(Math.Log(2.0) / 2.0, 1.0 / (2.0 * order));
        return Math.Exp(-2.0 * Math.Pow(scale / frequency, 2.0 * order));
    }

    public static double[] GenerateBandPassSuperGaussian(
        double frequencyLow,
        double frequencyHigh,
        int order,
        double nyquistHz,
        int blockLength)
    {
        if (blockLength <= 0 || blockLength % 2 != 0)
        {
            throw new ArgumentException("Block length must be a positive even value.", nameof(blockLength));
        }

        int halfCountInclusive = (blockLength / 2) + 1;
        var half = new double[blockLength / 2];
        double width = frequencyHigh - frequencyLow;
        double center = (frequencyHigh + frequencyLow) / 2.0;

        for (int i = 0; i < half.Length; i++)
        {
            double x = (nyquistHz * i) / (halfCountInclusive - 1);
            half[i] = SuperGaussian(x, width, order, center);
        }

        var output = new double[blockLength];
        for (int i = 0; i < half.Length; i++)
        {
            output[i] = half[i];
            output[blockLength - 1 - i] = half[i];
        }

        return output;
    }

    private static double Mod(double value, double divisor)
    {
        double result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static float VhsRustAtan2Approximation(float y, float x)
    {
        const float MinPositiveNormal = 1.17549435E-38f;
        x += MathF.CopySign(MinPositiveNormal, x);
        bool swap = MathF.Abs(x) < MathF.Abs(y);
        float input = swap ? x / y : y / x;
        float square = input * input;
        float result = input * (
            0.99997726f
            + (square * (
                -0.33262347f
                + (square * (
                    0.19354346f
                    + (square * (
                        -0.11643287f
                        + (square * (0.05265332f + (square * -0.01172120f))))))))));
        float halfPi = input >= 0.0f ? MathF.PI / 2.0f : -MathF.PI / 2.0f;
        result = swap ? halfPi - result : result;
        return (x >= 0.0f, y >= 0.0f) switch
        {
            (true, true) => result,
            (false, true) => MathF.PI + result,
            (false, false) => -MathF.PI + result,
            _ => result
        };
    }
}
