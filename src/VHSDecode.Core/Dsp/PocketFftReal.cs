// Radix-2/4 real FFT adapted from pocketfft's BSD-3-Clause implementation.
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;

namespace VHSDecode.Core.Dsp;

public static class PocketFftReal
{
    private static readonly ConcurrentDictionary<int, Plan> Plans = new();

    public static Complex[] Forward(ReadOnlySpan<double> input)
    {
        if (input.Length < 2 || (input.Length & (input.Length - 1)) != 0)
        {
            throw new ArgumentException("Real FFT length must be a power of two of at least two.", nameof(input));
        }

        var output = new Complex[(input.Length / 2) + 1];
        Plans.GetOrAdd(input.Length, static length => new Plan(length)).Forward(input, output);
        return output;
    }

    internal static void Forward(ReadOnlySpan<double> input, Complex[] output)
    {
        if (input.Length < 2 || (input.Length & (input.Length - 1)) != 0)
        {
            throw new ArgumentException("Real FFT length must be a power of two of at least two.", nameof(input));
        }

        ArgumentNullException.ThrowIfNull(output);
        int outputLength = (input.Length / 2) + 1;
        if (output.Length < outputLength)
        {
            throw new ArgumentException("Output buffer is shorter than the real half-spectrum.", nameof(output));
        }

        Plans.GetOrAdd(input.Length, static length => new Plan(length)).Forward(input, output);
    }

    public static double[] Inverse(ReadOnlySpan<Complex> input, int outputLength)
    {
        if (outputLength < 2 || (outputLength & (outputLength - 1)) != 0)
        {
            throw new ArgumentException("Real FFT length must be a power of two of at least two.", nameof(outputLength));
        }

        if (input.Length != (outputLength / 2) + 1)
        {
            throw new ArgumentException("Half-spectrum length does not match the requested real output length.", nameof(input));
        }

        var output = new double[outputLength];
        Plans.GetOrAdd(outputLength, static length => new Plan(length)).Inverse(input, output);
        return output;
    }

    internal static void Inverse(
        ReadOnlySpan<Complex> input,
        int outputLength,
        double[] output)
    {
        if (outputLength < 2 || (outputLength & (outputLength - 1)) != 0)
        {
            throw new ArgumentException("Real FFT length must be a power of two of at least two.", nameof(outputLength));
        }

        if (input.Length != (outputLength / 2) + 1)
        {
            throw new ArgumentException("Half-spectrum length does not match the requested real output length.", nameof(input));
        }

        ArgumentNullException.ThrowIfNull(output);
        if (output.Length < outputLength)
        {
            throw new ArgumentException("Output buffer is shorter than the requested real transform length.", nameof(output));
        }

        Plans.GetOrAdd(outputLength, static length => new Plan(length)).Inverse(input, output);
    }

    private sealed class Plan
    {
        private readonly int _length;
        private readonly Factor[] _factors;

        public Plan(int length)
        {
            _length = length;
            int[] radices = Factorize(length);
            _factors = BuildFactors(length, radices);
        }

        public void Forward(ReadOnlySpan<double> input, Complex[] output)
        {
            double[] packed = ArrayPool<double>.Shared.Rent(_length);
            try
            {
                input.CopyTo(packed);
                ExecuteForward(packed);
                output[0] = new Complex(packed[0], 0.0);
                int outputLength = (_length / 2) + 1;
                for (int i = 1; i < outputLength - 1; i++)
                {
                    output[i] = new Complex(packed[(2 * i) - 1], packed[2 * i]);
                }

                output[outputLength - 1] = new Complex(packed[_length - 1], 0.0);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(packed);
            }
        }

        public void Inverse(ReadOnlySpan<Complex> input, double[] output)
        {
            output[0] = input[0].Real;
            for (int i = 1; i < input.Length - 1; i++)
            {
                output[(2 * i) - 1] = input[i].Real;
                output[2 * i] = input[i].Imaginary;
            }

            output[_length - 1] = input[^1].Real;
            ExecuteBackward(output, 1.0 / _length);
        }

