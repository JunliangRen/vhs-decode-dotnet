namespace VHSDecode.Core.Decode;

internal sealed partial class CvbsVhdLumaRestoration
{
    private sealed record Ripple(double Frequency, double Epoch, double[][] Coefficients);
    private sealed record ReferenceCorrection(double[,,] Samples, double[][] Coefficients)
    {
        internal (double, double) At(int field, int line)
        {
            double index = Math.Clamp((line - FirstRow) / (double)RowStep, 0, FitRows.Length - 1);
            int first = (int)index;
            int last = Math.Min(first + 1, FitRows.Length - 1);
            double mix = index - first;
            return (Samples[field, first, 0] + mix * (Samples[field, last, 0] - Samples[field, first, 0]),
                    Samples[field, first, 1] + mix * (Samples[field, last, 1] - Samples[field, first, 1]));
        }
    }

    private static Ripple? FitRipple(double[,] luma, double[] times)
    {
        int count = times.Length;
        int columns = luma.GetLength(1);
        double epoch = times.Average();
        double[,] trend = SmoothColumns(luma);
        double bestScore = 0;
        double bestFrequency = 0;
        double[]? best = null;
        double variance = 0;
        for (int f = 0; f < count; f++)
        {
            for (int c = 0; c < columns; c++)
            {
                variance += Math.Pow(luma[f, c] - trend[f, c], 2);
            }
        }
        if (variance < 1e-6)
        {
            return null;
        }

        void Search(double first, double last, double step)
        {
            for (double hz = first; hz <= last + 1e-9; hz += step)
            {
                (NormalEquation[] outer, NormalEquation[] center) = BuildEquations(luma, trend,
                    (field, column, x) => FillHarmonic(x, 0, hz, times[field] + ScanOffset(column) - epoch, trend[field, column]),
                    4);
                NormalEquation all = Combine(outer, 4);
                all.Add(Combine(center, 4), 1);
                double[] candidate = all.Solve(.000001);
                double score = all.Improvement(candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestFrequency = hz;
                    best = candidate;
                }
            }
        }
        Search(98, 102, .1);
        if (best is null || bestScore < .15 * variance)
        {
            return null;
        }
        Search(Math.Max(98, bestFrequency - .09), Math.Min(102, bestFrequency + .09), .01);
        if (best is null || !WithinLimits(best))
        {
            return null;
        }

        (NormalEquation[] outerFull, NormalEquation[] centerFull) = BuildEquations(luma, trend, (f, c, x) =>
        {
            double scan = times[f] + ScanOffset(c) - epoch;
            FillHarmonic(x, 0, bestFrequency, scan, trend[f, c]);
            FillHarmonic(x, 4, 4.7, scan, trend[f, c]);
        }, 8);
        (double[][] fit, double[] validation, _) = LocalFit(outerFull, centerFull, FieldRate * .45, .04, 4);
        if (validation.Average() < .15 || validation.Min() < -.10 || fit.Any(x => !WithinLimits(x)))
        {
            return null;
        }
        return new Ripple(bestFrequency, epoch, fit);
    }

    private static double[,] SmoothColumns(double[,] input)
    {
        int fields = input.GetLength(0);
        int columns = input.GetLength(1);
        var result = new double[fields, columns];
        double[] sequence = new double[fields];
        for (int c = 0; c < columns; c++)
        {
            for (int f = 0; f < fields; f++)
            {
                sequence[f] = input[f, c];
            }
            double[] values = Smooth(sequence);
            for (int f = 0; f < fields; f++)
            {
                result[f, c] = values[f];
            }
        }
        return result;
    }

    private static double[] Smooth(double[] values)
    {
        // 21-field quadratic Savitzky-Golay, with a fitted polynomial at edges.
        const int radius = 10;
        const double axisVariance = 770.0 / 21;
        var result = new double[values.Length];
        for (int f = 0; f < values.Length; f++)
        {
            int center = Math.Clamp(f, radius, values.Length - radius - 1);
            double offset = f - center;
            double mean = 0, linear = 0, quadratic = 0;
            for (int i = -radius; i <= radius; i++)
            {
                double value = values[center + i];
                mean += value;
                linear += i * value;
                quadratic += (i * i - axisVariance) * value;
            }
            result[f] = mean / 21 + offset * linear / 770
                + (offset * offset - axisVariance) * quadratic / (50666 - 770 * axisVariance);
        }
        return result;
    }

