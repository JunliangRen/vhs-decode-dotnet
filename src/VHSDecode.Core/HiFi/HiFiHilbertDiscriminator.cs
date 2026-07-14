using System.Numerics;
using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

public sealed class HiFiHilbertDiscriminator
{
    public HiFiHilbertDiscriminator(
        int sampleRateHz,
        double carrierCenterHz,
        double deviationHz)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(carrierCenterHz)
            || carrierCenterHz <= 0.0
            || carrierCenterHz > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(carrierCenterHz));
        }

        if (!double.IsFinite(deviationHz)
            || deviationHz <= 0.0
            || deviationHz > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(deviationHz));
        }

        SampleRateHz = sampleRateHz;
        CarrierHz = checked((int)Math.Round(carrierCenterHz, MidpointRounding.ToEven));
        DeviationHz = checked((int)Math.Truncate(deviationHz));
    }

    public int SampleRateHz { get; }
    public int CarrierHz { get; }
    public int DeviationHz { get; }

    public void Demodulate(Span<float> input, Span<float> output)
    {
        ComputeAnalyticPhase(input);
        DemodulatePhase(input, output, SampleRateHz, CarrierHz, DeviationHz);
    }

    public static void ComputeAnalyticPhase(Span<float> input)
    {
        if (input.Length < 2)
        {
            throw new ArgumentException("HiFi Hilbert input must contain at least two samples.", nameof(input));
        }

        var realInput = new double[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            realInput[i] = input[i];
        }

        bool powerOfTwo = (input.Length & (input.Length - 1)) == 0;
        Complex[] spectrum = powerOfTwo
            ? PocketFftComplex.ForwardReal(realInput)
            : FastFourierTransform.ForwardAnyLength(realInput);
        int midpoint = input.Length / 2;
        for (int i = 0; i < spectrum.Length; i++)
        {
            double multiplier = i switch
            {
                0 => 1.0,
                _ when i < midpoint => 2.0,
                _ when i == midpoint => 1.0,
                _ => 0.0
            };
            spectrum[i] *= multiplier;
        }

        Complex[] analytic = powerOfTwo
            ? PocketFftComplex.Inverse(spectrum)
            : InverseAnyLength(spectrum);
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)Math.Atan2(analytic[i].Imaginary, analytic[i].Real);
        }
    }

    public static void DemodulatePhase(
        ReadOnlySpan<float> phase,
        Span<float> output,
        int sampleRateHz,
        int carrierHz,
        int deviationHz)
    {
        if (phase.IsEmpty)
        {
            throw new ArgumentException("HiFi phase input must not be empty.", nameof(phase));
        }

        if (output.Length < phase.Length)
        {
            throw new ArgumentException("HiFi demodulation output is shorter than the input.", nameof(output));
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (carrierHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(carrierHz));
        }

        if (deviationHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviationHz));
        }

        double previousPhase = 0.0;
        double previousCorrection = 0.0;
        double carrierScale = (double)carrierHz / deviationHz;
        float sampleRate = sampleRateHz;
        int outputIndex = 0;
        foreach (float currentPhaseValue in phase)
        {
            if (outputIndex >= output.Length - 1)
            {
                break;
            }

            double currentPhase = currentPhaseValue;
            double difference = currentPhase - previousPhase;
            previousPhase = currentPhase;
            double wrappedDifference = PositiveModulo(difference + Math.PI, Math.Tau) - Math.PI;
            if (wrappedDifference == -Math.PI && difference > 0.0)
            {
                wrappedDifference = Math.PI;
            }

            double correction = wrappedDifference - difference;
            if (Math.Abs(difference) < Math.PI)
            {
                correction = 0.0;
            }

            correction = previousCorrection + correction;
            double value = (correction - previousCorrection)
                / Math.Tau
                * sampleRate
                / deviationHz;
            output[outputIndex] = (float)Math.Min(
                Math.Max(value - carrierScale, float.MinValue),
                float.MaxValue);
            previousCorrection = correction;
            outputIndex++;
        }
    }

    private static Complex[] InverseAnyLength(ReadOnlySpan<Complex> input)
    {
        var conjugated = new Complex[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            conjugated[i] = Complex.Conjugate(input[i]);
        }

        Complex[] transformed = FastFourierTransform.ForwardAnyLength(conjugated);
        double scale = 1.0 / input.Length;
        for (int i = 0; i < transformed.Length; i++)
        {
            transformed[i] = Complex.Conjugate(transformed[i]) * scale;
        }

        return transformed;
    }

    private static double PositiveModulo(double value, double divisor)
    {
        double result = value % divisor;
        return result < 0.0 ? result + divisor : result;
    }
}
