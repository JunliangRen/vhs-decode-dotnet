using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Tbc;

internal static partial class CvbsReceiver
{
    internal sealed record Sync(double Threshold, double Tip, double Blank, IReadOnlyList<Pulse> Pulses);

    internal static Sync? AcquireSync(double[] video, double rate, double lineLength,
        Func<IReadOnlyList<Pulse>, double?>? findFirstBlanking = null)
    {
        int samplesPerUsec = (int)Math.Round(rate / 1e6);
        var integral = new double[video.Length];
        double center = video.Average();
        double sum = 0;
        for (int i = 0; i < video.Length; i++)
        {
            sum += (video[i] - center) / rate;
            integral[i] = sum;
        }

        Sync? best = null;
        double bestScore = 0;
        double bestBlanking = double.PositiveInfinity;
        var corrected = new double[video.Length];
        var quantiles = new double[(video.Length + 15) / 16];
        foreach (int corner in new[] { 0, 2000, 4000, 6000, 8000, 10000 })
        {
            double[] candidate = corner == 0 ? video : corrected;
            if (corner != 0)
            {
                for (int i = 0; i < video.Length; i++)
                {
                    candidate[i] = video[i] + 2 * Math.PI * corner * integral[i];
                }
            }

            for (int i = 0; i < quantiles.Length; i++)
            {
                quantiles[i] = candidate[i * 16];
            }

            Array.Sort(quantiles);
            double low = quantiles[quantiles.Length / 25];
            double middle = quantiles[quantiles.Length / 2];
            if (middle - low < 1e-8)
            {
                continue;
            }

            var tips = new List<double>();
            var porches = new List<double>();
            foreach (Pulse pulse in PulseDetection.FindPulses(candidate, (low + middle) / 2))
            {
                if (pulse.Length >= 3.2 * samplesPerUsec && pulse.Length <= 6.2 * samplesPerUsec
                    && pulse.Start + 10 * samplesPerUsec < video.Length)
                {
                    tips.Add(Mean(candidate.AsSpan(pulse.Start + samplesPerUsec, 2 * samplesPerUsec)));
                    porches.Add(Mean(candidate.AsSpan(pulse.Start + (int)(8.2 * samplesPerUsec),
                        Math.Max(1, (int)(.8 * samplesPerUsec)))));
                }
            }

            if (tips.Count < 8)
            {
                continue;
            }

            double tip = Median(tips.ToArray());
            double blank = Median(porches.ToArray());
            if (blank <= tip)
            {
                continue;
            }

            double threshold = (tip + blank) / 2;
            IReadOnlyList<Pulse> pulses = PulseDetection.FindPulses(candidate, threshold);
            int broadStart = -video.Length;
            int broadPairs = 0;
            int normal = 0;
            int oversize = 0;
            foreach (Pulse pulse in pulses)
            {
                double width = pulse.Length / (double)samplesPerUsec;
                if (width is > 22 and < 30)
                {
                    if (Math.Abs(pulse.Start - broadStart - lineLength / 2) < 2 * samplesPerUsec)
                    {
                        broadPairs++;
                    }
                    broadStart = pulse.Start;
                }
                normal += width is > 3.5 and < 6 ? 1 : 0;
                oversize += width > 40 ? 1 : 0;
            }

            double score = 20 * broadPairs + .05 * normal - 10 * oversize;
            if (findFirstBlanking is null && corner == 0 && broadPairs >= 5)
            {
                return new(threshold, tip, blank, pulses);
            }
            double blanking = findFirstBlanking?.Invoke(pulses) ?? double.PositiveInfinity;
            // At acquisition, a complete earlier blanking group takes precedence
            // over a better-looking separator that only finds a later field.
            if (blanking < bestBlanking - lineLength / 2
                || (blanking <= bestBlanking + lineLength / 2 && score > bestScore))
            {
                best = new(threshold, tip, blank, pulses);
                bestScore = score;
                bestBlanking = blanking;
            }
        }
        return best;
    }

