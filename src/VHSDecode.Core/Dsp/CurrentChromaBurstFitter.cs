namespace VHSDecode.Core.Dsp;

internal readonly record struct CurrentChromaBurstFit(
    double PhaseRadians,
    double PhaseDegrees,
    double Amplitude,
    double Magnitude,
    double Dc,
    double FrequencyHz,
    double Center,
    double I,
    double Q);

internal static class CurrentChromaBurstFitter
{
    private const int ParameterCount = 4;
    private const int MaximumIterations = 32;
    private const double FrequencyWeight = 1e4;
    private const double MaximumPrecision = 1e-10;
    private const double DiagonalRegularization = 1e-6;

    internal static CurrentChromaBurstFit Fit(
        ReadOnlySpan<double> burst,
        int burstStart,
        ReadOnlySpan<double> burstSin,
        ReadOnlySpan<double> burstCos,
        double fscHz)
    {
        if (burst.Length == 0)
        {
            throw new ArgumentException("Current chroma burst fitting requires at least one sample.", nameof(burst));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(burstStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fscHz);
        if (burstStart > burstSin.Length - burst.Length
            || burstStart > burstCos.Length - burst.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(burstStart),
                "Burst carrier tables must cover the requested burst range.");
        }

        double iComponent = 0.0;
        double qComponent = 0.0;
        double burstSum = 0.0;
        for (int index = 0; index < burst.Length; index++)
        {
            float sample = (float)burst[index];
            burstSum += sample;
            iComponent += sample * (float)burstCos[burstStart + index];
            qComponent += sample * (float)burstSin[burstStart + index];
        }

        double phase = NormalizeSignedRadians(Math.Atan2(qComponent, iComponent));
        double dc = burstSum / burst.Length;
        double amplitude =
            (2.0 * Math.Sqrt((iComponent * iComponent) + (qComponent * qComponent)))
            / burst.Length;
        double frequency = fscHz;
        Tune(
            burst,
            burstStart,
            fscHz,
            ref amplitude,
            ref phase,
            ref dc,
            ref frequency);

        double positivePhase = PositiveModulo(phase, Math.Tau);
        double center = burstStart
            + ((burst.Length - 1) / 2.0)
            + (positivePhase * (2.0 / Math.PI));
        return new CurrentChromaBurstFit(
            phase,
            PositiveModulo(phase * (180.0 / Math.PI), 360.0),
            amplitude,
            amplitude * (burst.Length / 2.0),
            dc,
            frequency,
            center,
            iComponent,
            qComponent);
    }

    private static void Tune(
        ReadOnlySpan<double> burst,
        int burstStart,
        double fscHz,
        ref double amplitude,
        ref double phase,
        ref double dc,
        ref double frequency)
    {
        Span<double> time = burst.Length <= 256
            ? stackalloc double[burst.Length]
            : new double[burst.Length];
        Span<double> theta = burst.Length <= 256
            ? stackalloc double[burst.Length]
            : new double[burst.Length];
        Span<double> cosine = burst.Length <= 256
            ? stackalloc double[burst.Length]
            : new double[burst.Length];
        Span<double> sine = burst.Length <= 256
            ? stackalloc double[burst.Length]
            : new double[burst.Length];
        Span<double> residual = burst.Length <= 256
            ? stackalloc double[burst.Length]
            : new double[burst.Length];
        Span<double> j1 = burst.Length <= 256
            ? stackalloc double[burst.Length]
            : new double[burst.Length];
        Span<double> j3 = burst.Length <= 256
            ? stackalloc double[burst.Length]
            : new double[burst.Length];
        for (int index = 0; index < time.Length; index++)
        {
            time[index] = (index + burstStart) / (4.0 * fscHz);
        }

        Span<double> normal = stackalloc double[ParameterCount * ParameterCount];
        Span<double> rhs = stackalloc double[ParameterCount];
        Span<double> delta = stackalloc double[ParameterCount];
        double frequencyTarget = fscHz;
        for (int iteration = 0; iteration < MaximumIterations; iteration++)
        {
            normal.Clear();
            rhs.Clear();
            double twoPiFrequency = Math.Tau * frequency;
            for (int index = 0; index < burst.Length; index++)
            {
                theta[index] = (twoPiFrequency * time[index]) - phase;
            }

            for (int index = 0; index < burst.Length; index++)
            {
                cosine[index] = Math.Cos(theta[index]);
            }

            for (int index = 0; index < burst.Length; index++)
            {
                sine[index] = Math.Sin(theta[index]);
            }

            for (int index = 0; index < burst.Length; index++)
            {
                residual[index] =
                    burst[index] - ((amplitude * cosine[index]) + dc);
            }

            for (int index = 0; index < burst.Length; index++)
            {
                j1[index] = amplitude * sine[index];
                j3[index] = (-Math.Tau * time[index]) * j1[index];
            }

            normal[0] = OpenBlasHaswellDot(cosine, cosine);
            normal[1] = OpenBlasHaswellDot(cosine, j1);
            normal[2] = SumSequential(cosine);
            normal[3] = OpenBlasHaswellDot(cosine, j3);
            normal[5] = OpenBlasHaswellDot(j1, j1);
            normal[6] = SumSequential(j1);
            normal[7] = OpenBlasHaswellDot(j1, j3);
            normal[10] = burst.Length;
            normal[11] = SumSequential(j3);
            normal[15] = OpenBlasHaswellDot(j3, j3);
            rhs[0] = OpenBlasHaswellDot(cosine, residual);
            rhs[1] = OpenBlasHaswellDot(j1, residual);
            rhs[2] = SumSequential(residual);
            rhs[3] = OpenBlasHaswellDot(j3, residual);

            normal[4] = normal[1];
            normal[8] = normal[2];
            normal[9] = normal[6];
            normal[12] = normal[3];
            normal[13] = normal[7];
            normal[14] = normal[11];
            normal[15] += FrequencyWeight;
            rhs[3] += FrequencyWeight * (frequencyTarget - frequency);
            normal[0] += DiagonalRegularization;
            normal[5] += DiagonalRegularization;
            normal[10] += DiagonalRegularization;
            normal[15] += DiagonalRegularization;

            if (!TrySolve4X4(normal, rhs, delta))
            {
                break;
            }

            amplitude += delta[0];
            phase += delta[1];
            dc += delta[2];
            frequency += delta[3];
            if (Math.Abs(delta[0]) < MaximumPrecision
                && Math.Abs(delta[1]) < MaximumPrecision
                && Math.Abs(delta[2]) < MaximumPrecision
                && Math.Abs(delta[3]) < MaximumPrecision)
            {
                break;
            }
        }

        phase = NormalizeSignedRadians(phase);
    }

