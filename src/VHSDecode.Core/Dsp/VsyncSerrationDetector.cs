namespace VHSDecode.Core.Dsp;

public sealed record VsyncSerrationMeasurement(
    int Start,
    int End,
    double SyncLevel,
    double BlankLevel);

public sealed record VsyncSerrationResult(
    bool FoundSerration,
    bool HasLevels,
    double? SyncLevel,
    double? BlankLevel,
    double SyncLevelBias,
    IReadOnlyList<VsyncSerrationMeasurement> Measurements,
    IReadOnlyList<int> EnvelopeMinima,
    IReadOnlyList<int> HarmonicMinima,
    IReadOnlyList<int> Candidates);

public sealed class VsyncSerrationDetector
{
    private const int EnvelopePadding = 1024;
    private readonly int _divisor;
    private readonly SosSection[] _vsyncEnvelopeFilter;
    private readonly SosSection[] _serrationBaseHighPass;
    private readonly SosSection[] _serrationBaseLowPass;
    private readonly SosSection[] _serrationEnvelopeFilter;
    private readonly MovingAverageWindow _syncLevels = new(window: 2, minimumWatermark: 1);
    private readonly MovingAverageWindow _blankLevels = new(window: 2, minimumWatermark: 1);

    public VsyncSerrationDetector(
        double sampleRateHz,
        double framesPerSecond,
        double frameLines,
        double equalizingPulseUs,
        int divisor = 1)
    {
        if (sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (framesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        if (frameLines <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLines));
        }

        if (equalizingPulseUs <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(equalizingPulseUs));
        }

        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor));
        }

        _divisor = divisor;
        SampleRateHz = sampleRateHz / divisor;
        double verticalFrequencyHz = framesPerSecond * 2.0;
        double horizontalFrequencyHz = framesPerSecond * frameLines;

        _vsyncEnvelopeFilter = DesignFilter(
            SampleRateHz,
            verticalFrequencyHz * 5.0,
            transitionWidthHz: 1_000.0,
            highPass: false);
        _serrationBaseHighPass = DesignFilter(
            SampleRateHz,
            horizontalFrequencyHz,
            transitionWidthHz: horizontalFrequencyHz,
            highPass: true);
        _serrationBaseLowPass = DesignFilter(
            SampleRateHz,
            horizontalFrequencyHz,
            transitionWidthHz: horizontalFrequencyHz,
            highPass: false);
        _serrationEnvelopeFilter = DesignFilter(
            SampleRateHz,
            horizontalFrequencyHz / 3.0,
            transitionWidthHz: horizontalFrequencyHz / 2.0,
            highPass: false);

        EqualizingPulseLength = checked((int)Math.Round(
            SampleRateHz * equalizingPulseUs * 1e-6,
            MidpointRounding.ToEven));
        VsyncLength = checked((int)Math.Round(
            SampleRateHz / verticalFrequencyHz,
            MidpointRounding.ToEven));
        LineLength = checked((int)Math.Round(
            SampleRateHz / horizontalFrequencyHz,
            MidpointRounding.ToEven));
        double vbiLength = 6.5 * LineLength;
        MinimumVbiLength = vbiLength * 0.75;
        MaximumVbiLength = vbiLength * 1.25;
    }

    public double SampleRateHz { get; }

    public int EqualizingPulseLength { get; }

    public int VsyncLength { get; }

    public int LineLength { get; }

    public double MinimumVbiLength { get; }

    public double MaximumVbiLength { get; }

    public int OriginalRateEqualizingPulseLength => EqualizingPulseLength * _divisor;

    public int OriginalRateLineLength => LineLength * _divisor;

    public int FieldCount { get; private set; }

    public bool HasLevels => _syncLevels.HasValues && _blankLevels.HasValues;

    public int LevelCount => Math.Min(_syncLevels.Count, _blankLevels.Count);

    public (double SyncLevel, double BlankLevel)? PullLevels()
    {
        double? sync = _syncLevels.Pull();
        double? blank = _blankLevels.Pull();
        return sync.HasValue && blank.HasValue ? (sync.Value, blank.Value) : null;
    }

    public void PushLevels(double syncLevel, double blankLevel)
    {
        _syncLevels.Push(syncLevel);
        _blankLevels.Push(blankLevel);
    }

    public VsyncSerrationResult Analyze(ReadOnlySpan<double> data)
    {
        if (data.IsEmpty)
        {
            FieldCount++;
            return new VsyncSerrationResult(false, HasLevels, null, null, double.NaN, [], [], [], []);
        }

        double[] reduced = Downsample(data, _divisor);
        int padding = Math.Min(EnvelopePadding, reduced.Length);
        int paddedLength = reduced.Length + padding;
        int minimumFilteredLength = new[]
        {
            SosFilter.DefaultPadLength(_vsyncEnvelopeFilter),
            SosFilter.DefaultPadLength(_serrationBaseHighPass),
            SosFilter.DefaultPadLength(_serrationBaseLowPass),
            SosFilter.DefaultPadLength(_serrationEnvelopeFilter)
        }.Max() + 1;
        if (paddedLength < minimumFilteredLength)
        {
            FieldCount++;
            (double SyncLevel, double BlankLevel)? savedLevels = PullLevels();
            return new VsyncSerrationResult(
                false,
                HasLevels,
                savedLevels?.SyncLevel,
                savedLevels?.BlankLevel,
                reduced.Min(),
                [],
                [],
                [],
                []);
        }

        (double[] envelope, double bias) = BuildVsyncEnvelope(reduced);
        int[] envelopeMinima = LocalMinimaIndices(envelope);
        int[] harmonicMinima = PowerRatioSearch(BuildPaddedInput(reduced));
        int[] candidates = ArbitrateVsync(VsyncLength, envelopeMinima, harmonicMinima, reduced.Length + Math.Min(EnvelopePadding, reduced.Length));
        var measurements = new List<VsyncSerrationMeasurement>();
        foreach (int candidate in candidates)
        {
            if (TryMeasureSerration(
                    reduced,
                    candidate,
                    LineLength,
                    EqualizingPulseLength,
                    MinimumVbiLength,
                    MaximumVbiLength,
                    out VsyncSerrationMeasurement? measurement))
            {
                measurements.Add(measurement!);
                PushLevels(measurement!.SyncLevel, measurement.BlankLevel);
            }
        }

        FieldCount++;
        (double SyncLevel, double BlankLevel)? levels = PullLevels();
        return new VsyncSerrationResult(
            measurements.Count > 0,
            HasLevels,
            levels?.SyncLevel,
            levels?.BlankLevel,
            bias,
            measurements,
            envelopeMinima,
            harmonicMinima,
            candidates);
    }

    public static int[] LocalMinimaIndices(ReadOnlySpan<double> data)
    {
        if (data.Length < 3)
        {
            return [];
        }

        var minima = new List<int>();
        for (int i = 1; i < data.Length - 1; i++)
        {
            if (data[i] < data[i - 1] && data[i] <= data[i + 1])
            {
                minima.Add(i);
            }
        }

        return minima.ToArray();
    }

    public static int[] ArbitrateVsync(
        int vsyncLength,
        IReadOnlyList<int> envelopeMinima,
        IReadOnlyList<int> serrations,
        int dataLength)
    {
        if (vsyncLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vsyncLength));
        }

        if (dataLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataLength));
        }

        if (envelopeMinima.Count > 1)
        {
            var valid = new List<int>();
            for (int i = 0; i < serrations.Count; i++)
            {
                int edge = serrations[i];
                int next = serrations[Math.Min(i + 1, serrations.Count - 1)];
                foreach (int minimum in envelopeMinima)
                {
                    if (edge <= minimum && minimum <= next)
                    {
                        valid.Add(edge);
                    }
                }
            }

            return valid
                .Where(serration => serration - vsyncLength >= 0
                    || serration + vsyncLength <= dataLength - 1)
                .ToArray();
        }

        if (envelopeMinima.Count == 1)
        {
            int minimum = envelopeMinima[0];
            return minimum + vsyncLength < dataLength - 1
                ? [minimum, minimum + vsyncLength]
                : [minimum, Math.Max(minimum - vsyncLength, 0)];
        }

        return [];
    }

    public static bool TryMeasureSerration(
        ReadOnlySpan<double> data,
        int position,
        int lineLength,
        int equalizingPulseLength,
        double minimumVbiLength,
        double maximumVbiLength,
        out VsyncSerrationMeasurement? measurement,
        int lineSpan = 30)
    {
        measurement = null;
        if (data.IsEmpty || lineLength <= 0 || equalizingPulseLength <= 0 || lineSpan <= 0)
        {
            return false;
        }

        int start = Math.Max(0, position - checked(lineLength * lineSpan));
        int end = Math.Min(data.Length - 1, position + checked(lineLength * lineSpan));
        if (end <= start)
        {
            return false;
        }

        double[] block = data[start..end].ToArray();
        double minimum = block.Min();
        double level = ((Median(block) - minimum) / 2.0) + minimum;
        var crossings = new List<int>();
        int previousSign = PythonSign(block[0] - level);
        for (int i = 1; i < block.Length; i++)
        {
            int sign = PythonSign(block[i] - level);
            if (sign != previousSign)
            {
                crossings.Add(i - 1);
            }

            previousSign = sign;
        }

        var validDifferences = new List<int>();
        for (int i = 0; i < crossings.Count - 1; i++)
        {
            int difference = crossings[i + 1] - crossings[i];
            if ((equalizingPulseLength * 0.2) < difference
                && difference < (equalizingPulseLength * 5.0 / 4.0))
            {
                validDifferences.Add(i);
            }
        }

        if (validDifferences.Count < 9 || validDifferences.Count > 12)
        {
            return false;
        }

        int serrationStart = crossings[validDifferences[0]] + start;
        int serrationEnd = Math.Min(
            (int)(crossings[validDifferences[^1]] + (equalizingPulseLength / 2.0)) + start,
            data.Length - 1);
        int length = serrationEnd - serrationStart;
        if (!(minimumVbiLength < length && length < maximumVbiLength))
        {
            return false;
        }

        ReadOnlySpan<double> serration = data[serrationStart..serrationEnd];
        double halfAmplitude = Mean(serration);
        var peaks = new List<double>();
        var valleys = new List<double>();
        foreach (double value in serration)
        {
            if (value > halfAmplitude)
            {
                peaks.Add(value);
            }
            else
            {
                valleys.Add(value);
            }
        }

        if (peaks.Count == 0 || valleys.Count == 0)
        {
            return false;
        }

        measurement = new VsyncSerrationMeasurement(
            serrationStart,
            serrationEnd,
            Median(valleys),
            Median(peaks));
        return true;
    }

    public static bool CheckLevels(
        ReadOnlySpan<double> data,
        double oldSync,
        double newSync,
        double newBlank,
        double referenceSync,
        double hzIre,
        bool full = true)
    {
        if (data.IsEmpty || hzIre == 0.0)
        {
            return false;
        }

        double blankSyncIreDifference = (newBlank - newSync) / hzIre;
        if ((referenceSync - newSync) > (hzIre * 15.0) || blankSyncIreDifference > 47.0)
        {
            return false;
        }

        if (newSync - oldSync < (hzIre * 5.0))
        {
            return true;
        }

        if (full)
        {
            int belowSync = 0;
            int belowBlank = 0;
            foreach (double value in data)
            {
                if (value < newSync)
                {
                    belowSync++;
                }

                if (value < newBlank)
                {
                    belowBlank++;
                }
            }

            double amountBelow = (double)belowSync / data.Length;
            double amountBelowHalfSync = (double)belowBlank / data.Length;
            if (amountBelow > 0.07 || amountBelowHalfSync < 0.005)
            {
                return false;
            }
        }

        return true;
    }

    private (double[] Envelope, double Bias) BuildVsyncEnvelope(ReadOnlySpan<double> data)
    {
        double[] padded = BuildPaddedInput(data);
        var clipped = new double[padded.Length];
        for (int i = 0; i < clipped.Length; i++)
        {
            clipped[i] = Math.Max(0.0, padded[i]);
        }

        double[]? forward = null;
        double[]? reverse = null;
        Parallel.Invoke(
            () => forward = SosFilter.ApplyForwardBackward(_vsyncEnvelopeFilter, clipped),
            () =>
            {
                double[] reversed = clipped.ToArray();
                Array.Reverse(reversed);
                reverse = SosFilter.ApplyForwardBackward(_vsyncEnvelopeFilter, reversed);
                Array.Reverse(reverse);
            });

        int half = clipped.Length / 2;
        var combined = new double[clipped.Length];
        Array.Copy(reverse!, 0, combined, 0, half);
        Array.Copy(forward!, half, combined, half, combined.Length - half);
        double bias = clipped.Min();
        int padding = Math.Min(EnvelopePadding, data.Length);
        var envelope = new double[data.Length];
        for (int i = 0; i < envelope.Length; i++)
        {
            envelope[i] = combined[padding + i] - bias;
        }

        return (envelope, bias);
    }

    private int[] PowerRatioSearch(ReadOnlySpan<double> padded)
    {
        double[] firstHarmonic = SosFilter.ApplyForwardBackward(_serrationBaseHighPass, padded);
        firstHarmonic = SosFilter.ApplyForwardBackward(_serrationBaseLowPass, firstHarmonic);
        for (int i = 0; i < firstHarmonic.Length; i++)
        {
            firstHarmonic[i] *= firstHarmonic[i];
        }

        firstHarmonic = SosFilter.ApplyForwardBackward(_serrationEnvelopeFilter, firstHarmonic);
        return LocalMinimaIndices(firstHarmonic);
    }

    private static SosSection[] DesignFilter(
        double sampleRateHz,
        double cutoffHz,
        double transitionWidthHz,
        bool highPass)
    {
        double nyquist = sampleRateHz / 2.0;
        double normalizedPass = cutoffHz / nyquist;
        double normalizedStop = (cutoffHz + transitionWidthHz) / nyquist;
        (int calculatedOrder, double normalizedCutoff) = IirFilterDesign.ButterworthLowPassOrder(
            normalizedPass,
            normalizedStop,
            passRippleDb: 3.0,
            stopAttenuationDb: 30.0);
        int order = Math.Min(calculatedOrder, 20);
        return highPass
            ? IirFilterDesign.ButterworthHighPass(order, normalizedCutoff)
            : IirFilterDesign.ButterworthLowPass(order, normalizedCutoff);
    }

    private static double[] BuildPaddedInput(ReadOnlySpan<double> data)
    {
        int padding = Math.Min(EnvelopePadding, data.Length);
        var padded = new double[data.Length + padding];
        for (int i = 0; i < padding; i++)
        {
            padded[i] = data[padding - 1 - i];
        }

        data.CopyTo(padded.AsSpan(padding));
        return padded;
    }

    private static double[] Downsample(ReadOnlySpan<double> data, int divisor)
    {
        if (divisor == 1)
        {
            return data.ToArray();
        }

        var output = new double[(data.Length + divisor - 1) / divisor];
        for (int i = 0, source = 0; i < output.Length; i++, source += divisor)
        {
            output[i] = data[source];
        }

        return output;
    }

    private static int PythonSign(double value)
        => value > 0.0 ? 1 : value < 0.0 ? -1 : 0;

    private static double Mean(ReadOnlySpan<double> values)
    {
        double sum = 0.0;
        foreach (double value in values)
        {
            sum += value;
        }

        return sum / values.Length;
    }

    private static double Median(IEnumerable<double> values)
        => Median(values.ToArray());

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return double.NaN;
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return (sorted.Length & 1) != 0
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private sealed class MovingAverageWindow(int window, int minimumWatermark)
    {
        private readonly List<double> _values = [];

        public bool HasValues => _values.Count > minimumWatermark;

        public int Count => _values.Count;

        public void Push(double value) => _values.Add(value);

        public double? Pull()
        {
            if (_values.Count == 0)
            {
                return null;
            }

            if (_values.Count >= window)
            {
                _values.RemoveRange(0, _values.Count - window);
            }

            return _values.Average();
        }
    }
}
