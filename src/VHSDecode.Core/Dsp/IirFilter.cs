using System.Buffers;

namespace VHSDecode.Core.Dsp;

public static class IirFilter
{
    private const int MaximumPooledSampleCount = 4 * 1024 * 1024;
    private const int MaximumArraysPerBucket = 3;
    private static readonly ArrayPool<double> WorkingBufferPool = ArrayPool<double>.Create(
        MaximumPooledSampleCount,
        MaximumArraysPerBucket);

    public static double[] ApplyForward(TransferFunction filter, ReadOnlySpan<double> input)
    {
        (double[] numerator, double[] denominator) = Normalize(filter);
        double[] state = new double[Math.Max(numerator.Length, denominator.Length) - 1];
        return ApplyForward(numerator, denominator, input, state);
    }

    public static double[] ApplyForward(
        TransferFunction filter,
        ReadOnlySpan<double> input,
        double[] initialState,
        out double[] finalState)
    {
        (double[] numerator, double[] denominator) = Normalize(filter);
        finalState = initialState.ToArray();
        return ApplyForward(numerator, denominator, input, finalState, retainFinalState: true);
    }

    public static double[] SteadyStateInitialConditions(TransferFunction filter)
    {
        (double[] numerator, double[] denominator) = Normalize(filter);
        return SteadyStateInitialConditions(numerator, denominator);
    }