    internal static double[] RefineHSync(double[] video, double[] locations, double inputRate)
    {
        double usec = inputRate / 1e6;
        int first = 18;
        int last = locations.Length - 12;
        if (last <= first + 80)
        {
            return locations;
        }

        var leading = new double[last - first];
        for (int y = first; y < last; y++)
        {
            int at = (int)Math.Round(locations[y]);
            leading[y - first] = FindEdge(video, at - (int)(2 * usec), at + (int)(2 * usec), -1);
        }
        double[] clock = FitLineClock(locations, leading, first);
        var trailing = new double[leading.Length];
        var delays = new List<double>();
        for (int y = first; y < last; y++)
        {
            int at = (int)Math.Round(clock[y]);
            double edge = FindEdge(video, at + (int)(3.5 * usec), at + (int)(7.25 * usec), 1);
            double delay = edge - clock[y];
            if (delay > 4.25 * usec && delay < 7 * usec)
            {
                delays.Add(delay);
                trailing[y - first] = edge - 4.7 * usec;
            }
            else
            {
                trailing[y - first] = double.NaN;
            }
        }
        delays.Sort();
        return delays.Count > .85 * leading.Length && delays[(int)(.9 * delays.Count)] > 5.325 * usec
            ? FitLineClock(clock, trailing, first)
            : clock;
    }

    private static double[] FitLineClock(double[] fallback, double[] edges, int firstLine)
    {
        var good = new bool[edges.Length];
        for (int i = 0; i < edges.Length; i++)
        {
            good[i] = double.IsFinite(edges[i]);
        }
        double origin = 0;
        double slope = 0;
        double[] residuals = new double[edges.Length];
        for (int iteration = 0; iteration < 5; iteration++)
        {
            int count = 0;
            double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
            for (int i = 0; i < edges.Length; i++)
            {
                if (good[i])
                {
                    count++;
                    sumX += i;
                    sumY += edges[i];
                    sumXX += i * i;
                    sumXY += i * edges[i];
                }
            }
            if (count < 32)
            {
                return fallback;
            }
            slope = (sumXY - sumX * sumY / count) / (sumXX - sumX * sumX / count);
            origin = (sumY - sumX * slope) / count;
            var errors = new double[count];
            int n = 0;
            for (int i = 0; i < edges.Length; i++)
            {
                residuals[i] = edges[i] - origin - slope * i;
                if (good[i])
                {
                    errors[n++] = residuals[i];
                }
            }
            double center = Median(errors);
            for (int i = 0; i < errors.Length; i++)
            {
                errors[i] = Math.Abs(errors[i] - center);
            }
            double limit = Math.Clamp(5.93 * Median(errors), 8, 24);
            for (int i = 0; i < good.Length; i++)
            {
                good[i] &= Math.Abs(residuals[i] - center) < limit;
            }
        }

        int[] anchors = Enumerable.Range(0, good.Length).Where(i => good[i] && Math.Abs(residuals[i]) < 16).ToArray();
        if (anchors.Length < 32 || !double.IsFinite(slope) || slope <= 0)
        {
            return fallback;
        }
        int next = 0;
        var curve = new double[fallback.Length];
        for (int y = 0; y < curve.Length; y++)
        {
            int i = y - firstLine;
            while (next < anchors.Length - 1 && anchors[next] < i)
            {
                next++;
            }
            int right = anchors[next];
            int left = anchors[Math.Max(0, next - 1)];
            double blend = right == left ? 0 : Math.Clamp((i - left) / (double)(right - left), 0, 1);
            curve[y] = residuals[left] + blend * (residuals[right] - residuals[left]);
        }

        var result = new double[curve.Length];
        Span<double> window = stackalloc double[5];
        for (int y = 0; y < curve.Length; y++)
        {
            for (int i = 0; i < window.Length; i++)
            {
                window[i] = curve[Math.Clamp(y + i - 2, 0, curve.Length - 1)];
            }
            result[y] = origin + slope * (y - firstLine) + Median(window);
        }
        return result;
    }

    internal static double? TrackLineZero(double[] video, double expected, double period, double rate,
        int lineCount, double syncSpan)
    {
        double usec = rate / 1e6;
        if (expected < 0 || expected > 40 * period
            || expected + (lineCount - 12) * period + 10 * usec >= video.Length)
        {
            return null;
        }

        int radius = (int)(period * .18);
        int width = (int)(4.7 * usec);
        var profile = new double[(2 * radius) + width + (int)(5 * usec)];
        int used = 0;
        for (int line = 20; line < lineCount - 12; line++)
        {
            int start = (int)Math.Round(expected + line * period) - radius;
            if (start < 0 || start + profile.Length > video.Length)
            {
                continue;
            }
            for (int x = 0; x < profile.Length; x++)
            {
                profile[x] += video[start + x];
            }
            used++;
        }
        if (used < 160)
        {
            return null;
        }

        double best = 0;
        int peak = -1;
        int lag = Math.Max(2, (int)(.3 * usec));
        for (int x = lag; x < 2 * radius - lag; x++)
        {
            double falling = profile[x - lag] - profile[x + lag];
            double rising = profile[x + width + lag] - profile[x + width - lag];
            double score = Math.Min(falling, rising);
            if (score > best)
            {
                best = score;
                peak = x;
            }
        }
        if (peak < 0 || best / used < Math.Max(100, .10 * syncSpan))
        {
            return null;
        }
        int n = (int)usec;
        int valid = 0;
        for (int line = 20; line < lineCount - 12; line++)
        {
            int start = (int)Math.Round(expected + line * period) + peak - radius;
            if (start >= 0 && start + 10 * n < video.Length)
            {
                double tip = Mean(video.AsSpan(start + n, 2 * n));
                double back = Mean(video.AsSpan(start + 8 * n, n));
                valid += back - tip > Math.Max(150, .35 * syncSpan) ? 1 : 0;
            }
        }
        return valid >= .75 * (lineCount - 32) ? expected + peak - radius : null;
    }

