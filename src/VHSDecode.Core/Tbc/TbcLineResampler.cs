using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.Tbc;

public enum TbcLineInterpolationMethod
{
    Linear,
    Quadratic,
    Cubic
}

public sealed class TbcLineResampler
{
    private const int ParallelSampleThreshold = 64 * 1024;
    private const int MinimumParallelSamplesPerWorker = 16 * 1024;
    private const int SincTapCount = 16;
    private const int SincPhaseCount = 65536;
    private const double KaiserBeta = 5.0;
    private const string SincLookupResourceName = "VHSDecode.Core.Tbc.Resources.sinc_lut.npz";
    private static readonly Lazy<float[]> SincLookup = new(LoadSincLookup);
    private readonly double? _nominalInputLineLength;
    private readonly int _workerThreads;

    internal sealed class ResamplingPlan : IDisposable
    {
        private readonly TbcLineResampler _owner;
        private readonly bool _pooled;
        private int _disposed;

        internal ResamplingPlan(
            TbcLineResampler owner,
            double[] sourcePositions,
            double[] levelAdjusts,
            int prefixSamples,
            int destinationLength,
            bool pooled)
        {
            _owner = owner;
            SourcePositions = sourcePositions;
            LevelAdjusts = levelAdjusts;
            PrefixSamples = prefixSamples;
            DestinationLength = destinationLength;
            _pooled = pooled;
        }

        internal double[] SourcePositions { get; }

        internal double[] LevelAdjusts { get; }

        internal int PrefixSamples { get; }

        internal int DestinationLength { get; }

        internal void ValidateOwner(TbcLineResampler owner)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                throw new ArgumentException("The resampling plan belongs to a different resampler.", nameof(owner));
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_pooled)
            {
                return;
            }

