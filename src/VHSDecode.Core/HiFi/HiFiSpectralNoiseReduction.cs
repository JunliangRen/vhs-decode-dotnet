using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

internal sealed class HiFiSpectralMatrix<T>
{
    internal HiFiSpectralMatrix(int rows, int columns)
        : this(rows, columns, new T[checked(rows * columns)])
    {
    }

    internal HiFiSpectralMatrix(int rows, int columns, T[] values)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != checked(rows * columns))
        {
            throw new ArgumentException(
                "Spectral matrix storage does not match its dimensions.",
                nameof(values));
        }

        Rows = rows;
        Columns = columns;
        Values = values;
    }

    internal int Rows { get; }
    internal int Columns { get; }
    internal T[] Values { get; }

    internal ref T this[int row, int column]
        => ref Values[(row * Columns) + column];
}

internal sealed class HiFiSpectralNoiseReduction
{
    internal const int FftSize = 1024;
    internal const int WindowLength = 1024;
    internal const int HopLength = WindowLength / 4;
    internal const int FrequencyBinCount = (FftSize / 2) + 1;
    private const int ChunkCount = 2;
    private const double FrequencyMaskSmoothHz = 500.0;
    private const double TimeMaskSmoothMilliseconds = 50.0;
    private const double ThresholdMultiplier = 2.0;
    private const double SigmoidSlope = 10.0;
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private readonly Queue<float[]> _history = new(ChunkCount);
    private readonly float[] _window;
    private readonly HiFiSpectralMatrix<double> _smoothingFilter;
    private readonly double _reductionAmount;
    private readonly double _smoothingB;