    public static double[] ApplyForwardBackward(TransferFunction filter, ReadOnlySpan<double> input, int? padLength = null)
    {
        if (input.IsEmpty)
        {
            return [];
        }

        (double[] numerator, double[] denominator) = Normalize(filter);
        int edge = padLength ?? DefaultPadLength(numerator, denominator);
        if (edge < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padLength));
        }

        int actualEdge = Math.Min(edge, input.Length - 1);
        double[] zi = SteadyStateInitialConditions(numerator, denominator);
        if (actualEdge == 0)
        {
            double[] output = input.ToArray();
            ApplyForwardBackwardInPlace(numerator, denominator, zi, output);
            return output;
        }

        int extendedLength = checked(input.Length + (actualEdge * 2));
        double[] rented = WorkingBufferPool.Rent(extendedLength);
        try
        {
            Span<double> extended = rented.AsSpan(0, extendedLength);
            WriteOddExtension(input, actualEdge, extended);
            ApplyForwardBackwardInPlace(numerator, denominator, zi, extended);

            var output = new double[input.Length];
            extended.Slice(actualEdge, output.Length).CopyTo(output);
            return output;
        }
        finally
        {
            WorkingBufferPool.Return(rented);
        }
    }

    public static int DefaultPadLength(TransferFunction filter)
    {
        (double[] numerator, double[] denominator) = Normalize(filter);
        return DefaultPadLength(numerator, denominator);
    }

    private static int DefaultPadLength(double[] numerator, double[] denominator)
        => 3 * Math.Max(numerator.Length, denominator.Length);

    private static (double[] Numerator, double[] Denominator) Normalize(TransferFunction filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Numerator.Length == 0 || filter.Denominator.Length == 0)
        {
            throw new ArgumentException("IIR filters must have numerator and denominator coefficients.", nameof(filter));
        }

        double a0 = filter.Denominator[0];
        if (a0 == 0.0)
        {
            throw new ArgumentException("IIR denominator a0 must not be zero.", nameof(filter));
        }

        if (a0 == 1.0)
        {
            return (filter.Numerator, filter.Denominator);
        }

        double[] numerator = filter.Numerator.ToArray();
        double[] denominator = filter.Denominator.ToArray();
        for (int i = 0; i < numerator.Length; i++)
        {
            numerator[i] /= a0;
        }

        for (int i = 0; i < denominator.Length; i++)
        {
            denominator[i] /= a0;
        }

        return (numerator, denominator);
    }

    private static double[] ApplyForward(
        double[] numerator,
        double[] denominator,
        ReadOnlySpan<double> input,
        double[] initialState)
        => ApplyForward(numerator, denominator, input, initialState, retainFinalState: false);

    private static double[] ApplyForward(
        double[] numerator,
        double[] denominator,
        ReadOnlySpan<double> input,
        double[] state,
        bool retainFinalState)
    {
        int stateLength = Math.Max(numerator.Length, denominator.Length) - 1;
        if (state.Length != stateLength)
        {
            throw new ArgumentException("Initial state length does not match the IIR filter order.", nameof(state));
        }

        double[] workingState = retainFinalState ? state : state.ToArray();
        double[] output = input.ToArray();
        ApplyForwardInPlace(numerator, denominator, output, workingState);
        return output;
    }

    private static void ApplyForwardInPlace(
        double[] numerator,
        double[] denominator,
        Span<double> samples,
        double[] state)
    {
        int stateLength = state.Length;
        if (numerator.Length == stateLength + 1 && denominator.Length == stateLength + 1)
        {
            for (int sample = 0; sample < samples.Length; sample++)
            {
                double x = samples[sample];
                double y = (numerator[0] * x) + (stateLength > 0 ? state[0] : 0.0);
                for (int i = 1; i < stateLength; i++)
                {
                    state[i - 1] = (numerator[i] * x) + state[i] - (denominator[i] * y);
                }

                if (stateLength > 0)
                {
                    state[stateLength - 1] =
                        (numerator[stateLength] * x) - (denominator[stateLength] * y);
                }

                samples[sample] = y;
            }

            return;
        }

        for (int sample = 0; sample < samples.Length; sample++)
        {
            double x = samples[sample];
            double y = (numerator[0] * x) + (stateLength > 0 ? state[0] : 0.0);
            for (int i = 1; i < stateLength; i++)
            {
                double b = i < numerator.Length ? numerator[i] : 0.0;
                double a = i < denominator.Length ? denominator[i] : 0.0;
                state[i - 1] = (b * x) + state[i] - (a * y);
            }

            if (stateLength > 0)
            {
                int i = stateLength;
                double b = i < numerator.Length ? numerator[i] : 0.0;
                double a = i < denominator.Length ? denominator[i] : 0.0;
                state[stateLength - 1] = (b * x) - (a * y);
            }

            samples[sample] = y;
        }
    }

    private static void ApplyForwardBackwardInPlace(
        double[] numerator,
        double[] denominator,
        double[] steadyState,
        Span<double> samples)
    {
        double[] firstState = Scale(steadyState, samples[0]);
        ApplyForwardInPlace(numerator, denominator, samples, firstState);
        samples.Reverse();
        double[] secondState = Scale(steadyState, samples[0]);
        ApplyForwardInPlace(numerator, denominator, samples, secondState);
        samples.Reverse();
    }

    private static double[] SteadyStateInitialConditions(double[] numerator, double[] denominator)
    {
        int stateLength = Math.Max(numerator.Length, denominator.Length) - 1;
        if (stateLength == 0)
        {
            return [];
        }

        int coefficientCount = stateLength + 1;
        double[]? paddedNumerator = null;
        double[]? paddedDenominator = null;
        ReadOnlySpan<double> effectiveNumerator = numerator;
        ReadOnlySpan<double> effectiveDenominator = denominator;
        if (numerator.Length != coefficientCount)
        {
            paddedNumerator = new double[coefficientCount];
            numerator.CopyTo(paddedNumerator, 0);
            effectiveNumerator = paddedNumerator;
        }

        if (denominator.Length != coefficientCount)
        {
            paddedDenominator = new double[coefficientCount];
            denominator.CopyTo(paddedDenominator, 0);
            effectiveDenominator = paddedDenominator;
        }

        double numeratorSum = SumNumpy(effectiveNumerator);
        double denominatorSum = SumNumpy(effectiveDenominator);
        if (denominatorSum == 0.0)
        {
            throw new ArgumentException("IIR filter has a pole at z = 1.", nameof(denominator));
        }

        double steadyOutput = numeratorSum / denominatorSum;
        var state = new double[stateLength];
        double cumulative = 0.0;
        for (int i = coefficientCount - 1; i >= 1; i--)
        {
            cumulative += effectiveNumerator[i] - (steadyOutput * effectiveDenominator[i]);
            state[i - 1] = cumulative;
        }

        return state;
    }

    private static double SumNumpy(ReadOnlySpan<double> values)
    {
        if (values.Length < 8)
        {
            double shortSum = -0.0;
            foreach (double value in values)
            {
                shortSum += value;
            }

            return shortSum;
        }

        double r0 = values[0];
        double r1 = values[1];
        double r2 = values[2];
        double r3 = values[3];
        double r4 = values[4];
        double r5 = values[5];
        double r6 = values[6];
        double r7 = values[7];
        int index = 8;
        int blockEnd = values.Length - (values.Length % 8);
        for (; index < blockEnd; index += 8)
        {
            r0 += values[index];
            r1 += values[index + 1];
            r2 += values[index + 2];
            r3 += values[index + 3];
            r4 += values[index + 4];
            r5 += values[index + 5];
            r6 += values[index + 6];
            r7 += values[index + 7];
        }

        double sum = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
        for (; index < values.Length; index++)
        {
            sum += values[index];
        }

        return sum;
    }

    private static double[] Scale(double[] values, double scale)
    {
        var output = new double[values.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = values[i] * scale;
        }

        return output;
    }

    private static void WriteOddExtension(
        ReadOnlySpan<double> input,
        int edge,
        Span<double> output)
    {
        if (edge <= 0 || output.Length != input.Length + (edge * 2))
        {
            throw new ArgumentException("Odd-extension output length did not match the requested edge.", nameof(output));
        }

        double first = input[0];
        for (int i = 0; i < edge; i++)
        {
            output[i] = (2.0 * first) - input[edge - i];
        }

        input.CopyTo(output.Slice(edge, input.Length));

        double last = input[^1];
        for (int i = 0; i < edge; i++)
        {
            output[edge + input.Length + i] = (2.0 * last) - input[input.Length - 2 - i];
        }
    }
}