            ArrayPool<double>.Shared.Return(SourcePositions);
            ArrayPool<double>.Shared.Return(LevelAdjusts);
        }
    }

    public TbcLineResampler(
        int outputLineLength,
        TbcLineInterpolationMethod interpolationMethod = TbcLineInterpolationMethod.Linear,
        double wowLevelAdjustSmoothing = 0.0,
        double? nominalInputLineLength = null,
        int workerThreads = 1)
    {
        if (outputLineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLineLength));
        }

        OutputLineLength = outputLineLength;
        InterpolationMethod = interpolationMethod;
        WowLevelAdjustSmoothing = Math.Max(0.0, wowLevelAdjustSmoothing);
        if (nominalInputLineLength.HasValue
            && (!double.IsFinite(nominalInputLineLength.Value) || nominalInputLineLength.Value <= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(nominalInputLineLength));
        }

        _nominalInputLineLength = nominalInputLineLength;
        _workerThreads = Math.Max(0, workerThreads);
    }

    public int OutputLineLength { get; }

    public TbcLineInterpolationMethod InterpolationMethod { get; }

    public double WowLevelAdjustSmoothing { get; }

    public static Range GetOutputLineRange(int oneBasedLine, int outputLineLength)
    {
        if (oneBasedLine <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(oneBasedLine));
        }

        if (outputLineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLineLength));
        }

        int start = checked(outputLineLength * (oneBasedLine - 1));
        return new Range(start, start + outputLineLength);
    }

    public double[] ResampleLine(ReadOnlySpan<double> source, IReadOnlyList<double> lineLocations, int line)
    {
        var output = new double[OutputLineLength];
        ResampleLine(source, lineLocations, line, output);
        return output;
    }

    public void ResampleLine(
        ReadOnlySpan<double> source,
        IReadOnlyList<double> lineLocations,
        int line,
        Span<double> destination)
    {
        if (destination.Length != OutputLineLength)
        {
            throw new ArgumentException("Destination length must match the configured output line length.", nameof(destination));
        }

        ILineLocationInterpolator interpolator = BuildInterpolator(source, lineLocations);
        ResampleLine(source, interpolator, line, destination);
    }

    public double[] ResampleLines(ReadOnlySpan<double> source, IReadOnlyList<double> lineLocations, int firstLine, int lineCount)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        using ResamplingPlan plan = PrepareLineResampling(lineLocations, firstLine, lineCount);
        return ResamplePrepared(source, plan);
    }

    internal void ResampleLinePrefixes(
        ReadOnlySpan<double> source,
        IReadOnlyList<double> lineLocations,
        int firstLine,
        int lineCount,
        int samplesPerLine,
        double[] destination)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        ArgumentNullException.ThrowIfNull(destination);
        if (lineCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineCount));
        }

        if (samplesPerLine <= 0 || samplesPerLine > OutputLineLength)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesPerLine));
        }

        int destinationLength = checked(OutputLineLength * lineCount);
        if (destination.Length != destinationLength)
        {
            throw new ArgumentException(
                "Destination length must match the requested output lines.",
                nameof(destination));
        }

        ILineLocationInterpolator interpolator = BuildInterpolator(lineLocations);
        if (firstLine < 0 || firstLine + lineCount >= interpolator.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLine));
        }

        if (interpolator is not LinearLineLocationInterpolator linear)
        {
            using ResamplingPlan plan = PrepareResampling(
                interpolator,
                firstLine,
                destinationLength);
            ResamplePreparedLinePrefixes(source, plan, samplesPerLine, destination);
            return;
        }

        int compactLength = checked(samplesPerLine * lineCount);
        double[] sourcePositions = ArrayPool<double>.Shared.Rent(compactLength);
        double[] levelAdjusts = ArrayPool<double>.Shared.Rent(compactLength);
        try
        {
            void BuildSourcePositions()
            {
                for (int line = 0; line < lineCount; line++)
                {
                    linear.FillOutputPositions(
                        checked((firstLine + line) * OutputLineLength),
                        OutputLineLength,
                        sourcePositions.AsSpan(line * samplesPerLine, samplesPerLine));
                }
            }

            if (_workerThreads > 1 && compactLength >= ParallelSampleThreshold)
            {
                Parallel.Invoke(
                    new ParallelOptions { MaxDegreeOfParallelism = 2 },
                    BuildSourcePositions,
                    () => BuildLinearPrefixLevelAdjusts(
                        linear,
                        firstLine,
                        lineCount,
                        samplesPerLine,
                        levelAdjusts));
            }
            else
            {
                BuildSourcePositions();
                BuildLinearPrefixLevelAdjusts(
                    linear,
                    firstLine,
                    lineCount,
                    samplesPerLine,
                    levelAdjusts);
            }

            ResampleCompactLinePrefixes(
                source,
                sourcePositions,
                levelAdjusts,
                lineCount,
                samplesPerLine,
                destination);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sourcePositions);
            ArrayPool<double>.Shared.Return(levelAdjusts);
        }
    }

    internal ResamplingPlan PrepareLineResampling(
        IReadOnlyList<double> lineLocations,
        int firstLine,
        int lineCount)
    {
        if (lineCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineCount));
        }

        ILineLocationInterpolator interpolator = BuildInterpolator(lineLocations);
        if (firstLine < 0 || firstLine + lineCount >= interpolator.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLine));
        }

        int destinationLength = checked(OutputLineLength * lineCount);
        return PrepareResampling(interpolator, firstLine, destinationLength);
    }

    internal double[] ResamplePrepared(ReadOnlySpan<double> source, ResamplingPlan plan)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        ArgumentNullException.ThrowIfNull(plan);
        plan.ValidateOwner(this);
        var output = new double[plan.DestinationLength];
        ResampleSamples(source, plan, output);
        return output;
    }

    internal double[] ResamplePreparedShifted(
        ReadOnlySpan<double> source,
        ResamplingPlan plan,
        double sourcePositionShift)
    {
        if (sourcePositionShift == 0.0)
        {
            return ResamplePrepared(source, plan);
        }

        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        if (!double.IsFinite(sourcePositionShift))
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePositionShift));
        }

        ArgumentNullException.ThrowIfNull(plan);
        plan.ValidateOwner(this);
        var output = new double[plan.DestinationLength];
        ResampleSamplesShifted(source, plan, sourcePositionShift, output);
        return output;
    }

    internal ushort[] ResamplePreparedToUInt16(
        ReadOnlySpan<double> source,
        ResamplingPlan plan,
        VideoOutputConverter converter)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(converter);
        plan.ValidateOwner(this);
        var output = new ushort[plan.DestinationLength];
        ResampleSamplesToUInt16(source, plan, converter, output);
        return output;
    }

    internal void ResamplePreparedToUInt16(
        ReadOnlySpan<double> source,
        ResamplingPlan plan,
        VideoOutputConverter converter,
        ushort[] destination)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(destination);
        plan.ValidateOwner(this);
        if (destination.Length != plan.DestinationLength)
        {
            throw new ArgumentException(
                "Destination length must match the prepared resampling plan.",
                nameof(destination));
        }

        ResampleSamplesToUInt16(source, plan, converter, destination);
    }

    internal void ResamplePrepared(
        ReadOnlySpan<double> source,
        ResamplingPlan plan,
        double[] destination)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        plan.ValidateOwner(this);
        if (destination.Length != plan.DestinationLength)
        {
            throw new ArgumentException(
                "Destination length must match the prepared resampling plan.",
                nameof(destination));
        }

        ResampleSamples(source, plan, destination);
    }

    internal void ResamplePreparedLinePrefixes(
        ReadOnlySpan<double> source,
        ResamplingPlan plan,
        int samplesPerLine,
        double[] destination)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        plan.ValidateOwner(this);
        if (destination.Length != plan.DestinationLength)
        {
            throw new ArgumentException(
                "Destination length must match the prepared resampling plan.",
                nameof(destination));
        }

        if (samplesPerLine <= 0 || samplesPerLine > OutputLineLength)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesPerLine));
        }

        if (destination.Length % OutputLineLength != 0)
        {
            throw new ArgumentException(
                "Prepared destination length must contain complete output lines.",
                nameof(plan));
        }

        ResampleLinePrefixes(source, plan, samplesPerLine, destination);
    }

    internal void ResamplePreparedShifted(
        ReadOnlySpan<double> source,
        ResamplingPlan plan,
        double sourcePositionShift,
        double[] destination)
    {
        if (sourcePositionShift == 0.0)
        {
            ResamplePrepared(source, plan, destination);
            return;
        }

        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        if (!double.IsFinite(sourcePositionShift))
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePositionShift));
        }

        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        plan.ValidateOwner(this);
        if (destination.Length != plan.DestinationLength)
        {
            throw new ArgumentException(
                "Destination length must match the prepared resampling plan.",
                nameof(destination));
        }

        ResampleSamplesShifted(source, plan, sourcePositionShift, destination);
    }

    private void ResampleLine(
        ReadOnlySpan<double> source,
        ILineLocationInterpolator interpolator,
        int line,
        Span<double> destination)
    {
        if (line < 0 || line + 1 >= interpolator.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        ResampleSamples(source, interpolator, line, destination);
    }

    private ILineLocationInterpolator BuildInterpolator(
        ReadOnlySpan<double> source,
        IReadOnlyList<double> lineLocations)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Source must contain at least one sample.", nameof(source));
        }

        return BuildInterpolator(lineLocations);
    }

    private ILineLocationInterpolator BuildInterpolator(IReadOnlyList<double> lineLocations)
    {
        ArgumentNullException.ThrowIfNull(lineLocations);

        if (lineLocations.Count < 2)
        {
            throw new ArgumentException("At least two line locations are required.", nameof(lineLocations));
        }

        for (int i = 0; i < lineLocations.Count; i++)
        {
            if (!double.IsFinite(lineLocations[i]))
            {
                throw new ArgumentException("Line locations must be finite.", nameof(lineLocations));
            }
        }

        double nominalLineLength = _nominalInputLineLength
            ?? Math.Max(1.0, MedianLineLength(lineLocations));
        return InterpolationMethod switch
        {
            TbcLineInterpolationMethod.Quadratic when lineLocations.Count >= 3 =>
                new ScipySplineLineLocationInterpolator(lineLocations, nominalLineLength, degree: 2, natural: false),
            TbcLineInterpolationMethod.Cubic when lineLocations.Count >= 3 =>
                new ScipySplineLineLocationInterpolator(lineLocations, nominalLineLength, degree: 3, natural: true),
            _ => new LinearLineLocationInterpolator(lineLocations, nominalLineLength)
        };
    }

    private void ResampleSamples(
        ReadOnlySpan<double> source,
        ILineLocationInterpolator interpolator,
        int firstLine,
        Span<double> destination)
    {
        using ResamplingPlan preparation =
            PrepareResampling(interpolator, firstLine, destination.Length);
        ResampleSamples(source, preparation, destination);
    }

    private unsafe void ResampleSamples(
        ReadOnlySpan<double> source,
        ResamplingPlan preparation,
        Span<double> destination)
    {
        preparation.ValidateOwner(this);
        double[] sourcePositions = preparation.SourcePositions;
        double[] levelAdjusts = preparation.LevelAdjusts;
        int prefixSamples = preparation.PrefixSamples;
        float[] sincLookup = SincLookup.Value;
        fixed (double* sourcePointer = source)
        fixed (float* sincLookupPointer = sincLookup)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = (float)(SampleSinc(
                        sourcePointer,
                        source.Length,
                        sourcePositions[i],
                        sincLookupPointer)
                    * levelAdjusts[prefixSamples + i]);
            }
        }
    }

    private void ResampleSamples(
        ReadOnlySpan<double> source,
        ILineLocationInterpolator interpolator,
        int firstLine,
        double[] destination)
    {
        using ResamplingPlan preparation =
            PrepareResampling(interpolator, firstLine, destination.Length);
        ResampleSamples(source, preparation, destination);
    }

    private unsafe void ResampleSamples(
        ReadOnlySpan<double> source,
        ResamplingPlan preparation,
        double[] destination)
    {
        preparation.ValidateOwner(this);
        double[] sourcePositions = preparation.SourcePositions;
        double[] levelAdjusts = preparation.LevelAdjusts;
        int prefixSamples = preparation.PrefixSamples;
        float[] sincLookup = SincLookup.Value;
        fixed (double* sourcePointer = source)
        fixed (float* sincLookupPointer = sincLookup)
        {
            if (_workerThreads <= 1 || destination.Length < ParallelSampleThreshold)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (float)(SampleSinc(
                            sourcePointer,
                            source.Length,
                            sourcePositions[i],
                            sincLookupPointer)
                        * levelAdjusts[prefixSamples + i]);
                }

                return;
            }

            nint sourceAddress = (nint)sourcePointer;
            nint sincLookupAddress = (nint)sincLookupPointer;
            int sourceLength = source.Length;
            Parallel.ForEach(
                Partitioner.Create(0, destination.Length),
                new ParallelOptions { MaxDegreeOfParallelism = _workerThreads },
                range =>
                {
                    var parallelSource = (double*)sourceAddress;
                    var parallelSincLookup = (float*)sincLookupAddress;
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        destination[i] = (float)(SampleSinc(
                                parallelSource,
                                sourceLength,
                                sourcePositions[i],
                                parallelSincLookup)
                            * levelAdjusts[prefixSamples + i]);
                    }
                });
        }
    }

    private unsafe void ResampleSamplesShifted(
        ReadOnlySpan<double> source,
        ResamplingPlan preparation,
        double sourcePositionShift,
        double[] destination)
    {
        preparation.ValidateOwner(this);
        double[] sourcePositions = preparation.SourcePositions;
        double[] levelAdjusts = preparation.LevelAdjusts;
        int prefixSamples = preparation.PrefixSamples;
        float[] sincLookup = SincLookup.Value;
        fixed (double* sourcePointer = source)
        fixed (float* sincLookupPointer = sincLookup)
        {
            if (_workerThreads <= 1 || destination.Length < ParallelSampleThreshold)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (float)(SampleSinc(
                            sourcePointer,
                            source.Length,
                            sourcePositions[i] + sourcePositionShift,
                            sincLookupPointer)
                        * levelAdjusts[prefixSamples + i]);
                }

                return;
            }

            nint sourceAddress = (nint)sourcePointer;
            nint sincLookupAddress = (nint)sincLookupPointer;
            int sourceLength = source.Length;
            Parallel.ForEach(
                Partitioner.Create(0, destination.Length),
                new ParallelOptions { MaxDegreeOfParallelism = _workerThreads },
                range =>
                {
                    var parallelSource = (double*)sourceAddress;
                    var parallelSincLookup = (float*)sincLookupAddress;
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        destination[i] = (float)(SampleSinc(
                                parallelSource,
                                sourceLength,
                                sourcePositions[i] + sourcePositionShift,
                                parallelSincLookup)
                            * levelAdjusts[prefixSamples + i]);
                    }
                });
        }
    }

    private unsafe void ResampleSamplesToUInt16(
        ReadOnlySpan<double> source,
        ResamplingPlan preparation,
        VideoOutputConverter converter,
        ushort[] destination)
    {
        preparation.ValidateOwner(this);
        if (destination.Length != preparation.DestinationLength)
        {
            throw new ArgumentException(
                "Destination length must match the prepared resampling plan.",
                nameof(destination));
        }

        double[] sourcePositions = preparation.SourcePositions;
        double[] levelAdjusts = preparation.LevelAdjusts;
        int prefixSamples = preparation.PrefixSamples;
        float[] sincLookup = SincLookup.Value;
        VideoOutputConverter.FastMathConversion conversion = converter.CreateFastMathConversion();
        fixed (double* sourcePointer = source)
        fixed (float* sincLookupPointer = sincLookup)
        {
            if (_workerThreads <= 1 || destination.Length < ParallelSampleThreshold)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    double resampled = (float)(SampleSinc(
                            sourcePointer,
                            source.Length,
                            sourcePositions[i],
                            sincLookupPointer)
                        * levelAdjusts[prefixSamples + i]);
                    destination[i] = conversion.Convert(resampled);
                }

                return;
            }

            nint sourceAddress = (nint)sourcePointer;
            nint sincLookupAddress = (nint)sincLookupPointer;
            int sourceLength = source.Length;
            Parallel.ForEach(
                Partitioner.Create(0, destination.Length),
                new ParallelOptions { MaxDegreeOfParallelism = _workerThreads },
                range =>
                {
                    var parallelSource = (double*)sourceAddress;
                    var parallelSincLookup = (float*)sincLookupAddress;
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        double resampled = (float)(SampleSinc(
                                parallelSource,
                                sourceLength,
                                sourcePositions[i],
                                parallelSincLookup)
                            * levelAdjusts[prefixSamples + i]);
                        destination[i] = conversion.Convert(resampled);
                    }
                });
        }
    }

    private ResamplingPlan PrepareResampling(
        ILineLocationInterpolator interpolator,
        int firstLine,
        int destinationLength)
    {
        int prefixSamples = checked(firstLine * OutputLineLength);
        int scaledSampleCount = checked(prefixSamples + destinationLength);
        if (interpolator is LinearLineLocationInterpolator linear
            && scaledSampleCount % OutputLineLength == 0)
        {
            double[] sourcePositions = ArrayPool<double>.Shared.Rent(destinationLength);
            double[] levelAdjusts = ArrayPool<double>.Shared.Rent(scaledSampleCount);
            try
            {
                void BuildSourcePositions()
                {
                    linear.FillOutputPositions(
                        prefixSamples,
                        OutputLineLength,
                        sourcePositions.AsSpan(0, destinationLength));
                }

                if (_workerThreads > 1 && destinationLength >= ParallelSampleThreshold)
                {
                    Parallel.Invoke(
                        new ParallelOptions { MaxDegreeOfParallelism = 2 },
                        BuildSourcePositions,
                        () => BuildLinearLevelAdjusts(linear, scaledSampleCount, levelAdjusts));
                }
                else
                {
                    BuildSourcePositions();
                    BuildLinearLevelAdjusts(linear, scaledSampleCount, levelAdjusts);
                }

                return new ResamplingPlan(
                    this,
                    sourcePositions,
                    levelAdjusts,
                    prefixSamples,
                    destinationLength,
                    pooled: true);
            }
            catch
            {
                ArrayPool<double>.Shared.Return(sourcePositions);
                ArrayPool<double>.Shared.Return(levelAdjusts);
                throw;
            }
        }

        var allocatedSourcePositions = new double[destinationLength];
        var wowFactors = new double[scaledSampleCount];
        for (int i = 0; i < scaledSampleCount; i++)
        {
            double factor = interpolator.EvaluateOutputDerivative(i, OutputLineLength);
            wowFactors[i] = factor;
            if (i >= prefixSamples)
            {
                allocatedSourcePositions[i - prefixSamples] = interpolator.EvaluateOutputPosition(
                    i,
                    OutputLineLength);
            }
        }

        return new ResamplingPlan(
            this,
            allocatedSourcePositions,
            BuildLevelAdjusts(wowFactors),
            prefixSamples,
            destinationLength,
            pooled: false);
    }

    private double[] BuildLevelAdjusts(double[] wowFactors)
    {
        if (wowFactors.Length == 0)
        {
            return [];
        }

        double[] levelAdjusts = ReplaceWowFactorOutliers(wowFactors);
        SmoothLevelAdjusts(levelAdjusts);
        return levelAdjusts;
    }

    private void BuildLinearLevelAdjusts(
        ILineLocationInterpolator interpolator,
        int sampleCount,
        double[] levelAdjusts)
    {
        if (levelAdjusts.Length < sampleCount)
        {
            throw new ArgumentException("Level-adjust buffer is shorter than the sample count.", nameof(levelAdjusts));
        }

        int lineCount = sampleCount / OutputLineLength;
        if (lineCount == 0)
        {
            return;
        }

        double[] lineFactors = ArrayPool<double>.Shared.Rent(lineCount);
        double[] medianScratch = ArrayPool<double>.Shared.Rent(lineCount);
        double[] deviationScratch = ArrayPool<double>.Shared.Rent(lineCount);
        try
        {
            for (int line = 0; line < lineCount; line++)
            {
                lineFactors[line] = interpolator.EvaluateOutputDerivative(
                    line * OutputLineLength,
                    OutputLineLength);
            }

            ReadOnlySpan<double> factors = lineFactors.AsSpan(0, lineCount);
            double median = NumpyReduction.MedianFloat64(factors, medianScratch);
            for (int line = 0; line < lineCount; line++)
            {
                deviationScratch[line] = Math.Abs(lineFactors[line] - median);
            }

            double mad = NumpyReduction.MedianFloat64(
                deviationScratch.AsSpan(0, lineCount),
                medianScratch);
            double threshold = mad > 0.0 ? 15.0 * mad : 0.001;
            for (int line = 0; line < lineCount; line++)
            {
                double factor = lineFactors[line];
                double adjustedFactor = Math.Abs(factor - median) > threshold
                    ? median
                    : factor;
                Array.Fill(
                    levelAdjusts,
                    adjustedFactor,
                    line * OutputLineLength,
                    OutputLineLength);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(lineFactors);
            ArrayPool<double>.Shared.Return(medianScratch);
            ArrayPool<double>.Shared.Return(deviationScratch);
        }

        SmoothLevelAdjusts(levelAdjusts.AsSpan(0, sampleCount));
    }

    private void BuildLinearPrefixLevelAdjusts(
        ILineLocationInterpolator interpolator,
        int firstLine,
        int lineCount,
        int samplesPerLine,
        double[] levelAdjusts)
    {
        int scaledLineCount = checked(firstLine + lineCount);
        int compactLength = checked(lineCount * samplesPerLine);
        if (levelAdjusts.Length < compactLength)
        {
            throw new ArgumentException(
                "Level-adjust buffer is shorter than the compact sample count.",
                nameof(levelAdjusts));
        }

        if (scaledLineCount == 0)
        {
            return;
        }

        double[] lineFactors = ArrayPool<double>.Shared.Rent(scaledLineCount);
        double[] medianScratch = ArrayPool<double>.Shared.Rent(scaledLineCount);
        double[] deviationScratch = ArrayPool<double>.Shared.Rent(scaledLineCount);
        try
        {
            for (int line = 0; line < scaledLineCount; line++)
            {
                lineFactors[line] = interpolator.EvaluateOutputDerivative(
                    line * OutputLineLength,
                    OutputLineLength);
            }

            ReadOnlySpan<double> factors = lineFactors.AsSpan(0, scaledLineCount);
            double median = NumpyReduction.MedianFloat64(factors, medianScratch);
            for (int line = 0; line < scaledLineCount; line++)
            {
                deviationScratch[line] = Math.Abs(lineFactors[line] - median);
            }

            double mad = NumpyReduction.MedianFloat64(
                deviationScratch.AsSpan(0, scaledLineCount),
                medianScratch);
            double threshold = mad > 0.0 ? 15.0 * mad : 0.001;
            for (int line = 0; line < scaledLineCount; line++)
            {
                double factor = lineFactors[line];
                lineFactors[line] = Math.Abs(factor - median) > threshold
                    ? median
                    : factor;
            }

            if (WowLevelAdjustSmoothing <= 0.0)
            {
                for (int line = 0; line < lineCount; line++)
                {
                    Array.Fill(
                        levelAdjusts,
                        lineFactors[firstLine + line],
                        line * samplesPerLine,
                        samplesPerLine);
                }

                return;
            }

            double alpha = 1.0 / (WowLevelAdjustSmoothing * OutputLineLength);
            double previous = lineFactors[0];
            for (int line = 0; line < scaledLineCount; line++)
            {
                double factor = lineFactors[line];
                int compactStart = (line - firstLine) * samplesPerLine;
                for (int sample = 0; sample < OutputLineLength; sample++)
                {
                    if (line != 0 || sample != 0)
                    {
                        previous = Math.FusedMultiplyAdd(
                            factor - previous,
                            alpha,
                            previous);
                    }

                    if (line >= firstLine && sample < samplesPerLine)
                    {
                        levelAdjusts[compactStart + sample] = previous;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(lineFactors);
            ArrayPool<double>.Shared.Return(medianScratch);
            ArrayPool<double>.Shared.Return(deviationScratch);
        }
    }

    private static double[] ReplaceWowFactorOutliers(double[] wowFactors)
    {
        double median = Median(wowFactors);
        var deviations = new double[wowFactors.Length];
        for (int i = 0; i < wowFactors.Length; i++)
        {
            deviations[i] = Math.Abs(wowFactors[i] - median);
        }

        double mad = Median(deviations);
        double threshold = mad > 0.0 ? 15.0 * mad : 0.001;
        var levelAdjusts = new double[wowFactors.Length];
        for (int i = 0; i < wowFactors.Length; i++)
        {
            levelAdjusts[i] = Math.Abs(wowFactors[i] - median) > threshold ? median : wowFactors[i];
        }

        return levelAdjusts;
    }

    private void SmoothLevelAdjusts(Span<double> levelAdjusts)
    {
        if (WowLevelAdjustSmoothing > 0.0)
        {
            double alpha = 1.0 / (WowLevelAdjustSmoothing * OutputLineLength);
            for (int i = 1; i < levelAdjusts.Length; i++)
            {
                double previous = levelAdjusts[i - 1];
                levelAdjusts[i] = Math.FusedMultiplyAdd(
                    levelAdjusts[i] - previous,
                    alpha,
                    previous);
            }
        }
    }

    private unsafe void ResampleLinePrefixes(
        ReadOnlySpan<double> source,
        ResamplingPlan preparation,
        int samplesPerLine,
        double[] destination)
    {
        preparation.ValidateOwner(this);
        double[] sourcePositions = preparation.SourcePositions;
        double[] levelAdjusts = preparation.LevelAdjusts;
        int prefixSamples = preparation.PrefixSamples;
        int lineCount = destination.Length / OutputLineLength;
        int resampledSampleCount = checked(lineCount * samplesPerLine);
        float[] sincLookup = SincLookup.Value;
        fixed (double* sourcePointer = source)
        fixed (float* sincLookupPointer = sincLookup)
        {
            if (_workerThreads <= 1 || resampledSampleCount < ParallelSampleThreshold)
            {
                for (int line = 0; line < lineCount; line++)
                {
                    int lineStart = line * OutputLineLength;
                    int lineEnd = lineStart + samplesPerLine;
                    for (int i = lineStart; i < lineEnd; i++)
                    {
                        destination[i] = (float)(SampleSinc(
                                sourcePointer,
                                source.Length,
                                sourcePositions[i],
                                sincLookupPointer)
                            * levelAdjusts[prefixSamples + i]);
                    }
                }

                return;
            }

            nint sourceAddress = (nint)sourcePointer;
            nint sincLookupAddress = (nint)sincLookupPointer;
            int sourceLength = source.Length;
            int workerCount = Math.Min(_workerThreads, lineCount);
            Parallel.For(
                0,
                workerCount,
                new ParallelOptions { MaxDegreeOfParallelism = workerCount },
                workerIndex =>
                {
                    var parallelSource = (double*)sourceAddress;
                    var parallelSincLookup = (float*)sincLookupAddress;
                    int firstLine = (lineCount * workerIndex) / workerCount;
                    int lastLine = (lineCount * (workerIndex + 1)) / workerCount;
                    for (int line = firstLine; line < lastLine; line++)
                    {
                        int lineStart = line * OutputLineLength;
                        int lineEnd = lineStart + samplesPerLine;
                        for (int i = lineStart; i < lineEnd; i++)
                        {
                            destination[i] = (float)(SampleSinc(
                                    parallelSource,
                                    sourceLength,
                                    sourcePositions[i],
                                    parallelSincLookup)
                                * levelAdjusts[prefixSamples + i]);
                        }
                    }
                });
        }
    }

    private unsafe void ResampleCompactLinePrefixes(
        ReadOnlySpan<double> source,
        double[] sourcePositions,
        double[] levelAdjusts,
        int lineCount,
        int samplesPerLine,
        double[] destination)
    {
        int compactLength = checked(lineCount * samplesPerLine);
        float[] sincLookup = SincLookup.Value;
        fixed (double* sourcePointer = source)
        fixed (float* sincLookupPointer = sincLookup)
        {
            int maximumUsefulWorkers = compactLength == 0
                ? 1
                : 1 + ((compactLength - 1) / MinimumParallelSamplesPerWorker);
            int workerCount = Math.Min(
                Math.Min(_workerThreads, lineCount),
                maximumUsefulWorkers);
            if (workerCount <= 1 || compactLength < ParallelSampleThreshold)
            {
                for (int line = 0; line < lineCount; line++)
                {
                    int compactStart = line * samplesPerLine;
                    int destinationStart = line * OutputLineLength;
                    for (int sample = 0; sample < samplesPerLine; sample++)
                    {
                        int compactIndex = compactStart + sample;
                        destination[destinationStart + sample] = (float)(SampleSinc(
                                sourcePointer,
                                source.Length,
                                sourcePositions[compactIndex],
                                sincLookupPointer)
                            * levelAdjusts[compactIndex]);
                    }
                }

                return;
            }

            nint sourceAddress = (nint)sourcePointer;
            nint sincLookupAddress = (nint)sincLookupPointer;
            int sourceLength = source.Length;
            Parallel.For(
                0,
                workerCount,
                new ParallelOptions { MaxDegreeOfParallelism = workerCount },
                workerIndex =>
                {
                    var parallelSource = (double*)sourceAddress;
                    var parallelSincLookup = (float*)sincLookupAddress;
                    int firstWorkerLine = (lineCount * workerIndex) / workerCount;
                    int lastWorkerLine = (lineCount * (workerIndex + 1)) / workerCount;
                    for (int line = firstWorkerLine; line < lastWorkerLine; line++)
                    {
                        int compactStart = line * samplesPerLine;
                        int destinationStart = line * OutputLineLength;
                        for (int sample = 0; sample < samplesPerLine; sample++)
                        {
                            int compactIndex = compactStart + sample;
                            destination[destinationStart + sample] = (float)(SampleSinc(
                                    parallelSource,
                                    sourceLength,
                                    sourcePositions[compactIndex],
                                    parallelSincLookup)
                                * levelAdjusts[compactIndex]);
                        }
                    }
                });
        }
    }

    private static double MedianLineLength(IReadOnlyList<double> lineLocations)
    {
        var lengths = new double[lineLocations.Count - 1];
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = lineLocations[i + 1] - lineLocations[i];
        }

        return Median(lengths);
    }

    private static double Median(double[] values)
        => NumpyReduction.MedianFloat64(values);

    private static unsafe double SampleSinc(
        double* source,
        int sourceLength,
        double position,
        float* weights)
    {
        if (!double.IsFinite(position))
        {
            return 0.0;
        }

        float coord = (float)position;
        int coordInt = (int)coord;
        float fraction = coord - coordInt;
        float phasePosition = fraction * SincPhaseCount;
        int phaseStartIndex = (int)phasePosition;
        int phaseEndIndex = phaseStartIndex + 1;
        float alpha = phasePosition - phaseStartIndex;
        int phaseStart = WrapNegativeIndex(phaseStartIndex, SincPhaseCount + 1);
        int phaseEnd = WrapNegativeIndex(phaseEndIndex, SincPhaseCount + 1);
        int weightStart = phaseStart * SincTapCount;
        int weightEnd = phaseEnd * SincTapCount;
        int sampleStart = coordInt - ((SincTapCount / 2) - 1);
        double result = 0.0;
        if (sourceLength >= SincTapCount
            && (uint)sampleStart <= (uint)(sourceLength - SincTapCount))
        {
            if (Avx.IsSupported && Fma.IsSupported)
            {
                return SampleSincInteriorAvxFma(
                    source + sampleStart,
                    weights + weightStart,
                    weights + weightEnd,
                    alpha);
            }

            for (int tap = 0; tap < SincTapCount; tap++)
            {
                float startWeight = weights[weightStart + tap];
                float weight = MathF.FusedMultiplyAdd(
                    alpha,
                    weights[weightEnd + tap] - startWeight,
                    startWeight);
                result += (float)source[sampleStart + tap] * weight;
            }

            return result;
        }

        for (int tap = 0; tap < SincTapCount; tap++)
        {
            float startWeight = weights[weightStart + tap];
            float weight = MathF.FusedMultiplyAdd(
                alpha,
                weights[weightEnd + tap] - startWeight,
                startWeight);
            int sampleIndex = sampleStart + tap;
            if (sampleIndex < 0)
            {
                sampleIndex += sourceLength;
            }

            sampleIndex = Math.Clamp(sampleIndex, 0, sourceLength - 1);
            result += (float)source[sampleIndex] * weight;
        }

        return result;
    }

    private static int WrapNegativeIndex(int index, int length)
    {
        if (index < 0)
        {
            index += length;
        }

        return Math.Clamp(index, 0, length - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [SkipLocalsInit]
    private static unsafe double SampleSincInteriorAvxFma(
        double* source,
        float* startWeights,
        float* endWeights,
        float alpha)
    {
        Vector256<float> alphaVector = Vector256.Create(alpha);
        Vector256<float> start0 = Avx.LoadVector256(startWeights);
        Vector256<float> start1 = Avx.LoadVector256(startWeights + 8);
        Vector256<float> weight0 = Fma.MultiplyAdd(
            alphaVector,
            Avx.Subtract(Avx.LoadVector256(endWeights), start0),
            start0);
        Vector256<float> weight1 = Fma.MultiplyAdd(
            alphaVector,
            Avx.Subtract(Avx.LoadVector256(endWeights + 8), start1),
            start1);
        Vector256<float> source0 = Vector256.Create(
            Avx.ConvertToVector128Single(Avx.LoadVector256(source)),
            Avx.ConvertToVector128Single(Avx.LoadVector256(source + 4)));
        Vector256<float> source1 = Vector256.Create(
            Avx.ConvertToVector128Single(Avx.LoadVector256(source + 8)),
            Avx.ConvertToVector128Single(Avx.LoadVector256(source + 12)));
        float* products = stackalloc float[SincTapCount];
        Avx.Store(products, Avx.Multiply(source0, weight0));
        Avx.Store(products + 8, Avx.Multiply(source1, weight1));

        double result = 0.0;
        result += products[0];
        result += products[1];
        result += products[2];
        result += products[3];
        result += products[4];
        result += products[5];
        result += products[6];
        result += products[7];
        result += products[8];
        result += products[9];
        result += products[10];
        result += products[11];
        result += products[12];
        result += products[13];
        result += products[14];
        result += products[15];

        return result;
    }

    private static float[] BuildKaiserSincLookup()
    {
        var lookup = new float[(SincPhaseCount + 1) * SincTapCount];
        int halfTaps = SincTapCount / 2;
        double i0Beta = BesselI0(KaiserBeta);
        var weights = new double[SincTapCount];
        for (int phaseIndex = 0; phaseIndex < SincPhaseCount; phaseIndex++)
        {
            double phase = (double)phaseIndex / SincPhaseCount;
            double sum = 0.0;
            for (int tap = 0; tap < SincTapCount; tap++)
            {
                int offset = (halfTaps - 1) - tap;
                double x = offset + phase;
                double weight = Sinc(x) * KaiserWindow(x, halfTaps, i0Beta);
                weights[tap] = weight;
                sum += weight;
            }

            int row = phaseIndex * SincTapCount;
            for (int tap = 0; tap < SincTapCount; tap++)
            {
                lookup[row + tap] = (float)(weights[tap] / sum);
            }
        }

        Array.Copy(
            lookup,
            (SincPhaseCount - 1) * SincTapCount,
            lookup,
            SincPhaseCount * SincTapCount,
            SincTapCount);
        return lookup;
    }

    private static float[] LoadSincLookup()
    {
        try
        {
            using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(SincLookupResourceName);
            if (resource is null)
            {
                return BuildKaiserSincLookup();
            }

            using var archive = new ZipArchive(resource, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? entry = archive.GetEntry("downscale_sinc_lut.npy");
            if (entry is null)
            {
                return BuildKaiserSincLookup();
            }

            using Stream input = entry.Open();
            Span<byte> prefix = stackalloc byte[8];
            input.ReadExactly(prefix);
            ReadOnlySpan<byte> expectedMagic = [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];
            if (!prefix[..6].SequenceEqual(expectedMagic))
            {
                return BuildKaiserSincLookup();
            }

            int headerLength;
            if (prefix[6] == 1)
            {
                Span<byte> length = stackalloc byte[2];
                input.ReadExactly(length);
                headerLength = BinaryPrimitives.ReadUInt16LittleEndian(length);
            }
            else if (prefix[6] is 2 or 3)
            {
                Span<byte> length = stackalloc byte[4];
                input.ReadExactly(length);
                headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(length));
            }
            else
            {
                return BuildKaiserSincLookup();
            }

            byte[] headerBytes = new byte[headerLength];
            input.ReadExactly(headerBytes);
            string header = Encoding.ASCII.GetString(headerBytes);
            if (!header.Contains("'descr': '<f4'", StringComparison.Ordinal)
                || !header.Contains("'fortran_order': False", StringComparison.Ordinal)
                || !header.Contains($"'shape': ({SincPhaseCount + 1}, {SincTapCount})", StringComparison.Ordinal))
            {
                return BuildKaiserSincLookup();
            }

            int valueCount = checked((SincPhaseCount + 1) * SincTapCount);
            byte[] data = new byte[checked(valueCount * sizeof(float))];
            input.ReadExactly(data);
            var lookup = new float[valueCount];
            if (BitConverter.IsLittleEndian)
            {
                Buffer.BlockCopy(data, 0, lookup, 0, data.Length);
            }
            else
            {
                for (int i = 0; i < lookup.Length; i++)
                {
                    int bits = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * sizeof(float), sizeof(float)));
                    lookup[i] = BitConverter.Int32BitsToSingle(bits);
                }
            }

            return lookup;
        }
        catch (InvalidDataException)
        {
            return BuildKaiserSincLookup();
        }
        catch (EndOfStreamException)
        {
            return BuildKaiserSincLookup();
        }
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
        {
            return 1.0;
        }

        double xPi = Math.PI * x;
        return Math.Sin(xPi) / xPi;
    }

    private static double KaiserWindow(double x, int halfTaps, double i0Beta)
    {
        double ratio = x / halfTaps;
        if (ratio is < -1.0 or > 1.0)
        {
            return 0.0;
        }

        return BesselI0(KaiserBeta * Math.Sqrt(1.0 - (ratio * ratio))) / i0Beta;
    }

    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double y = (x * x) / 4.0;
        double term = 1.0;
        for (int k = 1; k < 100; k++)
        {
            term *= y / (k * k);
            double next = sum + term;
            if (next == sum)
            {
                break;
            }

            sum = next;
        }

        return sum;
    }

    private interface ILineLocationInterpolator
    {
        int Count { get; }

        double NominalLineLength { get; }

        double Evaluate(double linePosition);

        double EvaluateDerivative(double linePosition, double inputScale);

        double EvaluateOutputPosition(int sampleIndex, int outputLineLength)
        {
            return Evaluate((double)sampleIndex / outputLineLength);
        }

        double EvaluateOutputDerivative(int sampleIndex, int outputLineLength)
        {
            return EvaluateDerivative(
                (double)sampleIndex / outputLineLength,
                1.0 / NominalLineLength);
        }
    }

    private sealed class LinearLineLocationInterpolator(
        IReadOnlyList<double> locations,
        double nominalLineLength) : ILineLocationInterpolator
    {
        public int Count => locations.Count;

        public double NominalLineLength => nominalLineLength;

        public double Evaluate(double linePosition)
        {
            int left = Math.Clamp((int)Math.Floor(linePosition), 0, locations.Count - 2);
            double fraction = Math.Clamp(linePosition - left, 0.0, 1.0);
            return locations[left] + ((locations[left + 1] - locations[left]) * fraction);
        }

        public void FillOutputPositions(
            int firstSampleIndex,
            int outputLineLength,
            Span<double> destination)
        {
            double outputScale = nominalLineLength / outputLineLength;
            int sampleIndex = firstSampleIndex;
            int destinationIndex = 0;
            while (destinationIndex < destination.Length)
            {
                int left = sampleIndex / outputLineLength;
                double leftLocation = locations[left];
                double locationDelta = locations[left + 1] - leftLocation;
                int samplesThisLine = Math.Min(
                    outputLineLength - (sampleIndex % outputLineLength),
                    destination.Length - destinationIndex);
                int destinationEnd = destinationIndex + samplesThisLine;
                while (destinationIndex < destinationEnd)
                {
                    double scaledPosition = sampleIndex * outputScale;
                    double fraction = Math.Clamp(
                        (scaledPosition - (left * nominalLineLength))
                            / nominalLineLength,
                        0.0,
                        1.0);
                    destination[destinationIndex] = leftLocation + (locationDelta * fraction);
                    destinationIndex++;
                    sampleIndex++;
                }
            }
        }

        public double EvaluateDerivative(double linePosition, double inputScale)
        {
            int left = Math.Clamp((int)Math.Floor(linePosition), 0, locations.Count - 2);
            return (locations[left + 1] * inputScale) - (locations[left] * inputScale);
        }
    }

    private sealed class ScipySplineLineLocationInterpolator : ILineLocationInterpolator
    {
        private readonly double[] _knots;
        private readonly double[] _coefficients;
        private readonly int _degree;

        public ScipySplineLineLocationInterpolator(
            IReadOnlyList<double> locations,
            double nominalLineLength,
            int degree,
            bool natural)
        {
            Count = locations.Count;
            NominalLineLength = nominalLineLength;
            _degree = degree;
            _coefficients = BuildCoefficients(locations, nominalLineLength, degree, natural, out _knots);
        }

        public int Count { get; }

        public double NominalLineLength { get; }

        public double Evaluate(double linePosition)
        {
            return EvaluateSpline(linePosition * NominalLineLength, derivativeOrder: 0);
        }

        public double EvaluateDerivative(double linePosition, double inputScale)
        {
            return EvaluateSpline(linePosition * NominalLineLength, derivativeOrder: 1);
        }

        public double EvaluateOutputPosition(int sampleIndex, int outputLineLength)
        {
            return EvaluateSpline(sampleIndex * (NominalLineLength / outputLineLength), derivativeOrder: 0);
        }

        public double EvaluateOutputDerivative(int sampleIndex, int outputLineLength)
        {
            return EvaluateSpline(sampleIndex * (NominalLineLength / outputLineLength), derivativeOrder: 1);
        }

        private double EvaluateSpline(double position, int derivativeOrder)
        {
            int interval = FindInterval(_knots, _degree, position, _degree);
            Span<double> work = stackalloc double[8];
            ComputeBasis(_knots, _degree, position, interval, derivativeOrder, work);
            double value = 0.0;
            for (int i = 0; i <= _degree; i++)
            {
                value += _coefficients[interval + i - _degree] * work[i];
            }

            return value;
        }

        private static double[] BuildCoefficients(
            IReadOnlyList<double> values,
            double nominalLineLength,
            int degree,
            bool natural,
            out double[] knots)
        {
            int count = values.Count;
            var expectedLocations = new double[count];
            for (int i = 0; i < count; i++)
            {
                expectedLocations[i] = i * nominalLineLength;
            }

            int leftDerivativeCount = natural ? 1 : 0;
            if (natural)
            {
                knots = new double[count + (2 * degree)];
                for (int i = 0; i < degree; i++)
                {
                    knots[i] = expectedLocations[0];
                    knots[knots.Length - 1 - i] = expectedLocations[^1];
                }

                expectedLocations.CopyTo(knots, degree);
            }
            else
            {
                knots = new double[count + degree + 1];
                for (int i = 0; i <= degree; i++)
                {
                    knots[i] = expectedLocations[0];
                    knots[knots.Length - 1 - i] = expectedLocations[^1];
                }

                var midpoints = new double[count - 1];
                for (int i = 0; i < midpoints.Length; i++)
                {
                    midpoints[i] = (expectedLocations[i + 1] + expectedLocations[i]) / 2.0;
                }

                for (int i = 1; i < midpoints.Length - 1; i++)
                {
                    knots[degree + i] = midpoints[i];
                }
            }

            int coefficientCount = knots.Length - degree - 1;
            var band = new double[(3 * degree) + 1, coefficientCount];
            var basisBuffer = new double[(2 * degree) + 2];
            for (int row = 0; row < count; row++)
            {
                int interval = FindInterval(knots, degree, expectedLocations[row], degree);
                ComputeBasis(
                    knots,
                    degree,
                    expectedLocations[row],
                    interval,
                    derivativeOrder: 0,
                    basisBuffer);
                int fullRow = row + leftDerivativeCount;
                for (int a = 0; a <= degree; a++)
                {
                    int column = interval - degree + a;
                    int bandRow = (2 * degree) + fullRow - column;
                    band[bandRow, column] = basisBuffer[a];
                }
            }

            if (natural)
            {
                FillDerivativeRow(knots, degree, expectedLocations[0], fullRow: 0, band, basisBuffer);
                FillDerivativeRow(knots, degree, expectedLocations[^1], coefficientCount - 1, band, basisBuffer);
            }

            var rightHandSide = new double[coefficientCount];
            for (int i = 0; i < values.Count; i++)
            {
                rightHandSide[i + leftDerivativeCount] = values[i];
            }

            SolveGeneralBand(band, degree, degree, rightHandSide);
            return rightHandSide;
        }

        private static void FillDerivativeRow(
            double[] knots,
            int degree,
            double position,
            int fullRow,
            double[,] band,
            double[] basisBuffer)
        {
            int interval = FindInterval(knots, degree, position, degree);
            ComputeBasis(knots, degree, position, interval, derivativeOrder: 2, basisBuffer);
            for (int a = 0; a <= degree; a++)
            {
                int column = interval - degree + a;
                int bandRow = (2 * degree) + fullRow - column;
                band[bandRow, column] = basisBuffer[a];
            }
        }

        private static void SolveGeneralBand(
            double[,] band,
            int lowerBands,
            int upperBands,
            double[] rightHandSide)
        {
            int count = rightHandSide.Length;
            int diagonalRow = upperBands + lowerBands;
            var pivots = new int[count];
            int lastUpdatedColumn = 0;
            for (int column = 0; column < count; column++)
            {
                if (column + diagonalRow < count)
                {
                    for (int row = 0; row < lowerBands; row++)
                    {
                        band[row, column + diagonalRow] = 0.0;
                    }
                }

                int multiplierCount = Math.Min(lowerBands, count - 1 - column);
                int pivotOffset = 0;
                double pivotMagnitude = Math.Abs(band[diagonalRow, column]);
                for (int i = 1; i <= multiplierCount; i++)
                {
                    double magnitude = Math.Abs(band[diagonalRow + i, column]);
                    if (magnitude > pivotMagnitude)
                    {
                        pivotMagnitude = magnitude;
                        pivotOffset = i;
                    }
                }

                pivots[column] = column + pivotOffset;
                if (band[diagonalRow + pivotOffset, column] == 0.0)
                {
                    throw new InvalidOperationException("Spline collocation matrix is singular.");
                }

                lastUpdatedColumn = Math.Max(
                    lastUpdatedColumn,
                    Math.Min(column + upperBands + pivotOffset, count - 1));
                if (pivotOffset != 0)
                {
                    for (int offset = 0; offset <= lastUpdatedColumn - column; offset++)
                    {
                        int targetColumn = column + offset;
                        int firstRow = diagonalRow + pivotOffset - offset;
                        int secondRow = diagonalRow - offset;
                        (band[firstRow, targetColumn], band[secondRow, targetColumn]) =
                            (band[secondRow, targetColumn], band[firstRow, targetColumn]);
                    }
                }

                if (multiplierCount == 0)
                {
                    continue;
                }

                double scale = 1.0 / band[diagonalRow, column];
                for (int row = 1; row <= multiplierCount; row++)
                {
                    band[diagonalRow + row, column] *= scale;
                }

                for (int targetColumn = column + 1; targetColumn <= lastUpdatedColumn; targetColumn++)
                {
                    int columnOffset = targetColumn - column - 1;
                    double multiplier = -band[diagonalRow - 1 - columnOffset, targetColumn];
                    for (int row = 1; row <= multiplierCount; row++)
                    {
                        band[diagonalRow + row - 1 - columnOffset, targetColumn] +=
                            band[diagonalRow + row, column] * multiplier;
                    }
                }
            }

            for (int column = 0; column < count - 1; column++)
            {
                int multiplierCount = Math.Min(lowerBands, count - 1 - column);
                int pivot = pivots[column];
                if (pivot != column)
                {
                    (rightHandSide[pivot], rightHandSide[column]) =
                        (rightHandSide[column], rightHandSide[pivot]);
                }

                double multiplier = -rightHandSide[column];
                for (int row = 1; row <= multiplierCount; row++)
                {
                    rightHandSide[column + row] += band[diagonalRow + row, column] * multiplier;
                }
            }

            int upperTriangleBands = lowerBands + upperBands;
            for (int column = count - 1; column >= 0; column--)
            {
                rightHandSide[column] /= band[diagonalRow, column];
                double multiplier = -rightHandSide[column];
                int firstRow = Math.Max(0, column - upperTriangleBands);
                for (int row = firstRow; row < column; row++)
                {
                    rightHandSide[row] +=
                        band[diagonalRow + row - column, column] * multiplier;
                }
            }
        }

        private static int FindInterval(
            double[] knots,
            int degree,
            double position,
            int previousInterval)
        {
            int coefficientCount = knots.Length - degree - 1;
            int interval = degree < previousInterval && previousInterval < coefficientCount
                ? previousInterval
                : degree;
            while (position < knots[interval] && interval != degree)
            {
                interval--;
            }

            interval++;
            while (position >= knots[interval] && interval != coefficientCount)
            {
                interval++;
            }

            return interval - 1;
        }

        private static void ComputeBasis(
            double[] knots,
            int degree,
            double position,
            int interval,
            int derivativeOrder,
            Span<double> work)
        {
            Span<double> values = work[..(degree + 1)];
            Span<double> previous = work[(degree + 1)..];
            values[0] = 1.0;
            for (int j = 1; j <= degree - derivativeOrder; j++)
            {
                values[..j].CopyTo(previous);
                values[0] = 0.0;
                for (int n = 1; n <= j; n++)
                {
                    int index = interval + n;
                    double upperKnot = knots[index];
                    double lowerKnot = knots[index - j];
                    if (upperKnot == lowerKnot)
                    {
                        values[n] = 0.0;
                        continue;
                    }

                    double weight = previous[n - 1] / (upperKnot - lowerKnot);
                    values[n - 1] += weight * (upperKnot - position);
                    values[n] = weight * (position - lowerKnot);
                }
            }

            for (int j = degree - derivativeOrder + 1; j <= degree; j++)
            {
                values[..j].CopyTo(previous);
                values[0] = 0.0;
                for (int n = 1; n <= j; n++)
                {
                    int index = interval + n;
                    double upperKnot = knots[index];
                    double lowerKnot = knots[index - j];
                    if (upperKnot == lowerKnot)
                    {
                        values[n] = 0.0;
                        continue;
                    }

                    double weight = j * previous[n - 1] / (upperKnot - lowerKnot);
                    values[n - 1] -= weight;
                    values[n] = weight;
                }
            }
        }
    }
}
