using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VHSDecode.Core.Dsp;

public static class PortedMath
{
    private static readonly Vector128<float> VhsRustAbsoluteValueMask =
        Vector128.Create(BitConverter.UInt32BitsToSingle(0x7FFFFFFFU));
    private static readonly Vector128<float> VhsRustSignMask = Vector128.Create(-0.0f);
    private static readonly Vector128<float> VhsRustMinPositiveNormal = Vector128.Create(1.17549435E-38f);
    private static readonly Vector128<float> VhsRustMaximumFinite = Vector128.Create(float.MaxValue);
    private static readonly Vector128<float> VhsRustTau = Vector128.Create(MathF.Tau);

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
        => UnwrapHilbertVhsRustApproximationCore(input, frequencyHz, allowSimd: true);

    internal static double[] UnwrapHilbertVhsRustApproximationScalar(
        ReadOnlySpan<Complex> input,
        double frequencyHz)
        => UnwrapHilbertVhsRustApproximationCore(input, frequencyHz, allowSimd: false);

    internal static unsafe double[] UnwrapHilbertVhsRustApproximation(
        ReadOnlySpan<double> real,
        ReadOnlySpan<double> imaginary,
        double frequencyHz)
    {
        if (real.IsEmpty)
        {
            throw new ArgumentException("Input must not be empty.", nameof(real));
        }

        if (imaginary.Length != real.Length)
        {
            throw new ArgumentException("Real and imaginary inputs must have matching lengths.", nameof(imaginary));
        }

        var output = new double[real.Length];
        float frequency = (float)frequencyHz;
        float previous = VhsRustAtan2Approximation((float)imaginary[0], (float)real[0]);
        int i = 1;
        if (Avx.IsSupported && Sse.IsSupported)
        {
            fixed (double* realPointer = real)
            fixed (double* imaginaryPointer = imaginary)
            fixed (double* outputPointer = output)
            {
                float* angles = stackalloc float[4];
                int vectorizedEnd = real.Length - ((real.Length - i) % 4);
                for (; i < vectorizedEnd; i += 4)
                {
                    if (TryVhsRustAtan2Approximation4(
                        realPointer + i,
                        imaginaryPointer + i,
                        out Vector128<float> currentAngles))
                    {
                        if (Sse41.IsSupported)
                        {
                            previous = StoreVhsRustFrequencyDifferences4(
                                currentAngles,
                                previous,
                                frequency,
                                outputPointer + i);
                        }
                        else
                        {
                            Sse.Store(angles, currentAngles);
                            for (int lane = 0; lane < 4; lane++)
                            {
                                float current = angles[lane];
                                outputPointer[i + lane] = VhsRustFrequencyDifference(current, previous, frequency);
                                previous = current;
                            }
                        }
                    }
                    else
                    {
                        for (int lane = 0; lane < 4; lane++)
                        {
                            float current = VhsRustAtan2Approximation(
                                (float)imaginaryPointer[i + lane],
                                (float)realPointer[i + lane]);
                            outputPointer[i + lane] = VhsRustFrequencyDifference(current, previous, frequency);
                            previous = current;
                        }
                    }
                }
            }
        }

        for (; i < real.Length; i++)
        {
            float current = VhsRustAtan2Approximation((float)imaginary[i], (float)real[i]);
            output[i] = VhsRustFrequencyDifference(current, previous, frequency);
            previous = current;
        }

        return output;
    }

    private static unsafe double[] UnwrapHilbertVhsRustApproximationCore(
        ReadOnlySpan<Complex> input,
        double frequencyHz,
        bool allowSimd)
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
        int i = 1;
        if (allowSimd && Avx.IsSupported && Sse.IsSupported)
        {
            fixed (Complex* inputPointer = input)
            fixed (double* outputPointer = output)
            {
                float* angles = stackalloc float[4];
                double* inputValues = (double*)inputPointer;
                int vectorizedEnd = input.Length - ((input.Length - i) % 4);
                for (; i < vectorizedEnd; i += 4)
                {
                    if (TryVhsRustAtan2Approximation4(inputValues + (i * 2), out Vector128<float> currentAngles))
                    {
                        if (Sse41.IsSupported)
                        {
                            previous = StoreVhsRustFrequencyDifferences4(
                                currentAngles,
                                previous,
                                frequency,
                                outputPointer + i);
                        }
                        else
                        {
                            Sse.Store(angles, currentAngles);
                            for (int lane = 0; lane < 4; lane++)
                            {
                                float current = angles[lane];
                                outputPointer[i + lane] = VhsRustFrequencyDifference(current, previous, frequency);
                                previous = current;
                            }
                        }
                    }
                    else
                    {
                        for (int lane = 0; lane < 4; lane++)
                        {
                            Complex sample = inputPointer[i + lane];
                            float current = VhsRustAtan2Approximation(
                                (float)sample.Imaginary,
                                (float)sample.Real);
                            outputPointer[i + lane] = VhsRustFrequencyDifference(current, previous, frequency);
                            previous = current;
                        }
                    }
                }
            }
        }