        private void ExecuteForward(double[] data)
        {
            double[] scratch = ArrayPool<double>.Shared.Rent(_length);
            try
            {
                double[] source = data;
                double[] destination = scratch;
                int l1 = _length;
                for (int pass = 0; pass < _factors.Length; pass++)
                {
                    Factor factor = _factors[_factors.Length - pass - 1];
                    int ido = _length / l1;
                    l1 /= factor.Radix;
                    if (factor.Radix == 4)
                    {
                        Radix4Forward(ido, l1, source, destination, factor.Twiddles);
                    }
                    else
                    {
                        Radix2Forward(ido, l1, source, destination, factor.Twiddles);
                    }

                    (source, destination) = (destination, source);
                }

                if (!ReferenceEquals(source, data))
                {
                    source.AsSpan(0, _length).CopyTo(data);
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(scratch);
            }
        }

        private void ExecuteBackward(double[] data, double normalization)
        {
            double[] scratch = ArrayPool<double>.Shared.Rent(_length);
            try
            {
                double[] source = data;
                double[] destination = scratch;
                int l1 = 1;
                foreach (Factor factor in _factors)
                {
                    int ido = _length / (factor.Radix * l1);
                    if (factor.Radix == 4)
                    {
                        Radix4Backward(ido, l1, source, destination, factor.Twiddles);
                    }
                    else
                    {
                        Radix2Backward(ido, l1, source, destination, factor.Twiddles);
                    }

                    (source, destination) = (destination, source);
                    l1 *= factor.Radix;
                }

                for (int i = 0; i < _length; i++)
                {
                    data[i] = normalization * source[i];
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(scratch);
            }
        }

        private static int[] Factorize(int length)
        {
            var factors = new List<int>();
            int remaining = length;
            while (remaining % 4 == 0)
            {
                factors.Add(4);
                remaining >>= 2;
            }

            if (remaining % 2 == 0)
            {
                remaining >>= 1;
                factors.Add(2);
                (factors[0], factors[^1]) = (factors[^1], factors[0]);
            }

            if (remaining != 1)
            {
                throw new ArgumentException("Only power-of-two real FFT lengths are supported.", nameof(length));
            }

            return factors.ToArray();
        }

        private static Factor[] BuildFactors(int length, int[] radices)
        {
            var factors = new Factor[radices.Length];
            var twiddle = new SinCos2PiByN(length);
            int l1 = 1;
            for (int k = 0; k < radices.Length; k++)
            {
                int radix = radices[k];
                int ido = length / (l1 * radix);
                double[] values = k == radices.Length - 1
                    ? []
                    : new double[(radix - 1) * (ido - 1)];
                for (int j = 1; j < radix; j++)
                {
                    for (int i = 1; i <= (ido - 1) / 2; i++)
                    {
                        Twiddle value = twiddle.Get(j * l1 * i);
                        int offset = ((j - 1) * (ido - 1)) + (2 * i) - 2;
                        values[offset] = value.Real;
                        values[offset + 1] = value.Imaginary;
                    }
                }

                factors[k] = new Factor(radix, values);
                l1 *= radix;
            }

            return factors;
        }

        private static void Radix2Forward(
            int ido,
            int l1,
            double[] input,
            double[] output,
            double[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                double left = input[ForwardInput(0, k, 0, ido, l1)];
                double right = input[ForwardInput(0, k, 1, ido, l1)];
                output[ForwardOutput(0, 0, k, ido, 2)] = left + right;
                output[ForwardOutput(ido - 1, 1, k, ido, 2)] = left - right;
            }

            if ((ido & 1) == 0)
            {
                for (int k = 0; k < l1; k++)
                {
                    output[ForwardOutput(0, 1, k, ido, 2)] =
                        -input[ForwardInput(ido - 1, k, 1, ido, l1)];
                    output[ForwardOutput(ido - 1, 0, k, ido, 2)] =
                        input[ForwardInput(ido - 1, k, 0, ido, l1)];
                }
            }

            if (ido <= 2)
            {
                return;
            }

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
                        out double tr2,
                        out double ti2);
                    double real = input[ForwardInput(i - 1, k, 0, ido, l1)];
                    output[ForwardOutput(i - 1, 0, k, ido, 2)] = real + tr2;
                    output[ForwardOutput(ic - 1, 1, k, ido, 2)] = real - tr2;
                    double imaginary = input[ForwardInput(i, k, 0, ido, l1)];
                    output[ForwardOutput(i, 0, k, ido, 2)] = ti2 + imaginary;
                    output[ForwardOutput(ic, 1, k, ido, 2)] = ti2 - imaginary;
                }
            }
        }

