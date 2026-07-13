namespace VHSDecode.Core.Dsp;

public static class IirFilter
{
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

        double[] extended = edge == 0 ? input.ToArray() : OddExtension(input, Math.Min(edge, input.Length - 1));
        double[] zi = SteadyStateInitialConditions(numerator, denominator);
        double[] firstZi = Scale(zi, extended[0]);
        double[] forward = ApplyForward(numerator, denominator, extended, firstZi);
        Array.Reverse(forward);
        double[] secondZi = Scale(zi, forward[0]);
        double[] backward = ApplyForward(numerator, denominator, forward, secondZi);
        Array.Reverse(backward);

        if (edge == 0)
        {
            return backward;
        }

        int actualEdge = (extended.Length - input.Length) / 2;
        var trimmed = new double[input.Length];
        Array.Copy(backward, actualEdge, trimmed, 0, trimmed.Length);
        return trimmed;
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

        double[] numerator = filter.Numerator.ToArray();
        double[] denominator = filter.Denominator.ToArray();
        if (a0 != 1.0)
        {
            for (int i = 0; i < numerator.Length; i++)
            {
                numerator[i] /= a0;
            }

            for (int i = 0; i < denominator.Length; i++)
            {
                denominator[i] /= a0;
            }
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
        var output = new double[input.Length];
        for (int sample = 0; sample < input.Length; sample++)
        {
            double x = input[sample];
            double y = (numerator[0] * x) + (stateLength > 0 ? workingState[0] : 0.0);
            for (int i = 1; i < stateLength; i++)
            {
                double b = i < numerator.Length ? numerator[i] : 0.0;
                double a = i < denominator.Length ? denominator[i] : 0.0;
                workingState[i - 1] = (b * x) + workingState[i] - (a * y);
            }

            if (stateLength > 0)
            {
                int i = stateLength;
                double b = i < numerator.Length ? numerator[i] : 0.0;
                double a = i < denominator.Length ? denominator[i] : 0.0;
                workingState[stateLength - 1] = (b * x) - (a * y);
            }

            output[sample] = y;
        }

        return output;
    }

    private static double[] SteadyStateInitialConditions(double[] numerator, double[] denominator)
    {
        int stateLength = Math.Max(numerator.Length, denominator.Length) - 1;
        if (stateLength == 0)
        {
            return [];
        }

        int coefficientCount = stateLength + 1;
        var paddedNumerator = new double[coefficientCount];
        var paddedDenominator = new double[coefficientCount];
        numerator.CopyTo(paddedNumerator, 0);
        denominator.CopyTo(paddedDenominator, 0);

        double numeratorSum = SumNumpy(paddedNumerator);
        double denominatorSum = SumNumpy(paddedDenominator);
        if (denominatorSum == 0.0)
        {
            throw new ArgumentException("IIR filter has a pole at z = 1.", nameof(denominator));
        }

        double steadyOutput = numeratorSum / denominatorSum;
        var state = new double[stateLength];
        double cumulative = 0.0;
        for (int i = coefficientCount - 1; i >= 1; i--)
        {
            cumulative += paddedNumerator[i] - (steadyOutput * paddedDenominator[i]);
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

    private static double[] OddExtension(ReadOnlySpan<double> input, int edge)
    {
        if (edge <= 0)
        {
            return input.ToArray();
        }

        var output = new double[input.Length + (edge * 2)];
        double first = input[0];
        for (int i = 0; i < edge; i++)
        {
            output[i] = (2.0 * first) - input[edge - i];
        }

        input.CopyTo(output.AsSpan(edge, input.Length));

        double last = input[^1];
        for (int i = 0; i < edge; i++)
        {
            output[edge + input.Length + i] = (2.0 * last) - input[input.Length - 2 - i];
        }

        return output;
    }
}