    private static void FillHarmonic(double[] vector, int start, double rate, double time, double level)
    {
        (double sin, double cos) = Math.SinCos(2 * Math.PI * rate * time);
        vector[start] = cos;
        vector[start + 1] = sin;
        vector[start + 2] = cos * level;
        vector[start + 3] = sin * level;
    }

    private static bool WithinLimits(double[] v)
    {
        double offset = Math.Sqrt(v[0] * v[0] + v[1] * v[1]);
        double gain = Math.Sqrt(v[2] * v[2] + v[3] * v[3]);
        if (v.Length > 4)
        {
            offset += Math.Sqrt(v[4] * v[4] + v[5] * v[5]);
            gain += Math.Sqrt(v[6] * v[6] + v[7] * v[7]);
        }
        return double.IsFinite(offset) && double.IsFinite(gain) && offset <= 5 && gain <= .15;
    }

    private static ReferenceCorrection? FitReference(double[,] luma, double[,,] raw)
    {
        int fields = luma.GetLength(0);
        var band = new double[fields, FitRows.Length, 2];
        var input = new double[fields];
        for (int row = 0; row < FitRows.Length; row++)
        {
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < fields; i++)
                {
                    input[i] = raw[i, row, c];
                }
                double[] filtered = VHSDecode.Core.Dsp.SosFilter.ApplyForwardBackward(ReferenceBand, input);
                double mean = filtered.Average();
                for (int i = 0; i < fields; i++)
                {
                    band[i, row, c] = filtered[i] - mean;
                }
            }
        }

        double[,] trend = SmoothColumns(luma);
        (NormalEquation[] outer, NormalEquation[] center) = BuildEquations(luma, trend, (f, c, v) =>
        {
            int y = c / 3;
            double level = (trend[f, c] - 30) / 20;
            v[0] = band[f, y, 0];
            v[1] = band[f, y, 1];
            v[2] = v[0] * level;
            v[3] = v[1] * level;
        }, 4);
        (double[][] coefficients, double[] scores, double[] thirds) = LocalFit(outer, center, .4 * FieldRate, .04, 0);
        if (scores.Average() < .03 || scores.Min() < -.05 || thirds.Max() < .05)
        {
            return null;
        }
        for (int i = 0; i < fields; i++)
        {
            double p = Math.Clamp(3.0 * (i + .5) / fields - .5, 0, 2);
            int lo = (int)p;
            double strength = thirds[lo] + (p - lo) * (thirds[Math.Min(lo + 1, 2)] - thirds[lo]);
            for (int c = 0; c < coefficients[i].Length; c++)
            {
                coefficients[i][c] *= Math.Clamp(strength / .10, 0, 1);
            }
        }
        return new ReferenceCorrection(band, coefficients);
    }

    private static (NormalEquation[] Outer, NormalEquation[] Center) BuildEquations(
        double[,] luma, double[,] trend, Action<int, int, double[]> fill, int features)
    {
        int fields = luma.GetLength(0);
        int columns = luma.GetLength(1);
        NormalEquation[] outer = Enumerable.Range(0, fields).Select(_ => new NormalEquation(features)).ToArray();
        NormalEquation[] center = Enumerable.Range(0, fields).Select(_ => new NormalEquation(features)).ToArray();
        var vector = new double[features];
        var design = new double[fields, features];
        for (int column = 0; column < columns; column++)
        {
            for (int field = 0; field < fields; field++)
            {
                fill(field, column, vector);
                for (int c = 0; c < features; c++)
                {
                    design[field, c] = vector[c];
                }
            }

            double[,] smooth = SmoothColumns(design);
            for (int field = 0; field < fields; field++)
            {
                for (int c = 0; c < features; c++)
                {
                    vector[c] = design[field, c] - smooth[field, c];
                }
                (column % 3 == 1 ? center[field] : outer[field]).Observe(vector, luma[field, column] - trend[field, column]);
            }
        }
        return (outer, center);
    }

    private static NormalEquation Combine(NormalEquation[] fields, int features)
    {
        var all = new NormalEquation(features);
        foreach (NormalEquation field in fields)
        {
            all.Add(field, 1);
        }
        return all;
    }

    private static (double[][] Coefficients, double[] Scores, double[] Thirds) LocalFit(
        NormalEquation[] outer, NormalEquation[] center, double width, double penalty, int fixedFrom)
    {
        int fields = outer.Length;
        int features = outer[0].Features;
        var holdout = new double[8];
        var energy = new double[8];
        var thirds = new double[3];
        var thirdEnergy = new double[3];
        var coefficients = new double[fields][];
        double[] global = Combine(outer, features).Solve(.001);
        var grouped = new double[8][];
        for (int group = 0; group < grouped.Length; group++)
        {
            var train = new NormalEquation(features);
            for (int i = 0; i < fields; i++)
            {
                if (i / 8 % 8 != group)
                {
                    train.Add(outer[i], 1);
                }
            }
            grouped[group] = train.Solve(.001);
        }

        for (int target = 0; target < fields; target++)
        {
            int group = target / 8 % 8;
            var all = new NormalEquation(features);
            var held = new NormalEquation(features);
            int span = (int)Math.Ceiling(width * 3);
            for (int i = Math.Max(0, target - span); i <= Math.Min(fields - 1, target + span); i++)
            {
                double weight = Math.Exp(-.5 * Math.Pow((i - target) / width, 2));
                all.Add(outer[i], weight);
                if (i / 8 % 8 != group)
                {
                    held.Add(outer[i], weight);
                }
            }
            coefficients[target] = all.Solve(penalty, global, fixedFrom);
            double[] prediction = held.Solve(penalty, grouped[group], fixedFrom);
            double improvement = center[target].Improvement(prediction);
            int third = Math.Min(2, target * 3 / fields);
            holdout[group] += improvement;
            energy[group] += center[target].Energy;
            thirds[third] += improvement;
            thirdEnergy[third] += center[target].Energy;
        }
        return (coefficients, holdout.Zip(energy, (a, b) => a / Math.Max(b, 1e-12)).ToArray(),
            thirds.Zip(thirdEnergy, (a, b) => a / Math.Max(b, 1e-12)).ToArray());
    }

    private sealed class NormalEquation(int features)
    {
        private readonly double[,] _matrix = new double[features, features];
        private readonly double[] _rhs = new double[features];

        internal int Features => features;
        internal double Energy { get; private set; }

        internal void Observe(double[] vector, double target)
        {
            for (int i = 0; i < features; i++)
            {
                _rhs[i] += vector[i] * target;
                for (int j = 0; j <= i; j++)
                {
                    _matrix[i, j] += vector[i] * vector[j];
                }
            }
            Energy += target * target;
        }

        internal void Add(NormalEquation source, double weight)
        {
            for (int i = 0; i < features; i++)
            {
                _rhs[i] += weight * source._rhs[i];
                for (int j = 0; j <= i; j++)
                {
                    _matrix[i, j] += weight * source._matrix[i, j];
                }
            }
            Energy += weight * source.Energy;
        }

        internal double Improvement(double[] coeff)
        {
            double score = 0;
            for (int i = 0; i < features; i++)
            {
                score += 2 * _rhs[i] * coeff[i] - _matrix[i, i] * coeff[i] * coeff[i];
                for (int j = 0; j < i; j++)
                {
                    score -= 2 * _matrix[i, j] * coeff[i] * coeff[j];
                }
            }
            return score;
        }

        internal double[] Solve(double regularization, double[]? prior = null, int fixedFrom = 0)
        {
            int size = fixedFrom > 0 ? fixedFrom : features;
            var factor = new double[size, size];
            var result = prior is null ? new double[features] : (double[])prior.Clone();
            for (int i = 0; i < size; i++)
            {
                double ridge = Math.Max(_matrix[i, i], 1e-12) * regularization;
                double rhs = _rhs[i] + ridge * (prior?[i] ?? 0);
                for (int j = size; j < features; j++)
                {
                    rhs -= _matrix[j, i] * result[j];
                }
                for (int j = 0; j <= i; j++)
                {
                    double value = _matrix[i, j] + (i == j ? ridge : 0);
                    for (int k = 0; k < j; k++)
                    {
                        value -= factor[i, k] * factor[j, k];
                    }
                    factor[i, j] = i == j ? Math.Sqrt(Math.Max(value, 1e-18)) : value / factor[j, j];
                }

                for (int j = 0; j < i; j++)
                {
                    rhs -= factor[i, j] * result[j];
                }
                result[i] = rhs / factor[i, i];
            }
            for (int i = size - 1; i >= 0; i--)
            {
                for (int j = i + 1; j < size; j++)
                {
                    result[i] -= factor[j, i] * result[j];
                }
                result[i] /= factor[i, i];
            }
            return result;
        }
    }
}
