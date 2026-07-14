// Float32 radix-4 real FFT adapted from pocketfft's BSD-3-Clause implementation.
namespace VHSDecode.Core.Dsp;

internal static class PocketFftReal32
{
    private const int SupportedLength = 1024;
    private static readonly Plan SharedPlan = new(SupportedLength);

    internal static Complex32[] Forward(ReadOnlySpan<float> input)
    {
        if (input.Length != SupportedLength)
        {
            throw new ArgumentException(
                $"This float32 real FFT requires exactly {SupportedLength} samples.",
                nameof(input));
        }

        return SharedPlan.Forward(input);
    }

    internal static float[] Inverse(
        ReadOnlySpan<Complex32> input,
        int outputLength)
    {
        if (outputLength != SupportedLength)
        {
            throw new ArgumentException(
                $"This float32 real FFT requires exactly {SupportedLength} output samples.",
                nameof(outputLength));
        }

        if (input.Length != (outputLength / 2) + 1)
        {
            throw new ArgumentException(
                "Half-spectrum length does not match the requested real output length.",
                nameof(input));
        }

        return SharedPlan.Inverse(input);
    }

    private sealed class Plan
    {
        private readonly Factor[] _factors;
        private readonly int _length;

        internal Plan(int length)
        {
            _length = length;
            _factors = BuildFactors(length);
        }

        internal Complex32[] Forward(ReadOnlySpan<float> input)
        {
            float[] packed = input.ToArray();
            ExecuteForward(packed);
            var output = new Complex32[(_length / 2) + 1];
            output[0] = new Complex32(packed[0], 0.0f);
            for (int i = 1; i < output.Length - 1; i++)
            {
                output[i] = new Complex32(
                    packed[(2 * i) - 1],
                    packed[2 * i]);
            }

            output[^1] = new Complex32(packed[^1], 0.0f);
            return output;
        }

        internal float[] Inverse(ReadOnlySpan<Complex32> input)
        {
            var packed = new float[_length];
            packed[0] = input[0].Real;
            for (int i = 1; i < input.Length - 1; i++)
            {
                packed[(2 * i) - 1] = input[i].Real;
                packed[2 * i] = input[i].Imaginary;
            }

            packed[^1] = input[^1].Real;
            ExecuteBackward(packed, 1.0f / _length);
            return packed;
        }

        private void ExecuteForward(float[] data)
        {
            var scratch = new float[_length];
            float[] source = data;
            float[] destination = scratch;
            int l1 = _length;
            for (int pass = 0; pass < _factors.Length; pass++)
            {
                Factor factor = _factors[_factors.Length - pass - 1];
                int ido = _length / l1;
                l1 /= 4;
                Radix4Forward(
                    ido,
                    l1,
                    source,
                    destination,
                    factor.Twiddles);
                (source, destination) = (destination, source);
            }

            if (!ReferenceEquals(source, data))
            {
                source.CopyTo(data, 0);
            }
        }

        private void ExecuteBackward(float[] data, float normalization)
        {
            var scratch = new float[_length];
            float[] source = data;
            float[] destination = scratch;
            int l1 = 1;
            foreach (Factor factor in _factors)
            {
                int ido = _length / (4 * l1);
                Radix4Backward(
                    ido,
                    l1,
                    source,
                    destination,
                    factor.Twiddles);
                (source, destination) = (destination, source);
                l1 *= 4;
            }

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = normalization * source[i];
            }
        }

        private static Factor[] BuildFactors(int length)
        {
            int remaining = length;
            int factorCount = 0;
            while (remaining % 4 == 0)
            {
                factorCount++;
                remaining /= 4;
            }

            if (remaining != 1)
            {
                throw new ArgumentException(
                    "Only power-of-four float32 real FFT lengths are supported.",
                    nameof(length));
            }

            var factors = new Factor[factorCount];
            var roots = new UnityRoots(length);
            int l1 = 1;
            for (int factorIndex = 0;
                factorIndex < factors.Length;
                factorIndex++)
            {
                int ido = length / (4 * l1);
                var twiddles = new float[3 * (ido - 1)];
                for (int j = 1; j < 4; j++)
                {
                    for (int i = 1; i <= (ido - 1) / 2; i++)
                    {
                        FloatTwiddle value = roots.Get(j * l1 * i);
                        int offset = ((j - 1) * (ido - 1))
                            + (2 * i)
                            - 2;
                        twiddles[offset] = value.Real;
                        twiddles[offset + 1] = value.Imaginary;
                    }
                }

                factors[factorIndex] = new Factor(twiddles);
                l1 *= 4;
            }

            return factors;
        }

