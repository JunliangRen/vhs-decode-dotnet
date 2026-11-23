using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.HiFi;

public sealed class HiFiQuadratureDiscriminator
{
    // Limiting the table keeps custom, long-period oscillators on the existing IPP path.
    private const int MaxLookupOscillatorLength = 4096;

    private readonly double[] _inPhaseOscillator;
    private readonly double[] _quadratureOscillator;
    private float[]? _phaseLookup;

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
        => Demodulate(input, output, useIpp: false);

    internal void Demodulate(ReadOnlySpan<float> input, Span<float> output, bool useIpp)
    {
        if (input.IsEmpty)
        {
            throw new ArgumentException("HiFi RF input must not be empty.", nameof(input));
        }

        if (output.Length < input.Length)
        {
            throw new ArgumentException("HiFi demodulation output is shorter than the input.", nameof(output));
        }

        if (useIpp)
        {
            DemodulateIpp(input, output);
            return;
        }

        // Exact only uses a table when both rounded phases are uniform across the oscillator.
        if (Avx2.IsSupported && !input.Overlaps(output)
            && TryDemodulateLookup(input, output, requireUniform: true))
        {
            return;
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

    private float[] BuildPhaseLookup(double phaseScale)
    {
        int oscillatorLength = _inPhaseOscillator.Length;
        var phases = new float[oscillatorLength * 2];
        // The common RF magnitude cancels in atan2. Reserve a margin around the
        // double-precision roundoff and any discontinuity before narrowing to float.
        const double phaseGuard = 1e-12;
        double scaledGuard = phaseGuard * phaseScale;
        for (int index = 0; index < oscillatorLength; index++)
        {
            int previous = index == 0 ? oscillatorLength - 1 : index - 1;
            for (int opposite = 0; opposite < 2; opposite++)
            {
                // The oscillator alternates polarity at each period boundary.
                int sign = ((index == 0) ^ (opposite != 0)) ? -1 : 1;
                double i = sign * _inPhaseOscillator[index];
                double q = sign * _quadratureOscillator[index];
                double previousI = _inPhaseOscillator[previous];
                double previousQ = _quadratureOscillator[previous];
                double imaginary = Math.FusedMultiplyAdd(q, previousI, -(i * previousQ));
                double real = Math.FusedMultiplyAdd(i, previousI, q * previousQ);
                double nominal = Math.Atan2(imaginary, real) * phaseScale;
                float low = (float)(nominal - scaledGuard);
                float high = (float)(nominal + scaledGuard);
                phases[2 * index + opposite] = Math.Abs(imaginary) <= phaseGuard
                    || BitConverter.SingleToInt32Bits(low) != BitConverter.SingleToInt32Bits(high)
                    ? float.NaN
                    : low;
            }
        }
        return phases;
    }

    private void DemodulateLookup(ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<float> phases, double phaseScale)
    {
        int oscillatorLength = _inPhaseOscillator.Length;
        int index = 0;
        float previousSample = input[0];
        uint previousBits = BitConverter.SingleToUInt32Bits(previousSample);
        bool previousIsSpecial = !IsFiniteNonzero(previousBits);
        for (int n = 1; n < input.Length; n++)
        {
            if (++index == oscillatorLength)
            {
                index = 0;
            }

            float sample = input[n];
            uint bits = BitConverter.SingleToUInt32Bits(sample);
            bool isSpecial = !IsFiniteNonzero(bits);
            int opposite = (int)((bits ^ previousBits) >> 31);
            float phase = phases[2 * index + opposite];
            // RF zeros, non-finite inputs and uncertain table entries use the scalar phase.
            output[n - 1] = isSpecial || previousIsSpecial || float.IsNaN(phase)
                ? DemodulateScalarPair(sample, previousSample, n, oscillatorLength, phaseScale)
                : phase;
            previousSample = sample;
            previousBits = bits;
            previousIsSpecial = isSpecial;
        }
    }

    private static bool IsFiniteNonzero(uint bits)
    {
        uint magnitude = bits & 0x7fff_ffffu;
        return magnitude - 1u < 0x7f7f_ffffu;
    }

    private float DemodulateScalarPair(float current, float previous, int position, int oscillatorLength, double phaseScale)
    {
        int index = position % oscillatorLength;
        int previousIndex = index == 0 ? oscillatorLength - 1 : index - 1;
        int sign = 1 - (2 * ((position / oscillatorLength) & 1));
        double signedRf = current * sign;
        double previousRf = previous * (index == 0 ? -sign : sign);
        double currentI = signedRf * _inPhaseOscillator[index];
        double currentQ = signedRf * _quadratureOscillator[index];
        double previousI = previousRf * _inPhaseOscillator[previousIndex];
        double previousQ = previousRf * _quadratureOscillator[previousIndex];
        double imaginary = Math.FusedMultiplyAdd(currentQ, previousI, -(currentI * previousQ));
        double real = Math.FusedMultiplyAdd(currentI, previousI, currentQ * previousQ);
        double value = Math.Atan2(imaginary, real) * phaseScale;
        return (float)Math.Min(Math.Max(value, float.MinValue), float.MaxValue);
    }


    private static bool HasUniformPhases(ReadOnlySpan<float> phases)
    {
        if (float.IsNaN(phases[0]) || float.IsNaN(phases[1]))
        {
            return false;
        }
        for (int index = 2; index < phases.Length; index++)
        {
            if (BitConverter.SingleToInt32Bits(phases[index]) != BitConverter.SingleToInt32Bits(phases[index & 1]))
            {
                return false;
            }
        }
        return true;
    }

    private void DemodulateUniformLookup(
        ReadOnlySpan<float> input, Span<float> output, float sameSign, float oppositeSign, double phaseScale)
    {
        const int lanes = 8;
        var magnitudeMask = Vector256.Create(0x7fff_ffff);
        var infinity = Vector256.Create(0x7f80_0000);
        var same = Vector256.Create(sameSign);
        var opposite = Vector256.Create(oppositeSign);
        ref int source = ref Unsafe.As<float, int>(ref MemoryMarshal.GetReference(input));
        ref float destination = ref MemoryMarshal.GetReference(output);
        int end = input.Length - 1;
        int n = 0;
        for (; n <= end - lanes; n += lanes)
        {
            var previous = Vector256.LoadUnsafe(ref source, (nuint)n);
            var current = Vector256.LoadUnsafe(ref source, (nuint)(n + 1));
            var previousMagnitude = previous & magnitudeMask;
            var currentMagnitude = current & magnitudeMask;
            var invalid = Vector256.Equals(Vector256.Min(previousMagnitude, currentMagnitude), Vector256<int>.Zero)
                | Vector256.GreaterThanOrEqual(Vector256.Max(previousMagnitude, currentMagnitude), infinity);

            if (Vector256.EqualsAll(invalid, Vector256<int>.Zero))
            {
                var changedSign = Vector256.LessThan(previous ^ current, Vector256<int>.Zero);
                Vector256.ConditionalSelect(changedSign.AsSingle(), opposite, same)
                    .StoreUnsafe(ref destination, (nuint)n);
            }
            else
            {
                for (int j = n; j < n + lanes; j++)
                {
                    output[j] = DemodulateScalarPair(input[j + 1], input[j], j + 1, _inPhaseOscillator.Length, phaseScale);
                }
            }
        }

        for (; n < end; n++)
        {
            output[n] = DemodulateScalarPair(input[n + 1], input[n], n + 1, _inPhaseOscillator.Length, phaseScale);
        }
    }


    private bool TryDemodulateLookup(ReadOnlySpan<float> input, Span<float> output, bool requireUniform)
    {
        if (DeviationHz <= 0 || _inPhaseOscillator.Length is 0 or > MaxLookupOscillatorLength)
        {
            return false;
        }

        double scale = SampleRateHz / (Math.Tau * DeviationHz);
        float[] phases = _phaseLookup ??= BuildPhaseLookup(scale);
        if (Avx2.IsSupported && HasUniformPhases(phases))
        {
            DemodulateUniformLookup(input, output, phases[0], phases[1], scale);
            return true;
        }

        if (requireUniform)
        {
            return false;
        }

        DemodulateLookup(input, output, phases, scale);
        return true;
    }

    private unsafe void DemodulateIpp(ReadOnlySpan<float> input, Span<float> output)
    {
        if (TryDemodulateLookup(input, output, requireUniform: false))
        {
            return;
        }

        const int batchSize = 512;
        double* imaginary = stackalloc double[batchSize];
        double* real = stackalloc double[batchSize];
        double* phase = stackalloc double[batchSize];
        double phaseScale = SampleRateHz / (Math.Tau * DeviationHz);
        int oscillatorLength = _inPhaseOscillator.Length;
        double previousI = input[0] * _inPhaseOscillator[0];
        double previousQ = input[0] * _quadratureOscillator[0];

        int oscillatorIndex = 0;
        int sign = 1;
        for (int start = 1; start < input.Length;)
        {
            int length = Math.Min(batchSize, input.Length - start);
            for (int j = 0; j < length; j++)
            {
                if (++oscillatorIndex == oscillatorLength)
                {
                    oscillatorIndex = 0;
                    sign = -sign;
                }

                double signedRf = input[start + j] * sign;
                double currentI = signedRf * _inPhaseOscillator[oscillatorIndex];
                double currentQ = signedRf * _quadratureOscillator[oscillatorIndex];
                imaginary[j] = Math.FusedMultiplyAdd(currentQ, previousI, -(currentI * previousQ));
                real[j] = Math.FusedMultiplyAdd(currentI, previousI, currentQ * previousQ);
                previousI = currentI;
                previousQ = currentQ;
            }

            IppStatus.ThrowIfFailed(IppNativeMethods.Atan2(imaginary, real, phase, length), "atan2_64f");
            for (int j = 0; j < length; j++)
            {
                // Keep the scalar behavior at signed zeros and non-finite phases.
                double angle = imaginary[j] == 0 || real[j] == 0 || !double.IsFinite(phase[j])
                    ? Math.Atan2(imaginary[j], real[j])
                    : phase[j];
                double value = angle * phaseScale;
                output[start + j - 1] = (float)Math.Min(Math.Max(value, float.MinValue), float.MaxValue);
            }

            start += length;
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
