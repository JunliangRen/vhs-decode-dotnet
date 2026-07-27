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
                double theta = (twoPiFrequency * time[index]) - phase;
                double cosine = Math.Cos(theta);
                double sine = Math.Sin(theta);
                double residual = burst[index] - ((amplitude * cosine) + dc);
                double j0 = cosine;
                double j1 = amplitude * sine;
                double j3 = (-Math.Tau * time[index]) * j1;

                normal[0] += j0 * j0;
                normal[1] += j0 * j1;
                normal[2] += j0;
                normal[3] += j0 * j3;
                normal[5] += j1 * j1;
                normal[6] += j1;
                normal[7] += j1 * j3;
                normal[10] += 1.0;
                normal[11] += j3;
                normal[15] += j3 * j3;

                rhs[0] += j0 * residual;
                rhs[1] += j1 * residual;
                rhs[2] += residual;
                rhs[3] += j3 * residual;
            }

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
