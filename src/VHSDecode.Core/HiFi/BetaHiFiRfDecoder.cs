using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

internal sealed record BetaHiFiIqBlock(float[][] FmHz, double[][] Power, float[][] InstantPower, int RfSamples)
{
    internal int Length => FmHz[0].Length;
}

internal readonly record struct BetaHiFiGap(int Left, int Right, int Events);

internal sealed record BetaHiFiRfCalibration(
    double DominanceMidpointDb,
    double DominanceP10Db,
    double DominanceP90Db,
    double[] CarrierReferencePower,
    string TimingAnchorGroup,
    double TimingAnchorThresholdDb,
    bool TimingAnchorUsable,
    BetaHiFiGap AToBGap,
    BetaHiFiGap BToAGap,
    int CalibrationSwitchCount,
    double HeadAFraction);

/// <summary>
/// Experimental NTSC Beta AFM front end. Calibration is learned from this capture;
/// no programme mean is subtracted and no previous capture's noise template is used.
/// Input blocks contain 250 ms RF context before and after the central emission.
/// </summary>
internal sealed class BetaHiFiRfDecoder
{
    internal const int IqRate = 500_000;
    internal const int ContextIqSamples = IqRate / 4;
    internal const int AudioRate = 48_000;
    private const double FieldRate = 60_000.0 / 1001;
    private const double FloatTiny = 1.1754943508222875e-38;
    private static readonly double[] Carriers = [87.75, 97.25, 106.75, 116.25];
    private readonly float[] _rfTaps;
    private readonly float[] _iqTaps;
    private readonly int _decimation;
    private readonly int _threads;
    private readonly bool _headInterpolation;
    private readonly bool _dropoutCompensation;
    private readonly bool _muteDropouts;
    private readonly SosSection[] _powerFilter = IirFilterDesign.ButterworthLowPassScipySos(2, 1500.0 / (IqRate / 2));
    private readonly SosSection[] _audioLowPass = IirFilterDesign.ButterworthLowPassScipySos(5, 20_000.0 / (IqRate / 2));
    private readonly SosSection[] _audioHighPass = IirFilterDesign.ButterworthHighPassScipySos(2, 15.0 / (IqRate / 2));

    internal BetaHiFiRfDecoder(double inputRateHz, int threads = 1,
        bool headSwitchingInterpolation = true, bool dropoutCompensation = true, bool muteDropouts = false)
    {
        if (!SupportsInputRate(inputRateHz))
        {
            throw new ArgumentOutOfRangeException(nameof(inputRateHz), "Experimental NTSC Beta HiFi requires 20 or 40 MSPS RF.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        InputRateHz = inputRateHz;
        ContextRfSamples = checked((int)inputRateHz / 4);
        EmitRfSamples = checked((int)inputRateHz * 2);
        _decimation = (int)inputRateHz / 5_000_000;
        _threads = Math.Min(threads, 4);
        _headInterpolation = headSwitchingInterpolation;
        _dropoutCompensation = dropoutCompensation;
        _muteDropouts = muteDropouts;
        _rfTaps = BetaHiFiSignalDsp.FirWin(20 * _decimation + 1, 1.0 / _decimation, 5).Select(x => (float)x).ToArray();
        _iqTaps = BetaHiFiSignalDsp.FirWin(639, 110_000.0 / 2_500_000, 8.6).Select(x => (float)x).ToArray();
    }

    internal double InputRateHz { get; }
    internal int ContextRfSamples { get; }
    internal int EmitRfSamples { get; }
    internal static bool SupportsInputRate(double rate) => rate is 20_000_000 or 40_000_000;
    internal static double[] CarrierFrequenciesHz => Carriers.Select(x => x * (15_750_000.0 / 1001)).ToArray();

    internal static long CalculateAudioFrames(long rfSamples, double inputRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rfSamples);
        if (!SupportsInputRate(inputRateHz))
        {
            throw new ArgumentOutOfRangeException(nameof(inputRateHz));
        }

        // 48000/rate reduces to 3/1250 or 3/2500. Round exact half frames
        // to even without double-operation-order errors or multiplying a long
        // RF count by 48000 (which would overflow on long recordings).
        long denominator = inputRateHz == 20_000_000 ? 1250 : 2500;
        long numeratorRemainder = (rfSamples % denominator) * 3;
        long frames = checked((rfSamples / denominator) * 3 + numeratorRemainder / denominator);
        long remainder = numeratorRemainder % denominator;
        if (remainder * 2 > denominator || (remainder * 2 == denominator && (frames & 1) != 0))
        {
            frames = checked(frames + 1);
        }

        return frames;
    }

    internal BetaHiFiIqBlock Analyze(ReadOnlySpan<float> rf)
    {
        if (rf.Length <= 2 * ContextRfSamples || rf.Length > EmitRfSamples + 2 * ContextRfSamples)
        {
            throw new ArgumentException("Beta RF blocks require a nonempty central region with 250 ms context on each side.", nameof(rf));
        }

        foreach (float value in rf)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Beta RF contains nonfinite samples.", nameof(rf));
            }
        }

