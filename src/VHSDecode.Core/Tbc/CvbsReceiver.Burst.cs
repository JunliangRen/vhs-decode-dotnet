using System.Numerics;

namespace VHSDecode.Core.Tbc;

internal static partial class CvbsReceiver
{
    private static readonly double[] ChromaKernel = MakeLowPass(49, 1e6 / (315e6 / 22));
    private static readonly double[] ZeroPhaseChromaKernel = Convolve(ChromaKernel);

    internal static (int? PhaseId, double? BurstIre) RestoreNtscBurst(double[] field, Gates gates, int fieldNumber)
    {
        var full = TryRestoreNtscBurst(field, gates, fieldNumber, 78, 112);
        if (full.PhaseId.HasValue)
        {
            return full;
        }
        var center = TryRestoreNtscBurst(field, gates, fieldNumber, 82, 106);
        if (center.PhaseId.HasValue)
        {
            return center;
        }
        var core = TryRestoreNtscBurst(field, gates, fieldNumber, 84, 104);
        return core.PhaseId.HasValue ? core : full;
    }

    private static (int? PhaseId, double? BurstIre) TryRestoreNtscBurst(
        double[] field, Gates gates, int fieldNumber, int start, int stop)
    {
        const int width = 910;
        int height = field.Length / width;
        start += gates.Shift;
        stop += gates.Shift;
        var phase = new double[height];
        var amplitude = new double[height];
        var noise = new double[height];
        for (int y = 20; y <= 250; y++)
        {
            ReadOnlySpan<double> line = field.AsSpan(y * width, width);
            (Complex fit, double residual) = Burst(line, start, stop);
            phase[y] = (y % 2 == 0 ? fit : -fit).Phase;
            amplitude[y] = fit.Magnitude;
            noise[y] = residual;
        }

        double median = Median(amplitude.AsSpan(20, 231).ToArray());
        if (median < 2)
        {
            return (null, median);
        }
        var good = new bool[height];
        var adjacent = new bool[height];
        var steps = new List<double>();
        for (int y = 20; y <= 250; y++)
        {
            good[y] = amplitude[y] > Math.Max(.35 * median, 3 * noise[y]) && amplitude[y] < 2 * median;
            if (y > 20 && good[y] && good[y - 1])
            {
                double step = Wrap(phase[y] - phase[y - 1]);
                if (Math.Abs(step) < .48)
                {
                    adjacent[y - 1] = adjacent[y] = true;
                    steps.Add(step);
                }
            }
        }
        if (steps.Count < 24)
        {
            return (null, median);
        }

        double velocity = Median(steps.ToArray());
        Complex seed = Complex.Zero;
        foreach (int y in Enumerable.Range(20, 231).Where(y => good[y]).Take(32))
        {
            seed += Complex.FromPolarCoordinates(1, phase[y] - y * velocity);
        }
        double current = seed.Phase;
        var path = new double[height];
        int locked = 0;
        for (int y = 0; y < height; y++)
        {
            if (y != 0)
            {
                current += velocity;
            }
            if (good[y])
            {
                double error = Wrap(phase[y] - current);
                if (Math.Abs(error) < .75)
                {
                    current += .8 * error;
                    velocity = Math.Clamp(velocity + .004 * error, -.5, .5);
                    locked++;
                }
            }
            path[y] = current;
        }
        var bypass = new bool[height];
        if (locked < .8 * 231)
        {
            int[] anchors = Enumerable.Range(20, 231).Where(y => adjacent[y]).ToArray();
            if (anchors.Length < .8 * 231)
            {
                return (null, median);
            }
            Array.Fill(path, phase[anchors[0]], 0, anchors[0] + 1);
            for (int i = 1; i < anchors.Length; i++)
            {
                int left = anchors[i - 1];
                int right = anchors[i];
                int gap = right - left;
                if (gap > 22)
                {
                    return (null, median);
                }
                double change = Wrap(phase[right] - path[left]);
                for (int y = left + 1; y <= right; y++)
                {
                    path[y] = path[left] + change * (y - left) / gap;
                    bypass[y] = y < right && Math.Abs(change) > .65 * Math.PI;
                }
            }
            Array.Fill(path, path[anchors[^1]], anchors[^1], height - anchors[^1]);
            velocity = Median(steps.ToArray());
        }

        int phaseId = (fieldNumber & 3) + 1;
        Remodulate(field, path, velocity, phaseId, gates.Shift, bypass);
        return (phaseId, median);
    }

