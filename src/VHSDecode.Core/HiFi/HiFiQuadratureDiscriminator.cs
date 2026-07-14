namespace VHSDecode.Core.HiFi;

public sealed class HiFiQuadratureDiscriminator
{
    private readonly double[] _inPhaseOscillator;
    private readonly double[] _quadratureOscillator;

    public HiFiQuadratureDiscriminator(
        int sampleRateHz,
        double carrierCenterHz,
        double deviationHz,
        int maximumOscillatorLength)
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

        if (maximumOscillatorLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOscillatorLength));
        }

        SampleRateHz = sampleRateHz;
        CarrierHz = checked((int)Math.Round(carrierCenterHz, MidpointRounding.ToEven));
        DeviationHz = checked((int)Math.Truncate(deviationHz));
        int oscillatorLength = MinimumOscillatorLength(
            SampleRateHz,
            CarrierHz,
            maximumOscillatorLength);
        _inPhaseOscillator = new double[oscillatorLength];
        _quadratureOscillator = new double[oscillatorLength];
        GenerateOscillators(
            _inPhaseOscillator,
            _quadratureOscillator,
            CarrierHz,
            SampleRateHz);
    }

    public int SampleRateHz { get; }
    public int CarrierHz { get; }
    public int DeviationHz { get; }
    public ReadOnlyMemory<double> InPhaseOscillator => _inPhaseOscillator;
    public ReadOnlyMemory<double> QuadratureOscillator => _quadratureOscillator;

    public void Demodulate(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.IsEmpty)
        {
            throw new ArgumentException("HiFi RF input must not be empty.", nameof(input));
        }

        if (output.Length < input.Length)
        {
            throw new ArgumentException("HiFi demodulation output is shorter than the input.", nameof(output));
        }

        double phaseScale = SampleRateHz / (Math.Tau * DeviationHz);
        int oscillatorLength = _inPhaseOscillator.Length;
        double previousI = input[0] * _inPhaseOscillator[0];
        double previousQ = input[0] * _quadratureOscillator[0];

        for (int i = 1; i < input.Length; i++)
        {
            int oscillatorIndex = i % oscillatorLength;
            int sign = 1 - (2 * ((i / oscillatorLength) & 1));
            double signedRf = input[i] * sign;
            double currentI = signedRf * _inPhaseOscillator[oscillatorIndex];
            double currentQ = signedRf * _quadratureOscillator[oscillatorIndex];
            double imaginary = Math.FusedMultiplyAdd(
                currentQ,
                previousI,
                -(currentI * previousQ));
            double real = Math.FusedMultiplyAdd(
                currentI,
                previousI,
                currentQ * previousQ);
            double value = Math.Atan2(imaginary, real) * phaseScale;
            output[i - 1] = (float)Math.Min(
                Math.Max(value, float.MinValue),
                float.MaxValue);
            previousI = currentI;
            previousQ = currentQ;
        }
    }

    public static int MinimumOscillatorLength(
        int sampleRateHz,
        int carrierHz,
        int maximumOscillatorLength)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (carrierHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(carrierHz));
        }

        if (maximumOscillatorLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOscillatorLength));
        }

        int divisor = GreatestCommonDivisor(sampleRateHz, carrierHz);
        long leastCommonMultiple = checked((long)(sampleRateHz / divisor) * carrierHz);
        double samplesPerPeriod = (double)sampleRateHz / carrierHz;
        double minimumPeriods = (double)leastCommonMultiple / sampleRateHz;
        int minimumSamples = checked((int)(samplesPerPeriod * minimumPeriods / 2.0));
        return Math.Min(minimumSamples, maximumOscillatorLength);
    }

    private static void GenerateOscillators(
        Span<double> inPhase,
        Span<double> quadrature,
        int carrierHz,
        int sampleRateHz)
    {
        double twoPiCarrier = Math.Tau * carrierHz;
        for (int i = 0; i < inPhase.Length; i++)
        {
            double time = (double)i / sampleRateHz;
            inPhase[i] = Math.Cos(twoPiCarrier * time);
            quadrature[i] = -Math.Sin(twoPiCarrier * time);
        }
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }
}
