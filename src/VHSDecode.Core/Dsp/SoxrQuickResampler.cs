using System.Numerics;

namespace VHSDecode.Core.Dsp;

public static class SoxrQuickResampler
{
    private const double FixedPointScale = 65536.0 * 65536.0;

    public static double[] Resample(ReadOnlySpan<double> input, int inputRate, int outputRate)
    {
        if (inputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        }

        if (outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputRate));
        }

        if (input.IsEmpty)
        {
            return [];
        }

        if (inputRate == outputRate)
        {
            return input.ToArray();
        }

        double inputOutputRatio = (double)inputRate / outputRate;
        long step = (long)((inputOutputRatio * FixedPointScale) + 0.5);
        int outputLength = checked((int)((input.Length / inputOutputRatio) + 0.5));
        var source = new float[input.Length];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (float)input[i];
        }

        var output = new double[outputLength];
        for (int outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            ulong position = (ulong)outputIndex * (ulong)step;
            int integer = checked((int)(position >> 32));
            uint fraction = (uint)position;
            double x = fraction * (1.0 / FixedPointScale);

            float sampleMinus1 = SampleOrZero(source, integer - 1);
            float sample0 = SampleOrZero(source, integer);
            float sample1 = SampleOrZero(source, integer + 1);
            float sample2 = SampleOrZero(source, integer + 2);
            float neighborSum = sample1 + sampleMinus1;
            double b = (0.5 * neighborSum) - sample0;
            float sampleDelta = sample2 - sample1;
            sampleDelta += sampleMinus1;
            sampleDelta -= sample0;
            double a = (1.0 / 6.0)
                * (sampleDelta - (4.0 * b));
            float firstDifference = sample1 - sample0;
            double c = firstDifference - a - b;
            output[outputIndex] = (float)((((a * x) + b) * x + c) * x + sample0);
        }

        return output;
    }

    public static (int Numerator, int Denominator) ApproximateRatio(
        double value,
        int maxDenominator = 1000)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (maxDenominator < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDenominator));
        }

        (BigInteger numerator, BigInteger denominator) = ExactPositiveDoubleRatio(value);
        if (denominator <= maxDenominator)
        {
            return (checked((int)numerator), checked((int)denominator));
        }

        BigInteger p0 = BigInteger.Zero;
        BigInteger q0 = BigInteger.One;
        BigInteger p1 = BigInteger.One;
        BigInteger q1 = BigInteger.Zero;
        BigInteger n = numerator;
        BigInteger d = denominator;
        while (true)
        {
            BigInteger a = BigInteger.DivRem(n, d, out BigInteger remainder);
            BigInteger q2 = q0 + (a * q1);
            if (q2 > maxDenominator)
            {
                break;
            }

            (p0, p1) = (p1, p0 + (a * p1));
            (q0, q1) = (q1, q2);
            n = d;
            d = remainder;
        }

        BigInteger k = (maxDenominator - q0) / q1;
        BigInteger bound1Numerator = p0 + (k * p1);
        BigInteger bound1Denominator = q0 + (k * q1);
        BigInteger bound2Numerator = p1;
        BigInteger bound2Denominator = q1;
        BigInteger bound1Difference = BigInteger.Abs(
            (bound1Numerator * denominator) - (numerator * bound1Denominator));
        BigInteger bound2Difference = BigInteger.Abs(
            (bound2Numerator * denominator) - (numerator * bound2Denominator));
        bool chooseBound2 = (bound2Difference * bound1Denominator)
            <= (bound1Difference * bound2Denominator);
        return chooseBound2
            ? (checked((int)bound2Numerator), checked((int)bound2Denominator))
            : (checked((int)bound1Numerator), checked((int)bound1Denominator));
    }

    private static float SampleOrZero(ReadOnlySpan<float> input, int index)
        => (uint)index < (uint)input.Length ? input[index] : 0.0f;

    private static (BigInteger Numerator, BigInteger Denominator) ExactPositiveDoubleRatio(double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int encodedExponent = (int)((bits >> 52) & 0x7ff);
        ulong fraction = bits & 0x000f_ffff_ffff_ffffUL;
        BigInteger numerator;
        int exponent;
        if (encodedExponent == 0)
        {
            numerator = fraction;
            exponent = -1022 - 52;
        }
        else
        {
            numerator = fraction | (1UL << 52);
            exponent = encodedExponent - 1023 - 52;
        }

        BigInteger denominator = BigInteger.One;
        if (exponent >= 0)
        {
            numerator <<= exponent;
        }
        else
        {
            denominator <<= -exponent;
        }

        BigInteger divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return (numerator / divisor, denominator / divisor);
    }
}