    private static double FindEdge(double[] input, int first, int last, int direction)
    {
        first = Math.Max(2, first);
        last = Math.Min(input.Length - 2, last);
        int peak = -1;
        double strength = 0;
        for (int i = first; i < last; i++)
        {
            double slope = direction * (input[i + 1] - input[i - 1]);
            if (slope > strength)
            {
                strength = slope;
                peak = i;
            }
        }
        if (peak <= first || peak >= last - 1)
        {
            return double.NaN;
        }
        double left = direction * (input[peak] - input[peak - 2]);
        double right = direction * (input[peak + 2] - input[peak]);
        double curvature = left + right - 2 * strength;
        return peak + (curvature < 0 ? Math.Clamp(.5 * (left - right) / curvature, -.5, .5) : 0);
    }

    internal readonly record struct Gates(int SyncStart, int SyncStop, int BackStart, int BackStop, int FrontStart, int FrontStop, int Shift);

    internal static Gates MeasureGates(double[] data, TbcFrameSpec spec)
    {
        int width = spec.OutputLineLength;
        bool pal = spec.System == "PAL";
        int nominalEdge = pal ? 83 : 68;
        int lo = pal ? 60 : 54;
        int hi = pal ? 118 : 94;
        var waveform = new double[hi + 3];
        for (int y = 20; y < spec.OutputLineCount - 12; y++)
        {
            for (int x = lo - 3; x < waveform.Length; x++)
            {
                waveform[x] += data[y * width + x];
            }
        }

        double best = 0;
        int edge = nominalEdge;
        for (int x = lo; x < hi; x++)
        {
            double d = waveform[x+1] + waveform[x+2] - waveform[x-1] - waveform[x-2];
            if (d > best)
            {
                best = d;
                edge = x;
            }
        }
        int shift = edge - nominalEdge;
        int frontStop = width - (pal ? 4 : 3) + shift;
        return new((pal ? 25 : 18) + shift, (pal ? 65 : 58) + shift,
            (pal ? 151 : 120) + shift, (pal ? 167 : 132) + shift,
            frontStop - (pal ? 16 : 12), frontStop, shift);
    }

    internal static double? EstimateCorner(double[] data, TbcFrameSpec spec, Gates gate)
    {
        Span<double> measured = stackalloc double[10];
        int width = spec.OutputLineLength;
        int first = spec.System == "PAL" ? 24 + gate.Shift : gate.SyncStart;
        for (int row = 20; row < Math.Min(250, spec.OutputLineCount - 12); row++)
        {
            for (int i = 0; i < measured.Length; i++)
            {
                measured[i] += Mean(data.AsSpan(row * width + first + i * 4, 4));
            }
        }
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int i = 0; i < measured.Length; i++)
        {
            if (measured[i] >= 0 || !double.IsFinite(measured[i]))
            {
                return null;
            }
            min = Math.Min(min, measured[i]);
            max = Math.Max(max, measured[i]);
        }
        if (max - min < .005 * -min)
        {
            return 0;
        }
        double mean = 0;
        double cov = 0;
        for (int i = 0; i < measured.Length; i++)
        {
            measured[i] = Math.Log(-measured[i]);
            mean += measured[i] / measured.Length;
            cov += (i - 4.5) * measured[i];
        }