        private static void Radix4Forward(
            int ido,
            int l1,
            double[] input,
            double[] output,
            double[] twiddles)
        {
            const double HalfSqrt2 = 0.707106781186547524400844362104849;
            for (int k = 0; k < l1; k++)
            {
                double c3 = input[ForwardInput(0, k, 3, ido, l1)];
                double c1 = input[ForwardInput(0, k, 1, ido, l1)];
                double tr1 = c3 + c1;
                output[ForwardOutput(0, 2, k, ido, 4)] = c3 - c1;
                double c0 = input[ForwardInput(0, k, 0, ido, l1)];
                double c2 = input[ForwardInput(0, k, 2, ido, l1)];
                double tr2 = c0 + c2;
                output[ForwardOutput(ido - 1, 1, k, ido, 4)] = c0 - c2;
                output[ForwardOutput(0, 0, k, ido, 4)] = tr2 + tr1;
                output[ForwardOutput(ido - 1, 3, k, ido, 4)] = tr2 - tr1;
            }

            if ((ido & 1) == 0)
            {
                for (int k = 0; k < l1; k++)
                {
                    double c1 = input[ForwardInput(ido - 1, k, 1, ido, l1)];
                    double c3 = input[ForwardInput(ido - 1, k, 3, ido, l1)];
                    double ti1 = -HalfSqrt2 * (c1 + c3);
                    double tr1 = HalfSqrt2 * (c1 - c3);
                    double c0 = input[ForwardInput(ido - 1, k, 0, ido, l1)];
                    output[ForwardOutput(ido - 1, 0, k, ido, 4)] = c0 + tr1;
                    output[ForwardOutput(ido - 1, 2, k, ido, 4)] = c0 - tr1;
                    double c2 = input[ForwardInput(ido - 1, k, 2, ido, l1)];
                    output[ForwardOutput(0, 3, k, ido, 4)] = ti1 + c2;
                    output[ForwardOutput(0, 1, k, ido, 4)] = ti1 - c2;
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
                    MultiplyConjugate(twiddles[i - 2], twiddles[i - 1],
                        input[ForwardInput(i - 1, k, 1, ido, l1)], input[ForwardInput(i, k, 1, ido, l1)],
                        out double cr2, out double ci2);
                    MultiplyConjugate(twiddles[stride + i - 2], twiddles[stride + i - 1],
                        input[ForwardInput(i - 1, k, 2, ido, l1)], input[ForwardInput(i, k, 2, ido, l1)],
                        out double cr3, out double ci3);
                    MultiplyConjugate(twiddles[(2 * stride) + i - 2], twiddles[(2 * stride) + i - 1],
                        input[ForwardInput(i - 1, k, 3, ido, l1)], input[ForwardInput(i, k, 3, ido, l1)],
                        out double cr4, out double ci4);

                    double tr1 = cr4 + cr2;
                    double tr4 = cr4 - cr2;
                    double ti1 = ci2 + ci4;
                    double ti4 = ci2 - ci4;
                    double c0r = input[ForwardInput(i - 1, k, 0, ido, l1)];
                    double tr2 = c0r + cr3;
                    double tr3 = c0r - cr3;
                    double c0i = input[ForwardInput(i, k, 0, ido, l1)];
                    double ti2 = c0i + ci3;
                    double ti3 = c0i - ci3;

                    output[ForwardOutput(i - 1, 0, k, ido, 4)] = tr2 + tr1;
                    output[ForwardOutput(ic - 1, 3, k, ido, 4)] = tr2 - tr1;
                    output[ForwardOutput(i, 0, k, ido, 4)] = ti1 + ti2;
                    output[ForwardOutput(ic, 3, k, ido, 4)] = ti1 - ti2;
                    output[ForwardOutput(i - 1, 2, k, ido, 4)] = tr3 + ti4;
                    output[ForwardOutput(ic - 1, 1, k, ido, 4)] = tr3 - ti4;
                    output[ForwardOutput(i, 2, k, ido, 4)] = tr4 + ti3;
                    output[ForwardOutput(ic, 1, k, ido, 4)] = tr4 - ti3;
                }
            }
        }