        private static void Radix4Forward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            const float HalfSqrt2 = 0.707106781186547524400844362104849f;
            for (int k = 0; k < l1; k++)
            {
                float c3 = input[ForwardInput(0, k, 3, ido, l1)];
                float c1 = input[ForwardInput(0, k, 1, ido, l1)];
                float tr1 = c3 + c1;
                output[ForwardOutput(0, 2, k, ido)] = c3 - c1;
                float c0 = input[ForwardInput(0, k, 0, ido, l1)];
                float c2 = input[ForwardInput(0, k, 2, ido, l1)];
                float tr2 = c0 + c2;
                output[ForwardOutput(ido - 1, 1, k, ido)] = c0 - c2;
                output[ForwardOutput(0, 0, k, ido)] = tr2 + tr1;
                output[ForwardOutput(ido - 1, 3, k, ido)] = tr2 - tr1;
            }

            if ((ido & 1) == 0)
            {
                for (int k = 0; k < l1; k++)
                {
                    float c1 = input[ForwardInput(ido - 1, k, 1, ido, l1)];
                    float c3 = input[ForwardInput(ido - 1, k, 3, ido, l1)];
                    float ti1 = -HalfSqrt2 * (c1 + c3);
                    float tr1 = HalfSqrt2 * (c1 - c3);
                    float c0 = input[ForwardInput(ido - 1, k, 0, ido, l1)];
                    output[ForwardOutput(ido - 1, 0, k, ido)] = c0 + tr1;
                    output[ForwardOutput(ido - 1, 2, k, ido)] = c0 - tr1;
                    float c2 = input[ForwardInput(ido - 1, k, 2, ido, l1)];
                    output[ForwardOutput(0, 3, k, ido)] = ti1 + c2;
                    output[ForwardOutput(0, 1, k, ido)] = ti1 - c2;
                }
            }

            if (ido <= 2)
            {
                return;
            }