        for (; i < input.Length; i++)
        {
            float current = VhsRustAtan2Approximation(
                (float)input[i].Imaginary,
                (float)input[i].Real);
            output[i] = VhsRustFrequencyDifference(current, previous, frequency);
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

    private static float VhsRustFrequencyDifference(float current, float previous, float frequency)
    {
        float difference = current - previous;
        difference -= MathF.Floor(difference / MathF.Tau) * MathF.Tau;
        return (float)((difference * frequency) / MathF.Tau);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float StoreVhsRustFrequencyDifferences4(
        Vector128<float> current,
        float previous,
        float frequency,
        double* output)
    {
        Vector128<float> previousAngles = Sse2.ShiftLeftLogical128BitLane(current.AsByte(), 4).AsSingle();
        previousAngles = Sse.MoveScalar(previousAngles, Vector128.CreateScalar(previous));
        Vector128<float> difference = Sse.Subtract(current, previousAngles);
        Vector128<float> periods = Sse41.Floor(Sse.Divide(difference, VhsRustTau));
        difference = Sse.Subtract(difference, Sse.Multiply(periods, VhsRustTau));
        Vector128<float> frequencies = Sse.Divide(
            Sse.Multiply(difference, Vector128.Create(frequency)),
            VhsRustTau);
        Avx.Store(output, Avx.ConvertToVector256Double(frequencies));
        return current.GetElement(3);
    }

    private static unsafe bool TryVhsRustAtan2Approximation4(
        double* input,
        out Vector128<float> result)
    {
        Vector128<float> first = Avx.ConvertToVector128Single(Avx.LoadVector256(input));
        Vector128<float> second = Avx.ConvertToVector128Single(Avx.LoadVector256(input + 4));
        Vector128<float> x = Sse.Shuffle(first, second, 0x88);
        Vector128<float> y = Sse.Shuffle(first, second, 0xDD);
        return TryVhsRustAtan2Approximation4(x, y, out result);
    }

    private static unsafe bool TryVhsRustAtan2Approximation4(
        double* real,
        double* imaginary,
        out Vector128<float> result)
    {
        Vector128<float> x = Avx.ConvertToVector128Single(Avx.LoadVector256(real));
        Vector128<float> y = Avx.ConvertToVector128Single(Avx.LoadVector256(imaginary));
        return TryVhsRustAtan2Approximation4(x, y, out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryVhsRustAtan2Approximation4(
        Vector128<float> x,
        Vector128<float> y,
        out Vector128<float> result)
    {
        Vector128<float> absoluteX = Sse.And(x, VhsRustAbsoluteValueMask);
        Vector128<float> absoluteY = Sse.And(y, VhsRustAbsoluteValueMask);
        Vector128<float> finite = Sse.And(
            Sse.CompareLessThanOrEqual(absoluteX, VhsRustMaximumFinite),
            Sse.CompareLessThanOrEqual(absoluteY, VhsRustMaximumFinite));
        if (Sse.MoveMask(finite) != 0b1111)
        {
            result = default;
            return false;
        }

        Vector128<float> signedMinimum = Sse.Or(
            Sse.And(x, VhsRustSignMask),
            VhsRustMinPositiveNormal);
        x = Sse.Add(x, signedMinimum);
        absoluteX = Sse.And(x, VhsRustAbsoluteValueMask);
        Vector128<float> swap = Sse.CompareLessThan(absoluteX, absoluteY);
        Vector128<float> ratio = Sse.Divide(
            Select(swap, x, y),
            Select(swap, y, x));
        Vector128<float> square = Sse.Multiply(ratio, ratio);
        Vector128<float> polynomial = Sse.Add(
            Vector128.Create(0.05265332f),
            Sse.Multiply(square, Vector128.Create(-0.01172120f)));
        polynomial = Sse.Add(
            Vector128.Create(-0.11643287f),
            Sse.Multiply(square, polynomial));
        polynomial = Sse.Add(
            Vector128.Create(0.19354346f),
            Sse.Multiply(square, polynomial));
        polynomial = Sse.Add(
            Vector128.Create(-0.33262347f),
            Sse.Multiply(square, polynomial));
        result = Sse.Multiply(
            ratio,
            Sse.Add(
                Vector128.Create(0.99997726f),
                Sse.Multiply(square, polynomial)));

        Vector128<float> zero = Vector128<float>.Zero;
        Vector128<float> ratioNonNegative = Sse.CompareGreaterThanOrEqual(ratio, zero);
        Vector128<float> halfPi = Select(
            ratioNonNegative,
            Vector128.Create(MathF.PI / 2.0f),
            Vector128.Create(-MathF.PI / 2.0f));
        result = Select(swap, Sse.Subtract(halfPi, result), result);

        Vector128<float> xNonNegative = Sse.CompareGreaterThanOrEqual(x, zero);
        Vector128<float> yNonNegative = Sse.CompareGreaterThanOrEqual(y, zero);
        Vector128<float> quadrantOffset = Select(
            yNonNegative,
            Vector128.Create(MathF.PI),
            Vector128.Create(-MathF.PI));
        result = Select(xNonNegative, result, Sse.Add(result, quadrantOffset));
        return true;
    }

    private static Vector128<float> Select(
        Vector128<float> mask,
        Vector128<float> whenTrue,
        Vector128<float> whenFalse)
        => Sse.Or(Sse.And(mask, whenTrue), Sse.AndNot(mask, whenFalse));
}