    private static (Complex, double) Burst(ReadOnlySpan<double> line, int start, int stop)
    {
        // Exact quarter-cycle samples and a local DC/slope term keep a tilted
        // porch from being interpreted as a rotated colour burst.
        double mean = Mean(line[start..stop]);
        double center = (start + stop - 1) / 2.0;
        double cov = 0, variance = 0;
        for (int x = start; x < stop; x++)
        {
            double t = x - center;
            cov += t * (line[x] - mean);
            variance += t * t;
        }
        double slope = cov / variance;
        double a = 0, b = 0, na = 0, nb = 0;
        for (int x = start; x < stop; x++)
        {
            double value = line[x] - mean - (x - center) * slope;
            if ((x & 1) == 0)
            {
                a += (x & 2) == 0 ? value : -value;
                na++;
            }
            else
            {
                b += (x & 2) == 0 ? -value : value;
                nb++;
            }
        }
        Complex fit = new(a / na, b / nb);
        double error = 0;
        for (int x = start; x < stop; x++)
        {
            double predicted = (fit * Quadrature(x)).Real + mean + (x - center) * slope;
            error += Math.Pow(line[x] - predicted, 2);
        }
        return (fit, Math.Sqrt(error / (stop - start)));
    }

    private static void Remodulate(double[] field, double[] phase, double velocity, int phaseId, int gate, bool[] bypass)
    {
        const int width = 910;
        int height = phase.Length;
        var baseband = new Complex[field.Length];
        var carrier = new Complex[width];
        Complex sampleStep = Complex.FromPolarCoordinates(1, Math.PI / 2 + velocity / width);
        carrier[0] = Complex.FromPolarCoordinates(1, -velocity / width * (94 + gate));
        for (int x = 1; x < width; x++)
        {
            carrier[x] = carrier[x - 1] * sampleStep;
        }

        int radius = ZeroPhaseChromaKernel.Length / 2;
        var mixed = new Complex[width + 2 * radius];
        for (int y = 0; y < height; y++)
        {
            Complex linePhase = Complex.FromPolarCoordinates(y % 2 == 0 ? 1 : -1, phase[y]);
            for (int x = 0; x < width; x++)
            {
                mixed[x + radius] = 2 * field[y * width + x] * Complex.Conjugate(carrier[x] * linePhase);
            }
            for (int x = 1; x <= radius; x++)
            {
                mixed[radius - x] = 2 * mixed[radius] - mixed[radius + x];
                mixed[radius + width - 1 + x] = 2 * mixed[radius + width - 1] - mixed[radius + width - 1 - x];
            }
            for (int x = 0; x < width; x++)
            {
                double re = 0, im = 0;
                for (int k = 0; k < ZeroPhaseChromaKernel.Length; k++)
                {
                    Complex z = mixed[x + k];
                    re += z.Real * ZeroPhaseChromaKernel[k];
                    im += z.Imaginary * ZeroPhaseChromaKernel[k];
                }
                baseband[y * width + x] = new(re, im);
            }
        }
        Complex target = Complex.FromPolarCoordinates(1, Math.PI * (147.0 / 180 + (phaseId is 2 or 3 ? 1 : 0)));
        for (int y = 0; y < height; y++)
        {
            if (bypass[y])
            {
                continue;
            }
            int lo = Math.Max(0, y - 1);
            int hi = Math.Min(height - 1, y + 1);
            double left = bypass[lo] ? 0 : .25;
            double right = bypass[hi] ? 0 : .25;
            Complex source = Complex.FromPolarCoordinates(y % 2 == 0 ? 1 : -1, phase[y]);
            Complex dest = (y % 2 == 0 ? 1 : -1) * target;
            CorrectRange(72 + gate, 118 + gate);
            CorrectRange(134 + gate, 894 + gate);

            void CorrectRange(int start, int stop)
            {
                start = Math.Max(0, start);
                stop = Math.Min(width, stop);
                for (int x = start; x < stop; x++)
                {
                    int i = y * width + x;
                    Complex color = (left * baseband[lo * width + x] + .5 * baseband[i]
                        + right * baseband[hi * width + x]) / (left + .5 + right);
                    int edge = Math.Min(x - start, stop - x - 1);
                    double weight = edge >= 4 ? 1 : .5 - .5 * Math.Cos(edge * Math.PI / 4);
                    field[i] += weight * (color * (Quadrature(x) * dest - carrier[x] * source)).Real;
                }
            }
        }
    }

    internal static double[] MakeLowPass(int count, double cutoff)
    {
        var kernel = new double[count];
        for (int i = 0; i < count; i++)
        {
            int k = i - count / 2;
            double sinc = k == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * k) / (Math.PI * k);
            kernel[i] = sinc * (.54 - .46 * Math.Cos(2 * Math.PI * i / (count - 1)));
        }
        double sum = kernel.Sum();
        for (int i = 0; i < count; i++)
        {
            kernel[i] /= sum;
        }
        return kernel;
    }

    internal static double[] Convolve(double[] kernel)
    {
        var result = new double[kernel.Length * 2 - 1];
        for (int i = 0; i < kernel.Length; i++)
        {
            for (int j = 0; j < kernel.Length; j++)
            {
                result[i + j] += kernel[i] * kernel[j];
            }
        }
        return result;
    }

    private static Complex Quadrature(int i) => (i & 3) switch
    {
        0 => Complex.One,
        1 => Complex.ImaginaryOne,
        2 => -Complex.One,
        _ => -Complex.ImaginaryOne
    };

    private static double Wrap(double x) => Math.IEEERemainder(x, 2 * Math.PI);
}