        double slope = cov / 82.5;
        double residual = 0;
        double variance = 0;
        for (int i = 0; i < measured.Length; i++)
        {
            residual += Math.Pow(measured[i] - mean - slope * (i - 4.5), 2);
            variance += Math.Pow(measured[i] - mean, 2);
        }
        double corner = -slope * spec.OutputSampleRateHz / (8 * Math.PI);
        return corner is >= 0 and <= 15000 && residual < .05 * variance ? corner : null;
    }

    internal static float[] MeasureBlanking(double[] data, TbcFrameSpec spec, Gates gate)
    {
        int width = spec.OutputLineLength;
        var result = new float[spec.OutputLineCount * 2];
        for (int y = 0; y < spec.OutputLineCount; y++)
        {
            ReadOnlySpan<double> row = data.AsSpan(y * width, width);
            result[2 * y] = (float)Mean(row[gate.SyncStart..gate.SyncStop]);
            result[2 * y + 1] = (float)Mean(row[gate.BackStart..gate.BackStop]);
        }
        return result;
    }

    internal static bool RestoreLevels(double[] data, TbcFrameSpec spec, Gates gate, double corner,
        VideoOutputConverter converter)
    {
        int width = spec.OutputLineLength;
        int height = spec.OutputLineCount;
        double factor = 2 * Math.PI * corner / spec.OutputSampleRateHz;
        var blank = new double[height];
        var span = new double[height];
        var residual = new double[height];
        var frontValid = new bool[height];
        var restored = new double[data.Length];
        bool canMeasureFront = gate.FrontStop <= width && corner > 0;
        for (int y = 0; y < height; y++)
        {
            Span<double> line = restored.AsSpan(y * width, width);
            double integral = 0;
            for (int x = 0; x < width; x++)
            {
                double sample = data[y * width + x];
                integral += sample;
                line[x] = sample + factor * (integral - .5 * sample);
            }
            blank[y] = Mean(line[gate.BackStart..gate.BackStop]);
            span[y] = blank[y] - Mean(line[gate.SyncStart..gate.SyncStop]);
        }

        double typical = Median(span.AsSpan(20, height - 32).ToArray());
        if (!double.IsFinite(typical) || typical <= 1e-8)
        {
            return false;
        }
        var frontResiduals = new List<double>(height);
        var lines = new List<int>(height);
        for (int y = 20; y < height - 12; y++)
        {
            if (span[y] <= .6 * typical || span[y] >= 1.5 * typical)
            {
                continue;
            }
            lines.Add(y);
            if (!canMeasureFront)
            {
                continue;
            }
            ReadOnlySpan<double> front = restored.AsSpan(y * width + gate.FrontStart, gate.FrontStop - gate.FrontStart);
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            for (int x = 0; x < front.Length; x += 4)
            {
                double level = Mean(front.Slice(x, 4));
                min = Math.Min(min, level);
                max = Math.Max(max, level);
            }
            residual[y] = Mean(front) - blank[y];
            frontValid[y] = Math.Abs(residual[y]) < .10 * typical && max - min < .045 * typical;
            if (frontValid[y])
            {
                frontResiduals.Add(residual[y]);
            }
        }

        if (lines.Count < 24)
        {
            return false;
        }
        double common = frontResiduals.Count >= .6 * (height - 32) ? Median(frontResiduals.ToArray()) : double.NaN;
        int anchor = gate.BackStop - 1;
        double rampLength = (gate.FrontStart + gate.FrontStop - 1) / 2.0 - anchor;
        int next = 0;
        Span<double> gain = stackalloc double[5];
        var interpolatedGain = new double[height];
        var interpolatedBlank = new double[height];
        for (int y = 0; y < height; y++)
        {
            while (next < lines.Count - 1 && lines[next] < y)
            {
                next++;
            }
            int hi = lines[next];
            int lo = next == 0 ? hi : lines[next - 1];
            double mix = hi == lo ? 0 : Math.Clamp((y - lo) / (double)(hi - lo), 0, 1);
            interpolatedGain[y] = span[lo] + (span[hi] - span[lo]) * mix;
            interpolatedBlank[y] = blank[lo] + (blank[hi] - blank[lo]) * mix;
        }
        for (int y = 0; y < height; y++)
        {
            for (int i = 0; i < gain.Length; i++)
            {
                gain[i] = interpolatedGain[Math.Clamp(y + i - 2, 0, height - 1)];
            }
            double irePerCode = -converter.VSyncIre / Median(gain);
            double drift = residual[y] - common;
            double slope = frontValid[y] && Math.Abs(drift) < .08 * typical ? drift / rampLength : 0;
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                data[i] = (restored[i] - interpolatedBlank[y] - slope * Math.Max(0, x - anchor)) * irePerCode;
            }
        }
        return true;
    }

    internal static double Mean(ReadOnlySpan<double> values)
    {
        double sum = 0;
        foreach (double x in values)
        {
            sum += x;
        }
        return sum / values.Length;
    }

    internal static double Median(Span<double> values)
    {
        values.Sort();
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }
}