        float[] reduced = BetaHiFiSignalDsp.Decimate(rf, _decimation, _rfTaps);
        var fm = new float[4][];
        var powers = new double[4][];
        var instantaneous = new float[4][];
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = _threads }, branch =>
        {
            double increment = -2 * Math.PI * Carriers[branch] * (15_750_000.0 / 1001) / 5_000_000;
            (double stepImaginary, double stepReal) = Math.SinCos(increment);
            double real = 1;
            double imaginary = 0;
            var mixedReal = new float[reduced.Length];
            var mixedImaginary = new float[reduced.Length];
            for (int i = 0; i < reduced.Length; i++)
            {
                if ((i & 4095) == 0)
                {
                    (imaginary, real) = Math.SinCos(i * increment);
                }

                mixedReal[i] = reduced[i] * (float)real;
                mixedImaginary[i] = reduced[i] * (float)imaginary;
                (real, imaginary) = (real * stepReal - imaginary * stepImaginary,
                    imaginary * stepReal + real * stepImaginary);
            }

            float[] iqReal = BetaHiFiSignalDsp.Decimate(mixedReal, 10, _iqTaps);
            float[] iqImaginary = BetaHiFiSignalDsp.Decimate(mixedImaginary, 10, _iqTaps);
            int count = iqReal.Length - 1;
            var frequency = new float[count];
            var power = new float[count];
            var powerDouble = new double[count];
            float scale = (float)(IqRate / (2 * Math.PI));
            for (int i = 0; i < count; i++)
            {
                float productReal = iqReal[i + 1] * iqReal[i] + iqImaginary[i + 1] * iqImaginary[i];
                float productImaginary = iqImaginary[i + 1] * iqReal[i] - iqReal[i + 1] * iqImaginary[i];
                frequency[i] = MathF.Atan2(productImaginary, productReal) * scale;
                power[i] = iqReal[i + 1] * iqReal[i + 1] + iqImaginary[i + 1] * iqImaginary[i + 1];
                powerDouble[i] = power[i];
            }

            double[] smoothed = SosFilter.ApplyForwardBackward(_powerFilter, powerDouble);
            for (int i = 0; i < smoothed.Length; i++)
            {
                smoothed[i] = Math.Max(FloatTiny, smoothed[i]);
            }

            fm[branch] = frequency;
            powers[branch] = smoothed;
            instantaneous[branch] = power;
        });

        return new BetaHiFiIqBlock(fm, powers, instantaneous, rf.Length);
    }

    internal BetaHiFiRfCalibration Calibrate(IReadOnlyList<BetaHiFiIqBlock> blocks, IReadOnlyList<int> emitRfSamples)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(emitRfSamples);
        if (blocks.Count == 0 || blocks.Count != emitRfSamples.Count)
        {
            throw new ArgumentException("Calibration requires matching IQ blocks and central RF counts.");
        }

        int count = 0;
        var lengths = new int[blocks.Count];
        for (int b = 0; b < blocks.Count; b++)
        {
            ValidateBlock(blocks[b]);
            ValidateEmission(blocks[b], emitRfSamples[b]);
            lengths[b] = Math.Min(checked((int)Math.Round(emitRfSamples[b] * (IqRate / InputRateHz))), 6 * IqRate - count);
            count += lengths[b];
        }

        if (count < IqRate / 4)
        {
            throw new InvalidDataException("Beta HiFi calibration needs at least 250 ms of RF containing alternating heads.");
        }

        float[][] fm = Enumerable.Range(0, 4).Select(_ => new float[count]).ToArray();
        double[][] power = Enumerable.Range(0, 4).Select(_ => new double[count]).ToArray();
        float[][] instant = Enumerable.Range(0, 4).Select(_ => new float[count]).ToArray();
        int position = 0;
        for (int b = 0; b < blocks.Count; b++)
        {
            for (int k = 0; k < 4; k++)
            {
                blocks[b].FmHz[k].AsSpan(ContextIqSamples, lengths[b]).CopyTo(fm[k].AsSpan(position));
                blocks[b].Power[k].AsSpan(ContextIqSamples, lengths[b]).CopyTo(power[k].AsSpan(position));
                blocks[b].InstantPower[k].AsSpan(ContextIqSamples, lengths[b]).CopyTo(instant[k].AsSpan(position));
            }

            position += lengths[b];
        }

        var combined = new BetaHiFiIqBlock(fm, power, instant, 0);
        double[] dominance = Dominance(power);
        double[] steadyDominance = dominance.AsSpan(50_000).ToArray();
        double p10 = Quantile(steadyDominance, .1);
        double p90 = Quantile(steadyDominance, .9);
        if (!double.IsFinite(p10) || !double.IsFinite(p90) || p90 - p10 <= 10)
        {
            throw new InvalidDataException("No usable alternating NTSC Beta HiFi carriers: insufficient two-head power separation.");
        }

        double midpoint = (p10 + p90) / 2;
        bool[] coarse = CoarseHeads(power, midpoint);
        int[] edges = Edges(coarse);
        ValidateHeadCadence(edges);
        bool[] valid = new bool[count];
        Array.Fill(valid, true, 50_000, count - 50_000 - 5000);
        foreach (int edge in edges)
        {
            Array.Fill(valid, false, Math.Max(0, edge - 500), Math.Min(count, edge + 500) - Math.Max(0, edge - 500));
        }

        var references = new double[4];
        for (int branch = 0; branch < 4; branch++)
        {
            bool groupA = branch is 0 or 2;
            double[] selected = Enumerable.Range(0, count).Where(i => valid[i] && coarse[i] == groupA).Select(i => power[branch][i]).ToArray();
            references[branch] = Quantile(selected, .9);
            if (!double.IsFinite(references[branch]) || references[branch] <= FloatTiny * 10)
            {
                throw new InvalidDataException("Beta HiFi carrier reference power is absent or nonfinite.");
            }
        }

        bool anchorA = true;
        double bestSeparation = double.NegativeInfinity;
        double threshold = 0;
        foreach (bool groupA in new[] { true, false })
        {
            double[] confidence = Confidence(instant, references, groupA);
            double active = Quantile(Enumerable.Range(0, count).Where(i => valid[i] && coarse[i] == groupA).Select(i => confidence[i]).ToArray(), .1);
            double inactive = Quantile(Enumerable.Range(0, count).Where(i => valid[i] && coarse[i] != groupA).Select(i => confidence[i]).ToArray(), .9);
            if (active - inactive > bestSeparation)
            {
                bestSeparation = active - inactive;
                anchorA = groupA;
                threshold = (active + inactive) / 2;
            }
        }

        var calibration = new BetaHiFiRfCalibration(midpoint, p10, p90, references, anchorA ? "A" : "B", threshold,
            bestSeparation >= 8, default, default, edges.Length, coarse.Count(x => x) / (double)count);
        bool[] heads = HeadsFromPower(combined, calibration);
        (double[] left, double[] right) = SelectAndRepair(combined, heads, calibration);
        return calibration with
        {
            AToBGap = LearnGap(left, right, heads, incomingA: false),
            BToAGap = LearnGap(left, right, heads, incomingA: true),
            CalibrationSwitchCount = Edges(heads).Length,
            HeadAFraction = heads.Count(x => x) / (double)count
        };
    }

    internal HiFiDecodedBlock Decode(BetaHiFiIqBlock block, BetaHiFiRfCalibration calibration, int emitRfSamples, long emittedRfSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ValidateBlock(block);
        ValidateEmission(block, emitRfSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(emittedRfSamples);
        int frames = checked((int)(CalculateAudioFrames(checked(emittedRfSamples + emitRfSamples), InputRateHz)
            - CalculateAudioFrames(emittedRfSamples, InputRateHz)));
        // A real EOF may contain fewer RF samples than one 500 kHz IQ period.
        // The phase-difference discriminator then retains context only, and the
        // exact audio clock emits no frames. Validate its geometry, but do not
        // require an invented central IQ/audio sample or run audio filters.
        if (frames == 0)
        {
            return new HiFiDecodedBlock([], [], 0, 0);
        }

        bool[] heads = HeadsFromPower(block, calibration);
        (double[] left, double[] right) = SelectAndRepair(block, heads, calibration);
        if (_headInterpolation)
        {
            ApplyGaps(left, right, heads, calibration, block);
        }

        float[] Process(double[] values)
        {
            double[] low = SosFilter.ApplyForwardBackward(_audioLowPass, values);
            double[] high = SosFilter.ApplyForwardBackward(_audioHighPass, low);
            for (int i = 0; i < high.Length; i++)
            {
                high[i] *= .5 / 75_000;
            }

            double[] sampled = BetaHiFiSignalDsp.Resample(high, 12, 125);
            if (sampled.Length < 12_000 + frames)
            {
                throw new InvalidDataException("Beta audio resampler did not retain the requested central emission.");
            }

            var output = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double value = sampled[12_000 + i];
                if (!double.IsFinite(value))
                {
                    throw new InvalidDataException("Beta HiFi demodulation produced nonfinite audio.");
                }

                output[i] = (float)value;
            }

            return output;
        }

        return new HiFiDecodedBlock(Process(left), Process(right), 0, 0);
    }

    internal static bool[] HeadsFromPower(BetaHiFiIqBlock block, BetaHiFiRfCalibration calibration)
    {
        bool[] coarse = CoarseHeads(block.Power, calibration.DominanceMidpointDb);
        if (!calibration.TimingAnchorUsable || coarse.Length == 0)
        {
            return coarse;
        }

        bool anchorA = calibration.TimingAnchorGroup == "A";
        double[] confidence = Confidence(block.InstantPower, calibration.CarrierReferencePower, anchorA);
        int[] edges = Edges(coarse);
        var refined = new List<int>(edges.Length);
        foreach (int edge in edges)
        {
            int lower = Math.Max(1, edge - 300);
            int upper = Math.Min(coarse.Length - 1, edge + 301);
            bool desiredOn = coarse[edge] == anchorA;
            int nearest = edge;
            int distance = int.MaxValue;
            int runStart = -1;
            for (int i = lower; i <= upper; i++)
            {
                bool desired = i < upper && (confidence[i] >= calibration.TimingAnchorThresholdDb) == desiredOn;
                if (desired && runStart < 0)
                {
                    runStart = i;
                }
                else if (!desired && runStart >= 0)
                {
                    if (runStart > lower && i - runStart >= 10 && Math.Abs(runStart - edge) < distance)
                    {
                        nearest = runStart;
                        distance = Math.Abs(runStart - edge);
                    }

                    runStart = -1;
                }
            }

            if (refined.Count > 0 && nearest <= refined[^1])
            {
                nearest = edge;
            }

            refined.Add(nearest);
        }

        var mask = new bool[coarse.Length];
        int previous = 0;
        bool state = coarse[0];
        foreach (int edge in refined)
        {
            Array.Fill(mask, state, previous, edge - previous);
            previous = edge;
            state = !state;
        }

        Array.Fill(mask, state, previous, mask.Length - previous);
        return mask;
    }

    internal static bool[] CoarseHeads(double[][] power, double midpoint)
    {
        double[] dominance = Dominance(power);
        for (int i = 0; i < dominance.Length; i++)
        {
            dominance[i] -= midpoint;
        }

        var candidates = new List<(int Start, int Sign)>();
        int start = 0;
        int Strength(double value) => value >= 3 ? 1 : value <= -3 ? -1 : 0;
        while (start < dominance.Length)
        {
            int sign = Strength(dominance[start]);
            int end = start + 1;
            while (end < dominance.Length && Strength(dominance[end]) == sign)
            {
                end++;
            }

            if (sign != 0 && end - start >= 75)
            {
                candidates.Add((start, sign));
            }

            start = end;
        }

        var selected = new bool[dominance.Length];
        if (selected.Length == 0)
        {
            return selected;
        }

        bool current = candidates.Count > 0 ? candidates[0].Sign > 0 : Quantile(dominance, .5) > 0;
        int last = 0;
        foreach ((int candidate, int sign) in candidates)
        {
            bool incoming = sign > 0;
            if (incoming == current)
            {
                continue;
            }

            int lower = Math.Max(last + 1, candidate - 250);
            int edge = candidate;
            for (int i = candidate; i >= Math.Max(1, lower); i--)
            {
                bool crossing = incoming ? dominance[i - 1] <= 0 && dominance[i] > 0 : dominance[i - 1] >= 0 && dominance[i] < 0;
                if (crossing)
                {
                    edge = i;
                    break;
                }
            }

            if (last > 0 && edge - last < .5 * IqRate / FieldRate)
            {
                continue;
            }

            Array.Fill(selected, current, last, edge - last);
            last = edge;
            current = incoming;
        }

        Array.Fill(selected, current, last, selected.Length - last);
        return selected;
    }

    internal (double[] Left, double[] Right) SelectAndRepair(BetaHiFiIqBlock block, bool[] heads, BetaHiFiRfCalibration calibration)
    {
        int count = block.Length;
        if (heads.Length != count)
        {
            throw new ArgumentException("Beta head mask length must match IQ.");
        }

        var left = new double[count];
        var right = new double[count];
        foreach (int channel in new[] { 0, 1 })
        {
            double[] audio = channel == 0 ? left : right;
            var bad = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int branch = 2 * channel + (heads[i] ? 0 : 1);
                double value = block.FmHz[branch][i];
                audio[i] = value;
                double selectedPower = block.Power[branch][i];
                bad[i] = !double.IsFinite(value) || Math.Abs(value) > 112_500 ||
                    !double.IsFinite(selectedPower) || selectedPower <= calibration.CarrierReferencePower[branch] * Math.Pow(10, -2.4);
            }

            if (_dropoutCompensation)
            {
                RepairInvalid(audio, bad, _muteDropouts);
            }

            for (int i = 0; i < count; i++)
            {
                if (!double.IsFinite(audio[i]))
                {
                    audio[i] = 0;
                }
            }
        }

        return (left, right);
    }

    internal static void RepairInvalid(double[] audio, bool[] bad, bool muteOnly = false)
    {
        if (audio.Length != bad.Length)
        {
            throw new ArgumentException("Invalid-sample mask must match the audio length.");
        }

        int start = 0;
        while (start < audio.Length)
        {
            if (!bad[start])
            {
                start++;
                continue;
            }

            int end = start + 1;
            while (end < audio.Length && bad[end])
            {
                end++;
            }

            bool hasLeft = start > 0 && !bad[start - 1];
            bool hasRight = end < audio.Length && !bad[end];
            double left = hasLeft ? audio[start - 1] : 0;
            double right = hasRight ? audio[end] : 0;
            if (!muteOnly && end - start <= 40 && hasLeft && hasRight)
            {
                for (int i = start; i < end; i++)
                {
                    double weight = (i - start + 1.0) / (end - start + 1);
                    audio[i] = left * (1 - weight) + right * weight;
                }
            }
            else
            {
                Array.Clear(audio, start, end - start);
                int width = Math.Min(25, (end - start) / 2);
                for (int i = 0; i < width; i++)
                {
                    if (hasLeft)
                    {
                        audio[start + i] = left * (1 - (i + 1.0) / width);
                    }

                    if (hasRight)
                    {
                        audio[end - width + i] = right * (i / (double)width);
                    }
                }
            }

            start = end;
        }
    }

    internal static BetaHiFiGap LearnGap(double[] left, double[] right, bool[] heads, bool incomingA)
    {
        int[] edges = Edges(heads).Where(i => heads[i] == incomingA && i > 102 && i < heads.Length - 103).ToArray();
        // Short captures with no repeated evidence must not invent a repair window.
        if (edges.Length < 4)
        {
            return new BetaHiFiGap(0, 0, edges.Length);
        }

        double[][] profiles = [new double[201], new double[201]];
        for (int channel = 0; channel < 2; channel++)
        {
            double[] audio = channel == 0 ? left : right;
            for (int offset = -100; offset <= 100; offset++)
            {
                double[] values = edges.Select(edge => Math.Abs(audio[edge + offset + 1] - 2 * audio[edge + offset] + audio[edge + offset - 1])).ToArray();
                profiles[channel][offset + 100] = Quantile(values, .5);
            }
        }

        double[] thresholds = profiles.Select(profile => Math.Max(1000, 3 * Quantile(Enumerable.Range(-100, 201).Where(x => Math.Abs(x) >= 60).Select(x => profile[x + 100]).ToArray(), .5))).ToArray();
        int first = int.MaxValue;
        int last = int.MinValue;
        for (int offset = -60; offset <= 60; offset++)
        {
            if (profiles[0][offset + 100] > thresholds[0] || profiles[1][offset + 100] > thresholds[1])
            {
                first = Math.Min(first, offset);
                last = Math.Max(last, offset);
            }
        }

        if (first == int.MaxValue || last - first + 9 > 80)
        {
            return new BetaHiFiGap(0, 0, edges.Length);
        }

        return new BetaHiFiGap(first - 4, last + 5, edges.Length);
    }

    internal static void ApplyGaps(double[] left, double[] right, bool[] heads, BetaHiFiRfCalibration calibration,
        BetaHiFiIqBlock? reliabilityBlock = null)
    {
        int lastEnd = -1;
        foreach (int edge in Edges(heads))
        {
            BetaHiFiGap gap = heads[edge] ? calibration.BToAGap : calibration.AToBGap;
            if (gap.Left == gap.Right)
            {
                continue;
            }

            int start = edge + gap.Left;
            int end = edge + gap.Right;
            if (start < 1 || end >= left.Length || start <= lastEnd || end <= start)
            {
                continue;
            }

            int greatestEnd = end;
            for (int channel = 0; channel < 2; channel++)
            {
                double[] audio = channel == 0 ? left : right;
                int channelStart = start;
                int channelEnd = end;
                if (reliabilityBlock is not null)
                {
                    (channelStart, channelEnd) = RefineGapAnchors(left, right, heads, reliabilityBlock,
                        calibration, start, end, channel);
                }

                double anchorLeft = audio[channelStart - 1];
                double anchorRight = audio[channelEnd];
                for (int i = channelStart; i < channelEnd; i++)
                {
                    double weight = (i - channelStart + 1.0) / (channelEnd - channelStart + 1);
                    audio[i] = anchorLeft * (1 - weight) + anchorRight * weight;
                }

                greatestEnd = Math.Max(greatestEnd, channelEnd);
            }

            lastEnd = greatestEnd;
        }
    }

    /// <summary>
    /// A median learned gap can leave a rare demodulation glitch as its interpolation
    /// anchor. Only an already identified handoff may grow, and only when weak actual
    /// IQ power corroborates abnormal outward curvature. Strong-RF musical transients
    /// and curvature pointing into the concealed interval do not trigger this repair.
    /// Each audio channel is assessed separately so a damaged carrier cannot expand
    /// the other channel's otherwise valid programme concealment.
    /// </summary>
    internal static (int Start, int End) RefineGapAnchors(
        double[] left, double[] right, bool[] heads, BetaHiFiIqBlock block,
        BetaHiFiRfCalibration calibration, int start, int end, int channel = 0)
    {
        if (channel is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        if (start < 1 || end >= left.Length || end <= start || end - start > 80)
        {
            return (start, end);
        }

        int FindAnchor(int anchor, int direction)
        {
            // All baseline and derivative samples point away from the learned gap.
            // The largest search plus baseline reach is less than 100 microseconds.
            if (anchor + direction * 50 < 0 || anchor + direction * 50 >= left.Length)
            {
                return anchor;
            }

            double Curvature(int sample)
            {
                double[] audio = channel == 0 ? left : right;
                return Math.Abs(audio[sample] - 2 * audio[sample + direction] + audio[sample + 2 * direction]);
            }

            double Confidence(int sample)
            {
                int branch = (heads[sample] ? 0 : 1) + 2 * channel;
                double current = 10 * Math.Log10(Math.Max(block.InstantPower[branch][sample], 1e-30)
                    / calibration.CarrierReferencePower[branch]);
                Span<float> median = stackalloc float[5];
                for (int i = 0; i < median.Length; i++)
                {
                    median[i] = block.InstantPower[branch][sample + i * direction];
                }

                median.Sort();
                double outward = 10 * Math.Log10(Math.Max(median[2], 1e-30)
                    / calibration.CarrierReferencePower[branch]);
                return Math.Min(current, outward);
            }

            double[] baseline = Enumerable.Range(8, 25).Select(i => Curvature(anchor + i * direction)).ToArray();
            double threshold = Math.Max(10_000, 6 * Quantile(baseline, .5));

            if (Confidence(anchor) >= -8 || Curvature(anchor) <= threshold)
            {
                return anchor;
            }

            for (int shift = 1; shift <= 16; shift++)
            {
                int candidate = anchor + direction * shift;
                int length = direction < 0 ? end - (candidate + 1) : candidate - start;
                if (length > 80)
                {
                    break;
                }

                if (Confidence(candidate) < -6)
                {
                    continue;
                }

                bool reliable = true;
                for (int i = 0; i < 3 && reliable; i++)
                {
                    if (Curvature(candidate + i * direction) > threshold)
                    {
                        reliable = false;
                    }
                }

                if (reliable)
                {
                    return candidate;
                }
            }

            return anchor;
        }

        start = FindAnchor(start - 1, -1) + 1;
        end = FindAnchor(end, 1);
        return (start, end);
    }

    private static double[] Dominance(double[][] power)
    {
        int count = power[0].Length;
        var output = new double[count];
        for (int i = 0; i < count; i++)
        {
            output[i] = (Math.Log(Math.Max(power[0][i], FloatTiny)) + Math.Log(Math.Max(power[2][i], FloatTiny)) -
                Math.Log(Math.Max(power[1][i], FloatTiny)) - Math.Log(Math.Max(power[3][i], FloatTiny))) * (5 / Math.Log(10));
        }

        return output;
    }

    private static double[] Confidence(float[][] instant, double[] reference, bool groupA)
    {
        int count = instant[0].Length;
        var output = new double[count];
        Span<float> median = stackalloc float[5];
        foreach (int branch in groupA ? new[] { 0, 2 } : new[] { 1, 3 })
        {
            for (int i = 0; i < count; i++)
            {
                for (int j = -2; j <= 2; j++)
                {
                    median[j + 2] = instant[branch][Math.Clamp(i + j, 0, count - 1)];
                }

                median.Sort();
                output[i] += 5 * Math.Log10(Math.Max(median[2], 1e-30) / reference[branch]);
            }
        }

        return output;
    }

    private static int[] Edges(bool[] heads)
    {
        var edges = new List<int>();
        for (int i = 1; i < heads.Length; i++)
        {
            if (heads[i] != heads[i - 1])
            {
                edges.Add(i);
            }
        }

        return edges.ToArray();
    }

    private static double Quantile(double[] values, double quantile)
    {
        if (values.Length == 0)
        {
            throw new InvalidDataException("Insufficient reliable Beta HiFi samples for calibration.");
        }

        Array.Sort(values);
        double position = (values.Length - 1) * quantile;
        int lower = (int)position;
        int upper = Math.Min(lower + 1, values.Length - 1);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }

    private static void ValidateHeadCadence(int[] edges)
    {
        if (edges.Length < 8)
        {
            throw new InvalidDataException("Insufficient alternating Beta HiFi head switches for capture calibration.");
        }

        double expected = IqRate / FieldRate;
        int regular = Enumerable.Range(1, edges.Length - 1).Count(i => Math.Abs((edges[i] - edges[i - 1]) / expected - 1) <= .2);
        if (regular < .7 * (edges.Length - 1))
        {
            throw new InvalidDataException("RF power changes do not follow the expected NTSC Beta HiFi head cadence.");
        }
    }

    private static void ValidateBlock(BetaHiFiIqBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.FmHz.Length != 4 || block.Power.Length != 4 || block.InstantPower.Length != 4 ||
            block.FmHz.Any(x => x is null) || block.Power.Any(x => x is null) || block.InstantPower.Any(x => x is null))
        {
            throw new ArgumentException("Beta IQ requires four carrier branches.", nameof(block));
        }

        int count = block.Length;
        if (count < 2 * ContextIqSamples || block.FmHz.Any(x => x.Length != count) ||
            block.Power.Any(x => x.Length != count) || block.InstantPower.Any(x => x.Length != count))
        {
            throw new ArgumentException("Beta IQ branch lengths or context are invalid.", nameof(block));
        }
    }

    private void ValidateEmission(BetaHiFiIqBlock block, int count)
    {
        int rfSamplesPerIq = checked((int)InputRateHz / IqRate);
        int expectedIqLength = checked((block.RfSamples + rfSamplesPerIq - 1) / rfSamplesPerIq - 1);
        if (count <= 0 || count > EmitRfSamples || block.RfSamples != checked(count + 2 * ContextRfSamples)
            || block.Length != expectedIqLength)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Central RF count and discriminator length must match this contextual block.");
        }
    }
}