            int stride = ido - 1;
            for (int k = 0; k < l1; k++)
            {
                for (int i = 2; i < ido; i += 2)
                {
                    int ic = ido - i;
                    MultiplyConjugate(
                        twiddles[i - 2],
                        twiddles[i - 1],
                        input[ForwardInput(i - 1, k, 1, ido, l1)],
                        input[ForwardInput(i, k, 1, ido, l1)],
                        out float cr2,
                        out float ci2);
                    MultiplyConjugate(
                        twiddles[stride + i - 2],
                        twiddles[stride + i - 1],
                        input[ForwardInput(i - 1, k, 2, ido, l1)],
                        input[ForwardInput(i, k, 2, ido, l1)],
                        out float cr3,
                        out float ci3);
                    MultiplyConjugate(
                        twiddles[(2 * stride) + i - 2],
                        twiddles[(2 * stride) + i - 1],
                        input[ForwardInput(i - 1, k, 3, ido, l1)],
                        input[ForwardInput(i, k, 3, ido, l1)],
                        out float cr4,
                        out float ci4);

                    float tr1 = cr4 + cr2;
                    float tr4 = cr4 - cr2;
                    float ti1 = ci2 + ci4;
                    float ti4 = ci2 - ci4;
                    float c0r = input[ForwardInput(i - 1, k, 0, ido, l1)];
                    float tr2 = c0r + cr3;
                    float tr3 = c0r - cr3;
                    float c0i = input[ForwardInput(i, k, 0, ido, l1)];
                    float ti2 = c0i + ci3;
                    float ti3 = c0i - ci3;

                    output[ForwardOutput(i - 1, 0, k, ido)] = tr2 + tr1;
                    output[ForwardOutput(ic - 1, 3, k, ido)] = tr2 - tr1;
                    output[ForwardOutput(i, 0, k, ido)] = ti1 + ti2;
                    output[ForwardOutput(ic, 3, k, ido)] = ti1 - ti2;
                    output[ForwardOutput(i - 1, 2, k, ido)] = tr3 + ti4;
                    output[ForwardOutput(ic - 1, 1, k, ido)] = tr3 - ti4;
                    output[ForwardOutput(i, 2, k, ido)] = tr4 + ti3;
                    output[ForwardOutput(ic, 1, k, ido)] = tr4 - ti3;
                }
            }
        }

        private static void Radix4Backward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            const float Sqrt2 = 1.414213562373095048801688724209698f;
            for (int k = 0; k < l1; k++)
            {
                float c0 = input[BackwardInput(0, 0, k, ido)];
                float c3 = input[BackwardInput(ido - 1, 3, k, ido)];
                float tr2 = c0 + c3;
                float tr1 = c0 - c3;
                float tr3 = 2.0f * input[BackwardInput(ido - 1, 1, k, ido)];
                float tr4 = 2.0f * input[BackwardInput(0, 2, k, ido)];
                output[BackwardOutput(0, k, 0, ido, l1)] = tr2 + tr3;
                output[BackwardOutput(0, k, 2, ido, l1)] = tr2 - tr3;
                output[BackwardOutput(0, k, 3, ido, l1)] = tr1 + tr4;
                output[BackwardOutput(0, k, 1, ido, l1)] = tr1 - tr4;
            }

            if ((ido & 1) == 0)
            {
                for (int k = 0; k < l1; k++)
                {
                    float c3 = input[BackwardInput(0, 3, k, ido)];
                    float c1 = input[BackwardInput(0, 1, k, ido)];
                    float ti1 = c3 + c1;
                    float ti2 = c3 - c1;
                    float c0 = input[BackwardInput(ido - 1, 0, k, ido)];
                    float c2 = input[BackwardInput(ido - 1, 2, k, ido)];
                    float tr2 = c0 + c2;
                    float tr1 = c0 - c2;
                    output[BackwardOutput(ido - 1, k, 0, ido, l1)] = tr2 + tr2;
                    output[BackwardOutput(ido - 1, k, 1, ido, l1)] =
                        Sqrt2 * (tr1 - ti1);
                    output[BackwardOutput(ido - 1, k, 2, ido, l1)] = ti2 + ti2;
                    output[BackwardOutput(ido - 1, k, 3, ido, l1)] =
                        -Sqrt2 * (tr1 + ti1);
                }
            }

            if (ido <= 2)
            {
                return;
            }

            int stride = ido - 1;
            for (int k = 0; k < l1; k++)
            {
                for (int i = 2; i < ido; i += 2)
                {
                    int ic = ido - i;
                    float c10 = input[BackwardInput(i - 1, 0, k, ido)];
                    float c43 = input[BackwardInput(ic - 1, 3, k, ido)];
                    float tr2 = c10 + c43;
                    float tr1 = c10 - c43;
                    float c20 = input[BackwardInput(i, 0, k, ido)];
                    float c53 = input[BackwardInput(ic, 3, k, ido)];
                    float ti1 = c20 + c53;
                    float ti2 = c20 - c53;
                    float c32 = input[BackwardInput(i, 2, k, ido)];
                    float c61 = input[BackwardInput(ic, 1, k, ido)];
                    float tr4 = c32 + c61;
                    float ti3 = c32 - c61;
                    float c42 = input[BackwardInput(i - 1, 2, k, ido)];
                    float c71 = input[BackwardInput(ic - 1, 1, k, ido)];
                    float tr3 = c42 + c71;
                    float ti4 = c42 - c71;

                    output[BackwardOutput(i - 1, k, 0, ido, l1)] = tr2 + tr3;
                    float cr3 = tr2 - tr3;
                    output[BackwardOutput(i, k, 0, ido, l1)] = ti2 + ti3;
                    float ci3 = ti2 - ti3;
                    float cr4 = tr1 + tr4;
                    float cr2 = tr1 - tr4;
                    float ci2 = ti1 + ti4;
                    float ci4 = ti1 - ti4;

                    MultiplyConjugate(
                        twiddles[i - 2],
                        twiddles[i - 1],
                        ci2,
                        cr2,
                        out output[BackwardOutput(i, k, 1, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 1, ido, l1)]);
                    MultiplyConjugate(
                        twiddles[stride + i - 2],
                        twiddles[stride + i - 1],
                        ci3,
                        cr3,
                        out output[BackwardOutput(i, k, 2, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 2, ido, l1)]);
                    MultiplyConjugate(
                        twiddles[(2 * stride) + i - 2],
                        twiddles[(2 * stride) + i - 1],
                        ci4,
                        cr4,
                        out output[BackwardOutput(i, k, 3, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 3, ido, l1)]);
                }
            }
        }

        private static void MultiplyConjugate(
            float c,
            float d,
            float e,
            float f,
            out float real,
            out float imaginary)
        {
            real = (c * e) + (d * f);
            imaginary = (c * f) - (d * e);
        }

        private static int ForwardInput(
            int a,
            int b,
            int c,
            int ido,
            int l1)
            => a + (ido * (b + (l1 * c)));

        private static int ForwardOutput(int a, int b, int c, int ido)
            => a + (ido * (b + (4 * c)));

        private static int BackwardInput(int a, int b, int c, int ido)
            => a + (ido * (b + (4 * c)));

        private static int BackwardOutput(
            int a,
            int b,
            int c,
            int ido,
            int l1)
            => a + (ido * (b + (l1 * c)));
    }

    private sealed record Factor(float[] Twiddles);

    private readonly record struct DoubleTwiddle(double Real, double Imaginary);

    private readonly record struct FloatTwiddle(float Real, float Imaginary);

    private sealed class UnityRoots
    {
        private readonly DoubleTwiddle[] _first;
        private readonly int _length;
        private readonly int _mask;
        private readonly DoubleTwiddle[] _second;
        private readonly int _shift;

        internal UnityRoots(int length)
        {
            _length = length;
            int nValue = (length + 2) / 2;
            int shift = 1;
            while ((1 << shift) * (1 << shift) < nValue)
            {
                shift++;
            }

            _shift = shift;
            _mask = (1 << shift) - 1;
            double angle = 0.25 * Math.PI / length;
            _first = new DoubleTwiddle[_mask + 1];
            _first[0] = new DoubleTwiddle(1.0, 0.0);
            for (int i = 1; i < _first.Length; i++)
            {
                _first[i] = Calculate(i, length, angle);
            }

            _second = new DoubleTwiddle[(nValue + _mask) / (_mask + 1)];
            _second[0] = new DoubleTwiddle(1.0, 0.0);
            for (int i = 1; i < _second.Length; i++)
            {
                _second[i] = Calculate(i * (_mask + 1), length, angle);
            }
        }

        internal FloatTwiddle Get(int index)
        {
            bool conjugate = 2 * index > _length;
            if (conjugate)
            {
                index = _length - index;
            }

            DoubleTwiddle left = _first[index & _mask];
            DoubleTwiddle right = _second[index >> _shift];
            double real = (left.Real * right.Real)
                - (left.Imaginary * right.Imaginary);
            double imaginary = (left.Real * right.Imaginary)
                + (left.Imaginary * right.Real);
            return new FloatTwiddle(
                (float)real,
                (float)(conjugate ? -imaginary : imaginary));
        }

        private static DoubleTwiddle Calculate(
            int index,
            int length,
            double angle)
        {
            int x = index << 3;
            if (x < 4 * length)
            {
                if (x < 2 * length)
                {
                    if (x < length)
                    {
                        return new DoubleTwiddle(
                            Math.Cos(x * angle),
                            Math.Sin(x * angle));
                    }

                    return new DoubleTwiddle(
                        Math.Sin((2 * length - x) * angle),
                        Math.Cos((2 * length - x) * angle));
                }

                x -= 2 * length;
                if (x < length)
                {
                    return new DoubleTwiddle(
                        -Math.Sin(x * angle),
                        Math.Cos(x * angle));
                }

                return new DoubleTwiddle(
                    -Math.Cos((2 * length - x) * angle),
                    Math.Sin((2 * length - x) * angle));
            }

            x = 8 * length - x;
            if (x < 2 * length)
            {
                if (x < length)
                {
                    return new DoubleTwiddle(
                        Math.Cos(x * angle),
                        -Math.Sin(x * angle));
                }

                return new DoubleTwiddle(
                    Math.Sin((2 * length - x) * angle),
                    -Math.Cos((2 * length - x) * angle));
            }

            x -= 2 * length;
            if (x < length)
            {
                return new DoubleTwiddle(
                    -Math.Sin(x * angle),
                    -Math.Cos(x * angle));
            }

            return new DoubleTwiddle(
                -Math.Cos((2 * length - x) * angle),
                -Math.Sin((2 * length - x) * angle));
        }
    }
}