    internal HiFiSpectralNoiseReduction(int sampleRateHz, double reductionAmount)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!(reductionAmount > 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(reductionAmount));
        }

        SampleRateHz = sampleRateHz;
        ChunkSize = sampleRateHz / HiFiConstants.BlocksPerSecond;
        EndPadding = ChunkSize / 8;
        _reductionAmount = reductionAmount;
        double timeConstantSeconds = ((ChunkSize * ChunkCount) / 2.0) / sampleRateHz;
        double timeFrames = timeConstantSeconds * sampleRateHz / HopLength;
        _smoothingB = 2.0 / (Math.Sqrt(1.0 + (4.0 * timeFrames * timeFrames)) + 1.0);
        _window = CreatePeriodicHannWindow();
        _smoothingFilter = CreateSmoothingFilter(sampleRateHz);
        for (int i = 0; i < ChunkCount; i++)
        {
            _history.Enqueue(new float[ChunkSize]);
        }
    }

    internal int SampleRateHz { get; }
    internal int ChunkSize { get; }
    internal int EndPadding { get; }
    internal double SmoothingB => _smoothingB;
    internal ReadOnlySpan<float> Window => _window;
    internal HiFiSpectralMatrix<double> SmoothingFilter => _smoothingFilter;

    internal void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != ChunkSize)
        {
            throw new ArgumentException(
                $"HiFi spectral noise-reduction blocks must contain {ChunkSize} samples.",
                nameof(input));
        }

        if (input.Length != output.Length)
        {
            throw new ArgumentException(
                "HiFi spectral noise-reduction input and output lengths must match.");
        }

        float[][] history = _history.ToArray();
        var chunk = new float[checked(
            history[0].Length
            + history[1].Length
            + input.Length
            + EndPadding)];
        int offset = 0;
        history[0].CopyTo(chunk, offset);
        offset += history[0].Length;
        history[1].CopyTo(chunk, offset);
        offset += history[1].Length;
        input.CopyTo(chunk.AsSpan(offset));

        _history.Dequeue();
        _history.Enqueue(input.ToArray());

        float[] denoised = Filter(chunk);
        int sourceOffset = denoised.Length - output.Length - EndPadding;
        denoised.AsSpan(sourceOffset, output.Length).CopyTo(output);
    }

    internal float[] Filter(ReadOnlySpan<float> chunk)
    {
        HiFiSpectralMatrix<Complex32> spectrum = CreateStft(chunk, _window);
        HiFiSpectralMatrix<float> absolute = Absolute(spectrum);
        HiFiSpectralMatrix<double> smooth = SmoothTime(absolute, _smoothingB);
        HiFiSpectralMatrix<double> rawMask = CreateMask(absolute, smooth);
        HiFiSpectralMatrix<double> smoothedMask = ConvolveSame(
            rawMask,
            _smoothingFilter);
        ApplyMask(spectrum, smoothedMask, _reductionAmount);
        return InverseStft(spectrum, _window);
    }

    internal static float[] CreatePeriodicHannWindow()
    {
        var window = new float[WindowLength];
        for (int i = 0; i < window.Length; i++)
        {
            double phase = -Math.PI + ((2.0 * Math.PI * i) / WindowLength);
            window[i] = (float)(0.5 + (0.5 * Math.Cos(phase)));
        }

        return window;
    }

    internal static HiFiSpectralMatrix<Complex32> CreateStft(
        ReadOnlySpan<float> chunk,
        ReadOnlySpan<float> window)
    {
        if (window.Length != WindowLength)
        {
            throw new ArgumentException(
                $"HiFi spectral windows must contain {WindowLength} samples.",
                nameof(window));
        }

        int extendedLength = checked(chunk.Length + WindowLength);
        int frameCount = ((extendedLength - WindowLength) / HopLength) + 1;
        var result = new HiFiSpectralMatrix<Complex32>(
            FrequencyBinCount,
            frameCount);
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float[] frame = CreateWindowedFrame(chunk, window, frameIndex);
            Complex32[] frameSpectrum = PocketFftReal32.Forward(frame);
            for (int frequency = 0; frequency < FrequencyBinCount; frequency++)
            {
                Complex32 value = frameSpectrum[frequency];
                float real = value.Real / 512.0f;
                float imaginary = value.Imaginary / 512.0f;
                result[frequency, frameIndex] = new Complex32(
                    real == 0.0f ? 0.0f : real,
                    imaginary == 0.0f ? 0.0f : imaginary);
            }
        }

        return result;
    }

    internal static float[] CreateWindowedFrame(
        ReadOnlySpan<float> chunk,
        ReadOnlySpan<float> window,
        int frameIndex)
    {
        if (window.Length != WindowLength)
        {
            throw new ArgumentException(
                $"HiFi spectral windows must contain {WindowLength} samples.",
                nameof(window));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        var frame = new float[FftSize];
        int chunkOffset = (frameIndex * HopLength) - (WindowLength / 2);
        for (int i = 0; i < WindowLength; i++)
        {
            int sourceIndex = chunkOffset + i;
            float source = (uint)sourceIndex < (uint)chunk.Length
                ? chunk[sourceIndex]
                : 0.0f;
            frame[i] = source * window[i];
        }

        return frame;
    }

    internal static HiFiSpectralMatrix<float> Absolute(
        HiFiSpectralMatrix<Complex32> spectrum)
    {
        var result = new HiFiSpectralMatrix<float>(
            spectrum.Rows,
            spectrum.Columns);
        for (int i = 0; i < spectrum.Values.Length; i++)
        {
            Complex32 value = spectrum.Values[i];
            result.Values[i] = NumpyHypot(value.Real, value.Imaginary);
        }

        return result;
    }

    internal static HiFiSpectralMatrix<double> SmoothTime(
        HiFiSpectralMatrix<float> absolute,
        double smoothingB)
    {
        var result = new HiFiSpectralMatrix<double>(
            absolute.Rows,
            absolute.Columns);
        double a1 = smoothingB - 1.0;
        double steadyStateGain = smoothingB / (1.0 + a1);
        double initialStateFactor = -(steadyStateGain * a1);
        for (int row = 0; row < absolute.Rows; row++)
        {
            int rowOffset = row * absolute.Columns;
            double state = initialStateFactor * absolute.Values[rowOffset];
            for (int column = 0; column < absolute.Columns; column++)
            {
                int index = rowOffset + column;
                double value = (smoothingB * absolute.Values[index]) + state;
                result.Values[index] = value;
                state = -(a1 * value);
            }

            int lastIndex = rowOffset + absolute.Columns - 1;
            state = initialStateFactor * result.Values[lastIndex];
            for (int column = absolute.Columns - 1; column >= 0; column--)
            {
                int index = rowOffset + column;
                double value = (smoothingB * result.Values[index]) + state;
                result.Values[index] = value;
                state = -(a1 * value);
            }
        }

        return result;
    }

    internal static HiFiSpectralMatrix<double> CreateMask(
        HiFiSpectralMatrix<float> absolute,
        HiFiSpectralMatrix<double> smooth)
    {
        ValidateSameShape(absolute, smooth);
        var result = new HiFiSpectralMatrix<double>(
            absolute.Rows,
            absolute.Columns);
        for (int i = 0; i < result.Values.Length; i++)
        {
            double denominator = Math.Max(smooth.Values[i], MachineEpsilon);
            double exponent = ((ThresholdMultiplier + 1.0)
                - (absolute.Values[i] / denominator)) * SigmoidSlope;
            result.Values[i] = 1.0 / (1.0 + Math.Exp(exponent));
        }

        return result;
    }

    internal static HiFiSpectralMatrix<double> ConvolveSame(
        HiFiSpectralMatrix<double> input,
        HiFiSpectralMatrix<double> filter)
    {
        var result = new HiFiSpectralMatrix<double>(input.Rows, input.Columns);
        int rowCenter = filter.Rows / 2;
        int columnCenter = filter.Columns / 2;
        for (int row = 0; row < input.Rows; row++)
        {
            for (int column = 0; column < input.Columns; column++)
            {
                double sum = 0.0;
                for (int filterRow = 0; filterRow < filter.Rows; filterRow++)
                {
                    int inputRow = row + filterRow - rowCenter;
                    if ((uint)inputRow >= (uint)input.Rows)
                    {
                        continue;
                    }

                    int inputOffset = inputRow * input.Columns;
                    int filterOffset = filterRow * filter.Columns;
                    for (int filterColumn = 0;
                        filterColumn < filter.Columns;
                        filterColumn++)
                    {
                        int inputColumn = column + filterColumn - columnCenter;
                        if ((uint)inputColumn >= (uint)input.Columns)
                        {
                            continue;
                        }

                        sum += input.Values[inputOffset + inputColumn]
                            * filter.Values[filterOffset + filterColumn];
                    }
                }

                result[row, column] = sum;
            }
        }

        return result;
    }

    internal static void ApplyMask(
        HiFiSpectralMatrix<Complex32> spectrum,
        HiFiSpectralMatrix<double> mask,
        double reductionAmount)
    {
        ValidateSameShape(spectrum, mask);
        for (int i = 0; i < spectrum.Values.Length; i++)
        {
            double scale = Math.FusedMultiplyAdd(
                reductionAmount,
                mask.Values[i] - 1.0,
                1.0);
            Complex32 value = spectrum.Values[i];
            spectrum.Values[i] = new Complex32(
                (float)(value.Real * scale),
                (float)(value.Imaginary * scale));
        }
    }

    internal static float[] InverseStft(
        HiFiSpectralMatrix<Complex32> spectrum,
        ReadOnlySpan<float> window)
    {
        if (spectrum.Rows != FrequencyBinCount)
        {
            throw new ArgumentException(
                $"HiFi spectral matrices must contain {FrequencyBinCount} frequency bins.",
                nameof(spectrum));
        }

        if (window.Length != WindowLength)
        {
            throw new ArgumentException(
                $"HiFi spectral windows must contain {WindowLength} samples.",
                nameof(window));
        }

        int outputLength = checked(
            WindowLength + ((spectrum.Columns - 1) * HopLength));
        var output = new float[outputLength];
        var normalization = new float[outputLength];
        var halfSpectrum = new Complex32[FrequencyBinCount];
        for (int frameIndex = 0; frameIndex < spectrum.Columns; frameIndex++)
        {
            for (int frequency = 0; frequency < FrequencyBinCount; frequency++)
            {
                Complex32 value = spectrum[frequency, frameIndex];
                halfSpectrum[frequency] = value;
            }

            float[] inverse = PocketFftReal32.Inverse(
                halfSpectrum,
                FftSize);
            int outputOffset = frameIndex * HopLength;
            for (int i = 0; i < WindowLength; i++)
            {
                float sample = inverse[i] * 512.0f;
                float weighted = sample * window[i];
                output[outputOffset + i] += weighted;
                float squaredWindow = window[i] * window[i];
                normalization[outputOffset + i] += squaredWindow;
            }
        }

        int boundary = WindowLength / 2;
        int trimmedLength = outputLength - WindowLength;
        var trimmed = new float[trimmedLength];
        for (int i = 0; i < trimmed.Length; i++)
        {
            float norm = normalization[boundary + i];
            trimmed[i] = output[boundary + i]
                / (norm > 1e-10f ? norm : 1.0f);
        }

        return trimmed;
    }

    private static HiFiSpectralMatrix<double> CreateSmoothingFilter(
        int sampleRateHz)
    {
        int frequencyGradient = (int)(FrequencyMaskSmoothHz
            / (sampleRateHz / (FftSize / 2.0)));
        int timeGradient = (int)(TimeMaskSmoothMilliseconds
            / ((HopLength / (double)sampleRateHz) * 1000.0));
        double[] frequency = CreateGradient(frequencyGradient);
        double[] time = CreateGradient(timeGradient);
        var filter = new HiFiSpectralMatrix<double>(
            frequency.Length,
            time.Length);
        for (int row = 0; row < filter.Rows; row++)
        {
            for (int column = 0; column < filter.Columns; column++)
            {
                double value = frequency[row] * time[column];
                filter[row, column] = value;
            }
        }

        double sum = NumpyPairwiseSum(filter.Values);
        for (int i = 0; i < filter.Values.Length; i++)
        {
            filter.Values[i] /= sum;
        }

        return filter;
    }

    private static double[] CreateGradient(int gradient)
    {
        if (gradient < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gradient));
        }

        int denominator = gradient + 1;
        var values = new double[(2 * gradient) + 1];
        double ascendingStep = 1.0 / denominator;
        double descendingStep = -1.0 / denominator;
        for (int i = 0; i < gradient; i++)
        {
            values[i] = (i + 1) * ascendingStep;
            values[gradient + 1 + i] = 1.0 + ((i + 1) * descendingStep);
        }

        values[gradient] = 1.0;
        return values;
    }

    private static double NumpyPairwiseSum(ReadOnlySpan<double> values)
    {
        const int blockSize = 128;
        if (values.Length < 8)
        {
            double sum = -0.0;
            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return sum;
        }

        if (values.Length > blockSize)
        {
            int split = values.Length / 2;
            split -= split % 8;
            return NumpyPairwiseSum(values[..split])
                + NumpyPairwiseSum(values[split..]);
        }

        double r0 = values[0];
        double r1 = values[1];
        double r2 = values[2];
        double r3 = values[3];
        double r4 = values[4];
        double r5 = values[5];
        double r6 = values[6];
        double r7 = values[7];
        int blockEnd = values.Length - (values.Length % 8);
        int index = 8;
        for (; index < blockEnd; index += 8)
        {
            r0 += values[index];
            r1 += values[index + 1];
            r2 += values[index + 2];
            r3 += values[index + 3];
            r4 += values[index + 4];
            r5 += values[index + 5];
            r6 += values[index + 6];
            r7 += values[index + 7];
        }

        double result = ((r0 + r1) + (r2 + r3))
            + ((r4 + r5) + (r6 + r7));
        for (; index < values.Length; index++)
        {
            result += values[index];
        }

        return result;
    }

    private static float NumpyHypot(float left, float right)
    {
        float absoluteLeft = MathF.Abs(left);
        float absoluteRight = MathF.Abs(right);
        if (float.IsInfinity(absoluteLeft) || float.IsInfinity(absoluteRight))
        {
            return float.PositiveInfinity;
        }

        if (float.IsNaN(absoluteLeft) || float.IsNaN(absoluteRight))
        {
            return float.NaN;
        }

        float larger = MathF.Max(absoluteLeft, absoluteRight);
        if (larger == 0.0f)
        {
            return 0.0f;
        }

        float smaller = MathF.Min(absoluteLeft, absoluteRight);
        float ratio = smaller / larger;
        float scaled = MathF.Sqrt(MathF.FusedMultiplyAdd(ratio, ratio, 1.0f));
        return scaled * larger;
    }

    private static void ValidateSameShape<TLeft, TRight>(
        HiFiSpectralMatrix<TLeft> left,
        HiFiSpectralMatrix<TRight> right)
    {
        if (left.Rows != right.Rows || left.Columns != right.Columns)
        {
            throw new ArgumentException("HiFi spectral matrix dimensions must match.");
        }
    }
}