        private static void Radix2Backward(
            int ido,
            int l1,
            double[] input,
            double[] output,
            double[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                double left = input[BackwardInput(0, 0, k, ido, 2)];
                double right = input[BackwardInput(ido - 1, 1, k, ido, 2)];
                output[BackwardOutput(0, k, 0, ido, l1)] = left + right;
                output[BackwardOutput(0, k, 1, ido, l1)] = left - right;
            }

            if ((ido & 1) == 0)
            {
                for (int k = 0; k < l1; k++)
                {
                    output[BackwardOutput(ido - 1, k, 0, ido, l1)] =
                        2.0 * input[BackwardInput(ido - 1, 0, k, ido, 2)];
                    output[BackwardOutput(ido - 1, k, 1, ido, l1)] =
                        -2.0 * input[BackwardInput(0, 1, k, ido, 2)];
                }
            }

            if (ido <= 2)
            {
                return;
            }

            for (int k = 0; k < l1; k++)
            {
                for (int i = 2; i < ido; i += 2)
                {
                    int ic = ido - i;
                    double c1 = input[BackwardInput(i - 1, 0, k, ido, 2)];
                    double c2 = input[BackwardInput(ic - 1, 1, k, ido, 2)];
                    output[BackwardOutput(i - 1, k, 0, ido, l1)] = c1 + c2;
                    double tr2 = c1 - c2;
                    double c3 = input[BackwardInput(i, 0, k, ido, 2)];
                    double c4 = input[BackwardInput(ic, 1, k, ido, 2)];
                    double ti2 = c3 + c4;
                    output[BackwardOutput(i, k, 0, ido, l1)] = c3 - c4;
                    MultiplyConjugate(
                        twiddles[i - 2],
                        twiddles[i - 1],
                        ti2,
                        tr2,
                        out output[BackwardOutput(i, k, 1, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 1, ido, l1)]);
                }
            }
        }

        private static unsafe void Radix4Backward(
            int ido,
            int l1,
            double[] input,
            double[] output,
            double[] twiddles)
        {
            const double Sqrt2 = 1.414213562373095048801688724209698;
            fixed (double* inputPointer = input)
            fixed (double* outputPointer = output)
            fixed (double* twiddlePointer = twiddles)
            {
                int inputGroupStride = 4 * ido;
                int outputGroupStride = ido * l1;
                for (int k = 0; k < l1; k++)
                {
                    double* inputGroup = inputPointer + (inputGroupStride * k);
                    double* outputGroup = outputPointer + (ido * k);
                    double c0 = inputGroup[0];
                    double c3 = inputGroup[inputGroupStride - 1];
                    double tr2 = c0 + c3;
                    double tr1 = c0 - c3;
                    double tr3 = 2.0 * inputGroup[(2 * ido) - 1];
                    double tr4 = 2.0 * inputGroup[2 * ido];
                    outputGroup[0] = tr2 + tr3;
                    outputGroup[2 * outputGroupStride] = tr2 - tr3;
                    outputGroup[3 * outputGroupStride] = tr1 + tr4;
                    outputGroup[outputGroupStride] = tr1 - tr4;
                }

                if ((ido & 1) == 0)
                {
                    for (int k = 0; k < l1; k++)
                    {
                        double* inputGroup = inputPointer + (inputGroupStride * k);
                        double* outputGroup = outputPointer + (ido * k) + ido - 1;
                        double c3 = inputGroup[3 * ido];
                        double c1 = inputGroup[ido];
                        double ti1 = c3 + c1;
                        double ti2 = c3 - c1;
                        double c0 = inputGroup[ido - 1];
                        double c2 = inputGroup[(3 * ido) - 1];
                        double tr2 = c0 + c2;
                        double tr1 = c0 - c2;
                        outputGroup[0] = tr2 + tr2;
                        outputGroup[outputGroupStride] = Sqrt2 * (tr1 - ti1);
                        outputGroup[2 * outputGroupStride] = ti2 + ti2;
                        outputGroup[3 * outputGroupStride] = -Sqrt2 * (tr1 + ti1);
                    }
                }

                if (ido <= 2)
                {
                    return;
                }

                int stride = ido - 1;
                for (int k = 0; k < l1; k++)
                {
                    double* inputGroup = inputPointer + (inputGroupStride * k);
                    double* outputGroup = outputPointer + (ido * k);
                    for (int i = 2; i < ido; i += 2)
                    {
                        int ic = ido - i;
                        double c10 = inputGroup[i - 1];
                        double c43 = inputGroup[(3 * ido) + ic - 1];
                        double tr2 = c10 + c43;
                        double tr1 = c10 - c43;
                        double c20 = inputGroup[i];
                        double c53 = inputGroup[(3 * ido) + ic];
                        double ti1 = c20 + c53;
                        double ti2 = c20 - c53;
                        double c32 = inputGroup[(2 * ido) + i];
                        double c61 = inputGroup[ido + ic];
                        double tr4 = c32 + c61;
                        double ti3 = c32 - c61;
                        double c42 = inputGroup[(2 * ido) + i - 1];
                        double c71 = inputGroup[ido + ic - 1];
                        double tr3 = c42 + c71;
                        double ti4 = c42 - c71;

                        outputGroup[i - 1] = tr2 + tr3;
                        double cr3 = tr2 - tr3;
                        outputGroup[i] = ti2 + ti3;
                        double ci3 = ti2 - ti3;
                        double cr4 = tr1 + tr4;
                        double cr2 = tr1 - tr4;
                        double ci2 = ti1 + ti4;
                        double ci4 = ti1 - ti4;

                        MultiplyConjugate(twiddlePointer[i - 2], twiddlePointer[i - 1], ci2, cr2,
                            out outputGroup[outputGroupStride + i],
                            out outputGroup[outputGroupStride + i - 1]);
                        MultiplyConjugate(twiddlePointer[stride + i - 2], twiddlePointer[stride + i - 1], ci3, cr3,
                            out outputGroup[(2 * outputGroupStride) + i],
                            out outputGroup[(2 * outputGroupStride) + i - 1]);
                        MultiplyConjugate(
                            twiddlePointer[(2 * stride) + i - 2],
                            twiddlePointer[(2 * stride) + i - 1],
                            ci4,
                            cr4,
                            out outputGroup[(3 * outputGroupStride) + i],
                            out outputGroup[(3 * outputGroupStride) + i - 1]);
                    }
                }
            }
        }

