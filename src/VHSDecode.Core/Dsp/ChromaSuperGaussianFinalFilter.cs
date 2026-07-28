namespace VHSDecode.Core.Dsp;

internal sealed class ChromaSuperGaussianFinalFilter
{
    private const int MaximumParallelWorkers = 4;
    private const double AttenuationDb = 80.0;
    private const double LowerBandwidthHz = 1_300_000.0;
    private const int Order = 2;
    private const int PadSamples = 256;

    private readonly int _inputLength;
    private readonly double[] _mask;
    private readonly int _padLeft;
    private readonly int _paddedLength;

    internal ChromaSuperGaussianFinalFilter(
        int inputLength,
        double fscHz,
        double colorUnderCarrierHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fscHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            colorUnderCarrierHz);

        _inputLength = inputLength;
        _paddedLength = NextFastLength(
            checked(inputLength + (2 * PadSamples)));
        _padLeft = (_paddedLength - inputLength) / 2;
        _mask = BuildMask(
            _paddedLength,
            fscHz,
            colorUnderCarrierHz);
    }

    internal int PaddedLength => _paddedLength;

    internal double[] Apply(ReadOnlySpan<double> input)
    {
        var output = new double[_inputLength];
        ApplyCore(input, output, workerThreads: 1);
        return output;
    }

    internal double[] ApplyInPlace(
        double[] input,
        int workerThreads = 1)
    {
        ArgumentNullException.ThrowIfNull(input);
        ApplyCore(
            input,
            input,
            Math.Clamp(workerThreads, 1, MaximumParallelWorkers));
        return input;
    }

    private void ApplyCore(
        ReadOnlySpan<double> input,
        Span<double> output,
        int workerThreads)
    {
        if (input.Length != _inputLength)
        {
            throw new ArgumentException(
                "Chroma field length does not match the configured filter.",
                nameof(input));
        }

        if (output.Length != _inputLength)
        {
            throw new ArgumentException(
                "Chroma output length does not match the configured filter.",
                nameof(output));
        }

        float[] padded = ReflectPad(input, _paddedLength, _padLeft);
        Complex32[] spectrum = PocketFftReal32.ForwardAnyLength(
            padded,
            workerThreads);
        for (int i = 0; i < spectrum.Length; i++)
        {
            double real = spectrum[i].Real;
            double imaginary = spectrum[i].Imaginary;
            spectrum[i] = new Complex32(
                (float)((real * _mask[i]) - (imaginary * 0.0)),
                (float)((real * 0.0) + (imaginary * _mask[i])));
        }

        float[] filtered = PocketFftReal32.InverseAnyLength(
            spectrum,
            _paddedLength,
            workerThreads);
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = filtered[_padLeft + i];
        }
    }

    internal static int NextFastLength(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumLength);
        if (minimumLength <= 12)
        {
            return minimumLength;
        }

        long best = 2L * minimumLength;
        for (long factor11 = 1; factor11 < best; factor11 *= 11)
        {
            for (long factor7 = factor11;
                factor7 < best;
                factor7 *= 7)
            {
                for (long factor5 = factor7;
                    factor5 < best;
                    factor5 *= 5)
                {
                    long candidate = factor5;
                    while (candidate < minimumLength)
                    {
                        candidate *= 2;
                    }

                    while (true)
                    {
                        if (candidate < minimumLength)
                        {
                            candidate *= 3;
                        }
                        else if (candidate > minimumLength)
                        {
                            best = Math.Min(best, candidate);
                            if ((candidate & 1) != 0)
                            {
                                break;
                            }

                            candidate >>= 1;
                        }
                        else
                        {
                            return minimumLength;
                        }
                    }
                }
            }
        }

        return checked((int)best);
    }

    private static double[] BuildMask(
        int length,
        double fscHz,
        double colorUnderCarrierHz)
    {
        double attenuation = Math.Pow(
            10.0,
            -Math.Abs(AttenuationDb) / 20.0);
        double exponent = 1.0 / (2.0 * Order);
        double upperBandwidthHz = (2.0 * colorUnderCarrierHz)
            / Math.Pow(-Math.Log(attenuation), exponent);
        double frequencyScale = length * (1.0 / (fscHz * 4.0));
        var mask = new double[(length / 2) + 1];
        for (int i = 0; i < mask.Length; i++)
        {
            double frequency = i / frequencyScale;
            double bandwidth = frequency <= fscHz
                ? LowerBandwidthHz
                : upperBandwidthHz;
            double normalized = (frequency - fscHz) / bandwidth;
            double squared = normalized * normalized;
            mask[i] = Math.Exp(-(squared * squared));
        }

        return mask;
    }

    private static float[] ReflectPad(
        ReadOnlySpan<double> input,
        int paddedLength,
        int padLeft)
    {
        int padRight = paddedLength - input.Length - padLeft;
        if (padLeft >= input.Length || padRight >= input.Length)
        {
            throw new ArgumentException(
                "Chroma field is too short for reflected FFT padding.",
                nameof(input));
        }

        var output = new float[paddedLength];
        for (int i = 0; i < padLeft; i++)
        {
            output[i] = (float)input[padLeft - i];
        }

        for (int i = 0; i < input.Length; i++)
        {
            output[padLeft + i] = (float)input[i];
        }

        for (int i = 0; i < padRight; i++)
        {
            output[padLeft + input.Length + i] =
                (float)input[input.Length - i - 2];
        }

        return output;
    }
}
