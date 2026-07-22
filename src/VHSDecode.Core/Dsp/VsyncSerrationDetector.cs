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
    IReadOnlyList<int> Candidates,
    int LevelCountBeforePull = 0);

public sealed class VsyncSerrationDetector
{
    private const int EnvelopePadding = 1024;
    private const int MaximumRetainedWorkspaceSampleCount = 1024 * 1024;
    private readonly int _divisor;
    private readonly TransferFunction _vsyncEnvelopeFilter;
    private readonly TransferFunction _serrationBaseHighPass;
    private readonly TransferFunction _serrationBaseLowPass;
    private readonly TransferFunction _serrationEnvelopeFilter;
    private readonly MovingAverageWindow _syncLevels = new(window: 2, minimumWatermark: 1);
    private readonly MovingAverageWindow _blankLevels = new(window: 2, minimumWatermark: 1);
    private AnalysisWorkspace? _mostRecentAnalysisWorkspace;
    private AnalysisWorkspace? _previousAnalysisWorkspace;

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

        int reducedLength = (data.Length + _divisor - 1) / _divisor;
        int padding = Math.Min(EnvelopePadding, reducedLength);
        int paddedLength = reducedLength + padding;
        AnalysisWorkspace workspace = GetAnalysisWorkspace(reducedLength, paddedLength);
        double[] reduced = workspace.Reduced;
        Downsample(data, _divisor, reduced);
        int minimumFilteredLength = new[]
        {
            IirFilter.DefaultPadLength(_vsyncEnvelopeFilter),
            IirFilter.DefaultPadLength(_serrationBaseHighPass),
            IirFilter.DefaultPadLength(_serrationBaseLowPass),
            IirFilter.DefaultPadLength(_serrationEnvelopeFilter)
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

        double[] padded = workspace.Padded;
        BuildPaddedInput(reduced, padded);
        int[] envelopeMinima = [];
        int[] harmonicMinima = [];
        double bias = 0.0;
        Parallel.Invoke(
            () =>
            {
                bias = BuildVsyncEnvelope(reduced, padded, workspace);
                envelopeMinima = LocalMinimaIndices(workspace.Envelope);
            },
            () => harmonicMinima = PowerRatioSearch(padded, workspace.PowerRatio));
        int[] candidates = ArbitrateVsync(
            VsyncLength,
            envelopeMinima,
            harmonicMinima,
            reduced.Length + Math.Min(EnvelopePadding, reduced.Length));
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
        int levelCountBeforePull = LevelCount;
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
            candidates,
            levelCountBeforePull);
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

        ReadOnlySpan<double> block = data[start..end];
        double minimum = MinimumFloat64(block);
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
        (double SyncLevel, double BlankLevel)? levels = GetSerrationSyncLevels(serration);
        if (!levels.HasValue)
        {
            return false;
        }