        private static void MultiplyConjugate(
            double c,
            double d,
            double e,
            double f,
            out double real,
            out double imaginary)
        {
            real = (c * e) + (d * f);
            imaginary = (c * f) - (d * e);
        }

        private static int ForwardInput(int a, int b, int c, int ido, int l1)
            => a + (ido * (b + (l1 * c)));

        private static int ForwardOutput(int a, int b, int c, int ido, int radix)
            => a + (ido * (b + (radix * c)));

        private static int BackwardInput(int a, int b, int c, int ido, int radix)
            => a + (ido * (b + (radix * c)));

        private static int BackwardOutput(int a, int b, int c, int ido, int l1)
            => a + (ido * (b + (l1 * c)));
    }

    private sealed record Factor(int Radix, double[] Twiddles);

    private readonly record struct Twiddle(double Real, double Imaginary);

    private sealed class SinCos2PiByN
    {
        private readonly int _length;
        private readonly int _mask;
        private readonly int _shift;
        private readonly Twiddle[] _first;
        private readonly Twiddle[] _second;

        public SinCos2PiByN(int length)
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
            _first = new Twiddle[_mask + 1];
            _first[0] = new Twiddle(1.0, 0.0);
            for (int i = 1; i < _first.Length; i++)
            {
                _first[i] = Calculate(i, length, angle);
            }

            _second = new Twiddle[(nValue + _mask) / (_mask + 1)];
            _second[0] = new Twiddle(1.0, 0.0);
            for (int i = 1; i < _second.Length; i++)
            {
                _second[i] = Calculate(i * (_mask + 1), length, angle);
            }
        }

        public Twiddle Get(int index)
        {
            if (2 * index <= _length)
            {
                return Multiply(_first[index & _mask], _second[index >> _shift], conjugate: false);
            }

            index = _length - index;
            return Multiply(_first[index & _mask], _second[index >> _shift], conjugate: true);
        }

        private static Twiddle Multiply(Twiddle left, Twiddle right, bool conjugate)
        {
            double real = (left.Real * right.Real) - (left.Imaginary * right.Imaginary);
            double imaginary = (left.Real * right.Imaginary) + (left.Imaginary * right.Real);
            return new Twiddle(real, conjugate ? -imaginary : imaginary);
        }

        private static Twiddle Calculate(int index, int length, double angle)
        {
            int x = index << 3;
            if (x < 4 * length)
            {
                if (x < 2 * length)
                {
                    if (x < length)
                    {
                        return new Twiddle(Math.Cos(x * angle), Math.Sin(x * angle));
                    }

                    return new Twiddle(
                        Math.Sin((2 * length - x) * angle),
                        Math.Cos((2 * length - x) * angle));
                }

                x -= 2 * length;
                if (x < length)
                {
                    return new Twiddle(-Math.Sin(x * angle), Math.Cos(x * angle));
                }

                return new Twiddle(
                    -Math.Cos((2 * length - x) * angle),
                    Math.Sin((2 * length - x) * angle));
            }

            x = 8 * length - x;
            if (x < 2 * length)
            {
                if (x < length)
                {
                    return new Twiddle(Math.Cos(x * angle), -Math.Sin(x * angle));
                }

                return new Twiddle(
                    Math.Sin((2 * length - x) * angle),
                    -Math.Cos((2 * length - x) * angle));
            }

            x -= 2 * length;
            if (x < length)
            {
                return new Twiddle(-Math.Sin(x * angle), -Math.Cos(x * angle));
            }

            return new Twiddle(
                -Math.Cos((2 * length - x) * angle),
                -Math.Sin((2 * length - x) * angle));
        }
    }
}