    private static double OpenBlasHaswellDot(
        ReadOnlySpan<double> left,
        ReadOnlySpan<double> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Dot-product spans must have the same length.");
        }

        // The pinned current oracle's Numba np.dot dispatches to the OpenBLAS
        // Haswell DDOT kernel: 16 FMA lanes followed by this reduction tree.
        Span<double> accumulators = stackalloc double[16];
        accumulators.Clear();
        int index = 0;
        int blockEnd = left.Length & ~15;
        for (; index < blockEnd; index += 16)
        {
            for (int lane = 0; lane < 16; lane++)
            {
                accumulators[lane] = Math.FusedMultiplyAdd(
                    left[index + lane],
                    right[index + lane],
                    accumulators[lane]);
            }
        }

        double pair00 = accumulators[0] + accumulators[2];
        double pair01 = accumulators[1] + accumulators[3];
        double pair10 = accumulators[4] + accumulators[6];
        double pair11 = accumulators[5] + accumulators[7];
        double pair20 = accumulators[8] + accumulators[10];
        double pair21 = accumulators[9] + accumulators[11];
        double pair30 = accumulators[12] + accumulators[14];
        double pair31 = accumulators[13] + accumulators[15];
        double lower0 = pair10 + pair00;
        double lower1 = pair11 + pair01;
        double upper0 = pair30 + pair20;
        double upper1 = pair31 + pair21;
        double dot0 = upper0 + lower0;
        double dot1 = upper1 + lower1;
        double dot = dot0 + dot1;
        for (; index < left.Length; index++)
        {
            dot = Math.FusedMultiplyAdd(left[index], right[index], dot);
        }

        return dot;
    }

    private static double SumSequential(ReadOnlySpan<double> values)
    {
        double sum = 0.0;
        for (int index = 0; index < values.Length; index++)
        {
            sum += values[index];
        }

        return sum;
    }

    private static bool TrySolve4X4(
        ReadOnlySpan<double> matrix,
        ReadOnlySpan<double> rhs,
        Span<double> solution)
    {
        Span<double> augmented = stackalloc double[ParameterCount * (ParameterCount + 1)];
        for (int row = 0; row < ParameterCount; row++)
        {
            int source = row * ParameterCount;
            int destination = row * (ParameterCount + 1);
            for (int column = 0; column < ParameterCount; column++)
            {
                augmented[destination + column] = matrix[source + column];
            }

            augmented[destination + ParameterCount] = rhs[row];
        }

        for (int pivot = 0; pivot < ParameterCount; pivot++)
        {
            int pivotRow = pivot;
            double maximum = Math.Abs(
                augmented[(pivot * (ParameterCount + 1)) + pivot]);
            for (int row = pivot + 1; row < ParameterCount; row++)
            {
                double candidate = Math.Abs(
                    augmented[(row * (ParameterCount + 1)) + pivot]);
                if (candidate > maximum)
                {
                    maximum = candidate;
                    pivotRow = row;
                }
            }

            if (pivotRow != pivot)
            {
                int left = pivot * (ParameterCount + 1);
                int right = pivotRow * (ParameterCount + 1);
                for (int column = pivot; column <= ParameterCount; column++)
                {
                    (augmented[left + column], augmented[right + column]) =
                        (augmented[right + column], augmented[left + column]);
                }
            }

            int pivotOffset = pivot * (ParameterCount + 1);
            if (Math.Abs(augmented[pivotOffset + pivot]) < 1e-12)
            {
                solution.Clear();
                return false;
            }

            for (int row = pivot + 1; row < ParameterCount; row++)
            {
                int rowOffset = row * (ParameterCount + 1);
                double factor =
                    augmented[rowOffset + pivot]
                    / augmented[pivotOffset + pivot];
                for (int column = pivot; column <= ParameterCount; column++)
                {
                    augmented[rowOffset + column] -=
                        factor * augmented[pivotOffset + column];
                }
            }
        }

        for (int row = ParameterCount - 1; row >= 0; row--)
        {
            int rowOffset = row * (ParameterCount + 1);
            double sum = 0.0;
            for (int column = row + 1; column < ParameterCount; column++)
            {
                sum += augmented[rowOffset + column] * solution[column];
            }

            solution[row] =
                (augmented[rowOffset + ParameterCount] - sum)
                / augmented[rowOffset + row];
        }

        return true;
    }

    private static double NormalizeSignedRadians(double value)
        => PositiveModulo(value + Math.PI, Math.Tau) - Math.PI;

    private static double PositiveModulo(double value, double modulus)
    {
        double remainder = value % modulus;
        return remainder < 0.0 ? remainder + modulus : remainder;
    }
}