        measurement = new VsyncSerrationMeasurement(
            serrationStart,
            serrationEnd,
            levels.Value.SyncLevel,
            levels.Value.BlankLevel);
        return true;
    }

    internal static (double SyncLevel, double BlankLevel)? GetSerrationSyncLevels(
        ReadOnlySpan<double> serration)
    {
        double halfAmplitude = NumbaReduction.MeanFloat64FastMath(serration);
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
            return null;
        }

        return (Median(valleys), Median(peaks));
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

    private double BuildVsyncEnvelope(
        ReadOnlySpan<double> data,
        ReadOnlySpan<double> padded,
        AnalysisWorkspace workspace)
    {
        double[] forward = workspace.Forward;
        for (int i = 0; i < forward.Length; i++)
        {
            forward[i] = Math.Max(0.0, padded[i]);
        }

        double bias = forward.Min();
        double[] reverse = workspace.Reverse;
        forward.CopyTo(reverse, 0);
        Parallel.Invoke(
            () => IirFilter.ApplyForwardBackwardInPlace(_vsyncEnvelopeFilter, forward),
            () =>
            {
                Array.Reverse(reverse);
                IirFilter.ApplyForwardBackwardInPlace(_vsyncEnvelopeFilter, reverse);
                Array.Reverse(reverse);
            });

        int half = forward.Length / 2;
        int padding = Math.Min(EnvelopePadding, data.Length);
        double[] envelope = workspace.Envelope;
        for (int i = 0; i < envelope.Length; i++)
        {
            int source = padding + i;
            envelope[i] = (source < half ? reverse[source] : forward[source]) - bias;
        }

        return bias;
    }

    private int[] PowerRatioSearch(ReadOnlySpan<double> padded, double[] firstHarmonic)
    {
        padded.CopyTo(firstHarmonic);
        IirFilter.ApplyForwardBackwardInPlace(_serrationBaseHighPass, firstHarmonic);
        IirFilter.ApplyForwardBackwardInPlace(_serrationBaseLowPass, firstHarmonic);
        for (int i = 0; i < firstHarmonic.Length; i++)
        {
            firstHarmonic[i] *= firstHarmonic[i];
        }

        IirFilter.ApplyForwardBackwardInPlace(_serrationEnvelopeFilter, firstHarmonic);
        return LocalMinimaIndices(firstHarmonic);
    }

    private static TransferFunction DesignFilter(
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
            ? IirFilterDesign.ButterworthHighPassTransferFunction(order, normalizedCutoff)
            : IirFilterDesign.ButterworthLowPassTransferFunction(order, normalizedCutoff);
    }

    private AnalysisWorkspace GetAnalysisWorkspace(int reducedLength, int paddedLength)
    {
        if (paddedLength > MaximumRetainedWorkspaceSampleCount)
        {
            return new AnalysisWorkspace(reducedLength, paddedLength);
        }

        if (Matches(_mostRecentAnalysisWorkspace, reducedLength, paddedLength))
        {
            return _mostRecentAnalysisWorkspace!;
        }

        if (Matches(_previousAnalysisWorkspace, reducedLength, paddedLength))
        {
            (_mostRecentAnalysisWorkspace, _previousAnalysisWorkspace) =
                (_previousAnalysisWorkspace, _mostRecentAnalysisWorkspace);
            return _mostRecentAnalysisWorkspace!;
        }

        var workspace = new AnalysisWorkspace(reducedLength, paddedLength);
        _previousAnalysisWorkspace = _mostRecentAnalysisWorkspace;
        _mostRecentAnalysisWorkspace = workspace;
        return workspace;
    }

    internal int RetainedAnalysisWorkspaceCount
        => (_mostRecentAnalysisWorkspace is null ? 0 : 1)
            + (_previousAnalysisWorkspace is null ? 0 : 1);

    internal bool HasRetainedAnalysisWorkspace(int reducedLength, int paddedLength)
        => Matches(_mostRecentAnalysisWorkspace, reducedLength, paddedLength)
            || Matches(_previousAnalysisWorkspace, reducedLength, paddedLength);

    private static bool Matches(
        AnalysisWorkspace? workspace,
        int reducedLength,
        int paddedLength)
        => workspace is not null
            && workspace.Reduced.Length == reducedLength
            && workspace.Padded.Length == paddedLength;

    private static void BuildPaddedInput(ReadOnlySpan<double> data, Span<double> padded)
    {
        int padding = Math.Min(EnvelopePadding, data.Length);
        if (padded.Length != data.Length + padding)
        {
            throw new ArgumentException("Padded workspace length did not match the input.", nameof(padded));
        }

        for (int i = 0; i < padding; i++)
        {
            padded[i] = data[padding - 1 - i];
        }

        data.CopyTo(padded[padding..]);
    }

    private static void Downsample(ReadOnlySpan<double> data, int divisor, Span<double> output)
    {
        int expectedLength = (data.Length + divisor - 1) / divisor;
        if (output.Length != expectedLength)
        {
            throw new ArgumentException("Downsample workspace length did not match the input.", nameof(output));
        }

        if (divisor == 1)
        {
            data.CopyTo(output);
            return;
        }

        for (int i = 0, source = 0; i < output.Length; i++, source += divisor)
        {
            output[i] = data[source];
        }
    }

    private static int PythonSign(double value)
        => value > 0.0 ? 1 : value < 0.0 ? -1 : 0;

    internal static double MinimumFloat64(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            throw new InvalidOperationException("Sequence contains no elements");
        }

        int index = 0;
        double minimum = values[index++];
        while (double.IsNaN(minimum))
        {
            if (index >= values.Length)
            {
                return minimum;
            }

            minimum = values[index++];
        }

        for (; index < values.Length; index++)
        {
            double value = values[index];
            if (double.IsNaN(value))
            {
                return value;
            }

            if (value < minimum)
            {
                minimum = value;
            }
        }

        return minimum;
    }

    private static double Median(IEnumerable<double> values)
        => NumpyReduction.MedianFloat64(values.ToArray());

    private static double Median(ReadOnlySpan<double> values)
        => NumpyReduction.MedianFloat64(values);

    private sealed class AnalysisWorkspace
    {
        public AnalysisWorkspace(int reducedLength, int paddedLength)
        {
            Reduced = new double[reducedLength];
            Padded = new double[paddedLength];
            Forward = new double[paddedLength];
            Reverse = new double[paddedLength];
            Envelope = new double[reducedLength];
            PowerRatio = new double[paddedLength];
        }

        public double[] Reduced { get; }

        public double[] Padded { get; }

        public double[] Forward { get; }

        public double[] Reverse { get; }

        public double[] Envelope { get; }

        public double[] PowerRatio { get; }
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
