using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
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
    private const int SincTapCount = 16;
    private const int SincPhaseCount = 65536;
    private const double KaiserBeta = 5.0;
    private const string SincLookupResourceName = "VHSDecode.Core.Tbc.Resources.sinc_lut.npz";
    private static readonly Lazy<float[]> SincLookup = new(LoadSincLookup);
    private readonly double? _nominalInputLineLength;
    private readonly int _workerThreads;

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
        if (lineCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineCount));
        }

        var output = new double[checked(OutputLineLength * lineCount)];
        ILineLocationInterpolator interpolator = BuildInterpolator(source, lineLocations);
        if (firstLine < 0 || firstLine + lineCount >= interpolator.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLine));
        }

        ResampleSamples(source, interpolator, firstLine, output);

        return output;
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

        if (lineLocations.Count < 2)
        {
            throw new ArgumentException("At least two line locations are required.", nameof(lineLocations));
        }

        for (int i = 0; i < lineLocations.Count; i++)
        {
            double location = lineLocations[i];
            if (!double.IsFinite(location) || (i > 0 && location <= lineLocations[i - 1]))
            {
                throw new ArgumentException("Line locations must be finite and strictly increasing.", nameof(lineLocations));
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

    private unsafe void ResampleSamples(
        ReadOnlySpan<double> source,
        ILineLocationInterpolator interpolator,
        int firstLine,
        Span<double> destination)
    {
        (double[] sourcePositions, double[] levelAdjusts, int prefixSamples) =
            PrepareResampling(interpolator, firstLine, destination.Length);
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

    private unsafe void ResampleSamples(
        ReadOnlySpan<double> source,
        ILineLocationInterpolator interpolator,
        int firstLine,
        double[] destination)
    {
        (double[] sourcePositions, double[] levelAdjusts, int prefixSamples) =
            PrepareResampling(interpolator, firstLine, destination.Length);
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

    private (double[] SourcePositions, double[] LevelAdjusts, int PrefixSamples) PrepareResampling(
        ILineLocationInterpolator interpolator,
        int firstLine,
        int destinationLength)
    {
        int prefixSamples = checked(firstLine * OutputLineLength);
        int scaledSampleCount = checked(prefixSamples + destinationLength);
        var sourcePositions = new double[destinationLength];
        double[] levelAdjusts;
        if (interpolator is LinearLineLocationInterpolator linear
            && scaledSampleCount % OutputLineLength == 0)
        {
            void BuildSourcePositions()
            {
                for (int i = 0; i < destinationLength; i++)
                {
                    sourcePositions[i] = interpolator.EvaluateOutputPosition(prefixSamples + i, OutputLineLength);
                }
            }

            if (_workerThreads > 1 && destinationLength >= ParallelSampleThreshold)
            {
                double[]? parallelLevelAdjusts = null;
                Parallel.Invoke(
                    new ParallelOptions { MaxDegreeOfParallelism = 2 },
                    BuildSourcePositions,
                    () => parallelLevelAdjusts = BuildLinearLevelAdjusts(linear, scaledSampleCount));
                levelAdjusts = parallelLevelAdjusts!;
            }
            else
            {
                BuildSourcePositions();
                levelAdjusts = BuildLinearLevelAdjusts(linear, scaledSampleCount);
            }
        }
        else
        {
            var wowFactors = new double[scaledSampleCount];
            for (int i = 0; i < scaledSampleCount; i++)
            {
                double factor = interpolator.EvaluateOutputDerivative(i, OutputLineLength);
                wowFactors[i] = factor;
                if (i >= prefixSamples)
                {
                    sourcePositions[i - prefixSamples] = interpolator.EvaluateOutputPosition(i, OutputLineLength);
                }
            }

            levelAdjusts = BuildLevelAdjusts(wowFactors);
        }

        return (sourcePositions, levelAdjusts, prefixSamples);
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

    private double[] BuildLinearLevelAdjusts(
        ILineLocationInterpolator interpolator,
        int sampleCount)
    {
        int lineCount = sampleCount / OutputLineLength;
        var lineFactors = new double[lineCount];
        for (int line = 0; line < lineCount; line++)
        {
            lineFactors[line] = interpolator.EvaluateOutputDerivative(
                line * OutputLineLength,
                OutputLineLength);
        }

        double[] adjustedLineFactors = ReplaceWowFactorOutliers(lineFactors);
        var levelAdjusts = new double[sampleCount];
        for (int line = 0; line < lineCount; line++)
        {
            Array.Fill(
                levelAdjusts,
                adjustedLineFactors[line],
                line * OutputLineLength,
                OutputLineLength);
        }

        SmoothLevelAdjusts(levelAdjusts);
        return levelAdjusts;
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

    private void SmoothLevelAdjusts(double[] levelAdjusts)
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
        if (fraction < 0.0f)
        {
            coordInt--;
            fraction += 1.0f;
        }

        float phasePosition = fraction * SincPhaseCount;
        int phaseStart = Math.Clamp((int)phasePosition, 0, SincPhaseCount - 1);
        int phaseEnd = phaseStart + 1;
        float alpha = phasePosition - phaseStart;
        int weightStart = phaseStart * SincTapCount;
        int weightEnd = phaseEnd * SincTapCount;
        int sampleStart = coordInt - ((SincTapCount / 2) - 1);
        double result = 0.0;
        for (int tap = 0; tap < SincTapCount; tap++)
        {
            float startWeight = weights[weightStart + tap];
            float weight = MathF.FusedMultiplyAdd(
                alpha,
                weights[weightEnd + tap] - startWeight,
                startWeight);
            int sampleIndex = Math.Clamp(sampleStart + tap, 0, sourceLength - 1);
            result += (float)source[sampleIndex] * weight;
        }

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
