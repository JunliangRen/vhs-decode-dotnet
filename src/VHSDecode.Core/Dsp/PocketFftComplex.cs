// Radix-2/4/8 complex FFT adapted from pocketfft's BSD-3-Clause implementation.
using System.Collections.Concurrent;
using System.Numerics;

namespace VHSDecode.Core.Dsp;

public static class PocketFftComplex
{
    private static readonly ConcurrentDictionary<int, Plan> Plans = new();
    private static readonly ConcurrentDictionary<(int Length, int RootLength), Plan> RootedPlans = new();

    public static Complex[] Forward(ReadOnlySpan<Complex> input)
        => Transform(input, forward: true);

    private static Complex[] TransformWithRootLength(
        ReadOnlySpan<Complex> input,
        int rootLength,
        bool forward)
    {
        ValidateLength(input.Length, nameof(input));
        return RootedPlans.GetOrAdd(
                (input.Length, rootLength),
                static key => new Plan(key.Length, key.RootLength))
            .Transform(input, forward);
    }

    private static Complex[] TransformDuccPacketized(ReadOnlySpan<Complex> input, bool forward)
    {
        ValidateLength(input.Length, nameof(input));
        if (input.Length <= 10_000)
        {
            throw new ArgumentException(
                "DUCC packetization is only used for transforms longer than 10000 samples.",
                nameof(input));
        }

        int firstPacketLength = 1;
        int secondPacketLength = 1;
        for (int remaining = input.Length; remaining > 1; remaining >>= 1)
        {
            if (firstPacketLength > secondPacketLength)
            {
                secondPacketLength <<= 1;
            }
            else
            {
                firstPacketLength <<= 1;
            }
        }

        int length = input.Length;
        var roots = new SinCos2PiByN(length);
        var stage = new Complex[length];
        var firstPacket = new Complex[firstPacketLength];
        for (int i = 0; i < secondPacketLength; i++)
        {
            for (int m = 0; m < firstPacketLength; m++)
            {
                firstPacket[m] = input[i + (secondPacketLength * m)];
            }

            Complex[] transformed = TransformWithRootLength(firstPacket, length, forward);
            for (int m = 0; m < firstPacketLength; m++)
            {
                Complex value = transformed[m];
                if (i != 0 && m != 0)
                {
                    Value root = roots.Get(m * i);
                    Value multiplied = SpecialMultiply(
                        new Value(value.Real, value.Imaginary),
                        root,
                        forward);
                    value = new Complex(multiplied.Real, multiplied.Imaginary);
                }

                stage[i + (secondPacketLength * m)] = value;
            }
        }

        var output = new Complex[length];
        var secondPacket = new Complex[secondPacketLength];
        for (int k = 0; k < firstPacketLength; k++)
        {
            int offset = secondPacketLength * k;
            stage.AsSpan(offset, secondPacketLength).CopyTo(secondPacket);
            Complex[] transformed = TransformWithRootLength(secondPacket, length, forward);
            for (int m = 0; m < secondPacketLength; m++)
            {
                output[k + (firstPacketLength * m)] = transformed[m];
            }
        }

        return output;
    }

    internal static Complex[] ForwardDucc(ReadOnlySpan<Complex> input)
    {
        ValidateLength(input.Length, nameof(input));
        return input.Length > 10_000
            ? TransformDuccPacketized(input, forward: true)
            : Forward(input);
    }

    internal static Complex[] ForwardDucc(ReadOnlySpan<double> input)
    {
        ValidateLength(input.Length, nameof(input));
        var complexInput = new Complex[input.Length];
        for (int i = 0; i < complexInput.Length; i++)
        {
            complexInput[i] = new Complex(input[i], 0.0);
        }

        return input.Length > 10_000
            ? TransformDuccPacketized(complexInput, forward: true)
            : Forward(complexInput);
    }

    internal static Complex[] InverseDucc(ReadOnlySpan<Complex> input)
    {
        ValidateLength(input.Length, nameof(input));
        return input.Length > 10_000
            ? TransformDuccPacketized(input, forward: false)
            : Inverse(input);
    }

    internal static Complex[] ForwardDuccReal(ReadOnlySpan<double> input)
    {
        ValidateLength(input.Length, nameof(input));
        int length = input.Length;
        int complexLength = length / 2;
        var complexInput = new Complex[complexLength];
        for (int i = 0; i < complexLength; i++)
        {
            complexInput[i] = new Complex(input[2 * i], input[(2 * i) + 1]);
        }

        Complex[] transformed = complexLength > 10_000
            ? TransformDuccPacketized(complexInput, forward: true)
            : Forward(complexInput);
        var roots = new SinCos2PiByN(length);
        var packed = new double[length];
        packed[0] = transformed[0].Real + transformed[0].Imaginary;
        for (int i = 1, xi = complexLength - 1; i <= xi; i++, xi--)
        {
            Complex left = transformed[i];
            Complex right = transformed[xi];
            Value even = new(
                left.Real + right.Real,
                left.Imaginary - right.Imaginary);
            Value odd = new(
                left.Imaginary + right.Imaginary,
                right.Real - left.Real);
            Value root = roots.Get(i);
            Value rotated = SpecialMultiply(odd, root, forward: true);
            packed[(2 * i) - 1] = 0.5 * (even.Real + rotated.Real);
            packed[2 * i] = 0.5 * (even.Imaginary + rotated.Imaginary);
            packed[(2 * xi) - 1] = 0.5 * (even.Real - rotated.Real);
            packed[2 * xi] = 0.5 * (rotated.Imaginary - even.Imaginary);
        }

        packed[^1] = transformed[0].Real - transformed[0].Imaginary;
        var output = new Complex[complexLength + 1];
        output[0] = new Complex(packed[0], 0.0);
        for (int i = 1; i < output.Length - 1; i++)
        {
            output[i] = new Complex(packed[(2 * i) - 1], packed[2 * i]);
        }

        output[^1] = new Complex(packed[^1], 0.0);
        return output;
    }

    internal static Complex[] ForwardDuccRealFull(ReadOnlySpan<double> input)
    {
        Complex[] halfSpectrum = ForwardDuccReal(input);
        var output = new Complex[input.Length];
        halfSpectrum.CopyTo(output, 0);
        for (int i = 1; i < halfSpectrum.Length - 1; i++)
        {
            output[^i] = Complex.Conjugate(halfSpectrum[i]);
        }

        // scipy.fft.fft(real) preserves negative imaginary zero at both real-only bins.
        output[0] = new Complex(output[0].Real, -0.0);
        output[input.Length / 2] = new Complex(output[input.Length / 2].Real, -0.0);
        return output;
    }

    internal static double[] InverseDuccReal(ReadOnlySpan<Complex> input, int outputLength)
    {
        ValidateLength(outputLength, nameof(outputLength));
        if (input.Length != (outputLength / 2) + 1)
        {
            throw new ArgumentException(
                "Half-spectrum length does not match the requested real output length.",
                nameof(input));
        }

        int complexLength = outputLength / 2;
        var packedSpectrum = new Complex[complexLength];
        packedSpectrum[0] = new Complex(
            0.5 * (input[0].Real + input[^1].Real),
            0.5 * (input[0].Real - input[^1].Real));
        var roots = new SinCos2PiByN(outputLength);
        for (int i = 1, xi = complexLength - 1; i <= xi; i++, xi--)
        {
            Complex left = input[i];
            Complex right = input[xi];
            Value even = new(
                left.Real + right.Real,
                left.Imaginary - right.Imaginary);
            Value rotated = new(
                left.Real - right.Real,
                left.Imaginary + right.Imaginary);
            Value odd = SpecialMultiply(rotated, roots.Get(i), forward: false);
            packedSpectrum[i] = new Complex(
                0.5 * (even.Real - odd.Imaginary),
                0.5 * (even.Imaginary + odd.Real));
            packedSpectrum[xi] = new Complex(
                0.5 * (even.Real + odd.Imaginary),
                0.5 * (odd.Real - even.Imaginary));
        }

        Complex[] transformed = complexLength > 10_000
            ? TransformDuccPacketized(packedSpectrum, forward: false)
            : Inverse(packedSpectrum);
        var output = new double[outputLength];
        for (int i = 0; i < transformed.Length; i++)
        {
            output[2 * i] = transformed[i].Real;
            output[(2 * i) + 1] = transformed[i].Imaginary;
        }

        return output;
    }

    public static Complex[] ForwardReal(ReadOnlySpan<double> input)
    {
        ValidateLength(input.Length, nameof(input));
        var values = new Complex[input.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = new Complex(input[i], 0.0);
        }

        return Plans.GetOrAdd(input.Length, static length => new Plan(length))
            .Transform(values, forward: true);
    }

    public static Complex[] Inverse(ReadOnlySpan<Complex> input)
        => Transform(input, forward: false);

    private static Complex[] Transform(ReadOnlySpan<Complex> input, bool forward)
    {
        ValidateLength(input.Length, nameof(input));
        return Plans.GetOrAdd(input.Length, static length => new Plan(length))
            .Transform(input, forward);
    }

    private static void ValidateLength(int length, string parameterName)
    {
        if (length < 2 || (length & (length - 1)) != 0)
        {
            throw new ArgumentException(
                "Complex FFT length must be a power of two of at least two.",
                parameterName);
        }
    }

    private sealed class Plan
    {
        private readonly int _length;
        private readonly Factor[] _factors;

        public Plan(int length)
            : this(length, length)
        {
        }

        internal Plan(int length, int rootLength)
        {
            _length = length;
            if (rootLength < length || rootLength % length != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rootLength));
            }

            _factors = BuildFactors(
                length,
                Factorize(length),
                new SinCos2PiByN(rootLength),
                rootLength / length);
        }

        public Complex[] Transform(ReadOnlySpan<Complex> input, bool forward)
        {
            var values = new Value[_length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = new Value(input[i].Real, input[i].Imaginary);
            }

            Execute(values, forward, forward ? 1.0 : 1.0 / _length);
            var output = new Complex[_length];
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = new Complex(values[i].Real, values[i].Imaginary);
            }

            return output;
        }

        private void Execute(Value[] data, bool forward, double normalization)
        {
            var scratch = new Value[_length];
            Value[] source = data;
            Value[] destination = scratch;
            int l1 = 1;
            foreach (Factor factor in _factors)
            {
                int ido = _length / (factor.Radix * l1);
                switch (factor.Radix)
                {
                    case 2:
                        Pass2(ido, l1, source, destination, factor.Twiddles, forward);
                        break;
                    case 4:
                        Pass4(ido, l1, source, destination, factor.Twiddles, forward);
                        break;
                    case 8:
                        Pass8(ido, l1, source, destination, factor.Twiddles, forward);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported complex FFT radix {factor.Radix}.");
                }

                (source, destination) = (destination, source);
                l1 *= factor.Radix;
            }

            if (!ReferenceEquals(source, data))
            {
                if (normalization == 1.0)
                {
                    source.CopyTo(data, 0);
                }
                else
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = Scale(source[i], normalization);
                    }
                }
            }
            else if (normalization != 1.0)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = Scale(data[i], normalization);
                }
            }
        }

        private static void Pass2(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles,
            bool forward)
        {
            for (int k = 0; k < l1; k++)
            {
                Value left = input[InputIndex(0, 0, k, ido, 2)];
                Value right = input[InputIndex(0, 1, k, ido, 2)];
                Pair(out Value sum, out Value difference, left, right);
                output[OutputIndex(0, k, 0, ido, l1)] = sum;
                output[OutputIndex(0, k, 1, ido, l1)] = difference;
                for (int i = 1; i < ido; i++)
                {
                    left = input[InputIndex(i, 0, k, ido, 2)];
                    right = input[InputIndex(i, 1, k, ido, 2)];
                    output[OutputIndex(i, k, 0, ido, l1)] = Add(left, right);
                    output[OutputIndex(i, k, 1, ido, l1)] = SpecialMultiply(
                        Subtract(left, right),
                        Twiddle(twiddles, 0, i, ido),
                        forward);
                }
            }
        }

        private static void Pass4(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles,
            bool forward)
        {
            for (int k = 0; k < l1; k++)
            {
                Pass4FirstIndex(ido, l1, input, output, k, forward);
                for (int i = 1; i < ido; i++)
                {
                    Value c0 = input[InputIndex(i, 0, k, ido, 4)];
                    Value c1 = input[InputIndex(i, 1, k, ido, 4)];
                    Value c2 = input[InputIndex(i, 2, k, ido, 4)];
                    Value c3 = input[InputIndex(i, 3, k, ido, 4)];
                    Pair(out Value t2, out Value t1, c0, c2);
                    Pair(out Value t3, out Value t4, c1, c3);
                    t4 = RotateX90(t4, forward);
                    output[OutputIndex(i, k, 0, ido, l1)] = Add(t2, t3);
                    output[OutputIndex(i, k, 1, ido, l1)] = SpecialMultiply(
                        Add(t1, t4),
                        Twiddle(twiddles, 0, i, ido),
                        forward);
                    output[OutputIndex(i, k, 2, ido, l1)] = SpecialMultiply(
                        Subtract(t2, t3),
                        Twiddle(twiddles, 1, i, ido),
                        forward);
                    output[OutputIndex(i, k, 3, ido, l1)] = SpecialMultiply(
                        Subtract(t1, t4),
                        Twiddle(twiddles, 2, i, ido),
                        forward);
                }
            }
        }

        private static void Pass4FirstIndex(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            int k,
            bool forward)
        {
            Pair(
                out Value t2,
                out Value t1,
                input[InputIndex(0, 0, k, ido, 4)],
                input[InputIndex(0, 2, k, ido, 4)]);
            Pair(
                out Value t3,
                out Value t4,
                input[InputIndex(0, 1, k, ido, 4)],
                input[InputIndex(0, 3, k, ido, 4)]);
            t4 = RotateX90(t4, forward);
            Pair(out Value output0, out Value output2, t2, t3);
            Pair(out Value output1, out Value output3, t1, t4);
            output[OutputIndex(0, k, 0, ido, l1)] = output0;
            output[OutputIndex(0, k, 1, ido, l1)] = output1;
            output[OutputIndex(0, k, 2, ido, l1)] = output2;
            output[OutputIndex(0, k, 3, ido, l1)] = output3;
        }

        private static void Pass8(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles,
            bool forward)
        {
            for (int k = 0; k < l1; k++)
            {
                Pass8FirstIndex(ido, l1, input, output, k, forward);
                for (int i = 1; i < ido; i++)
                {
                    Pair(
                        out Value a1,
                        out Value a5,
                        input[InputIndex(i, 1, k, ido, 8)],
                        input[InputIndex(i, 5, k, ido, 8)]);
                    Pair(
                        out Value a3,
                        out Value a7,
                        input[InputIndex(i, 3, k, ido, 8)],
                        input[InputIndex(i, 7, k, ido, 8)]);
                    a7 = RotateX90(a7, forward);
                    PairInPlace(ref a1, ref a3);
                    a3 = RotateX90(a3, forward);
                    PairInPlace(ref a5, ref a7);
                    a5 = RotateX45(a5, forward);
                    a7 = RotateX135(a7, forward);
                    Pair(
                        out Value a0,
                        out Value a4,
                        input[InputIndex(i, 0, k, ido, 8)],
                        input[InputIndex(i, 4, k, ido, 8)]);
                    Pair(
                        out Value a2,
                        out Value a6,
                        input[InputIndex(i, 2, k, ido, 8)],
                        input[InputIndex(i, 6, k, ido, 8)]);
                    PairInPlace(ref a0, ref a2);
                    output[OutputIndex(i, k, 0, ido, l1)] = Add(a0, a1);
                    output[OutputIndex(i, k, 4, ido, l1)] = SpecialMultiply(
                        Subtract(a0, a1),
                        Twiddle(twiddles, 3, i, ido),
                        forward);
                    output[OutputIndex(i, k, 2, ido, l1)] = SpecialMultiply(
                        Add(a2, a3),
                        Twiddle(twiddles, 1, i, ido),
                        forward);
                    output[OutputIndex(i, k, 6, ido, l1)] = SpecialMultiply(
                        Subtract(a2, a3),
                        Twiddle(twiddles, 5, i, ido),
                        forward);
                    a6 = RotateX90(a6, forward);
                    PairInPlace(ref a4, ref a6);
                    output[OutputIndex(i, k, 1, ido, l1)] = SpecialMultiply(
                        Add(a4, a5),
                        Twiddle(twiddles, 0, i, ido),
                        forward);
                    output[OutputIndex(i, k, 5, ido, l1)] = SpecialMultiply(
                        Subtract(a4, a5),
                        Twiddle(twiddles, 4, i, ido),
                        forward);
                    output[OutputIndex(i, k, 3, ido, l1)] = SpecialMultiply(
                        Add(a6, a7),
                        Twiddle(twiddles, 2, i, ido),
                        forward);
                    output[OutputIndex(i, k, 7, ido, l1)] = SpecialMultiply(
                        Subtract(a6, a7),
                        Twiddle(twiddles, 6, i, ido),
                        forward);
                }
            }
        }

        private static void Pass8FirstIndex(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            int k,
            bool forward)
        {
            Pair(
                out Value a1,
                out Value a5,
                input[InputIndex(0, 1, k, ido, 8)],
                input[InputIndex(0, 5, k, ido, 8)]);
            Pair(
                out Value a3,
                out Value a7,
                input[InputIndex(0, 3, k, ido, 8)],
                input[InputIndex(0, 7, k, ido, 8)]);
            PairInPlace(ref a1, ref a3);
            a3 = RotateX90(a3, forward);
            a7 = RotateX90(a7, forward);
            PairInPlace(ref a5, ref a7);
            a5 = RotateX45(a5, forward);
            a7 = RotateX135(a7, forward);
            Pair(
                out Value a0,
                out Value a4,
                input[InputIndex(0, 0, k, ido, 8)],
                input[InputIndex(0, 4, k, ido, 8)]);
            Pair(
                out Value a2,
                out Value a6,
                input[InputIndex(0, 2, k, ido, 8)],
                input[InputIndex(0, 6, k, ido, 8)]);
            Pair(out Value output0, out Value output4, Add(a0, a2), a1);
            Pair(out Value output2, out Value output6, Subtract(a0, a2), a3);
            a6 = RotateX90(a6, forward);
            Pair(out Value output1, out Value output5, Add(a4, a6), a5);
            Pair(out Value output3, out Value output7, Subtract(a4, a6), a7);
            output[OutputIndex(0, k, 0, ido, l1)] = output0;
            output[OutputIndex(0, k, 1, ido, l1)] = output1;
            output[OutputIndex(0, k, 2, ido, l1)] = output2;
            output[OutputIndex(0, k, 3, ido, l1)] = output3;
            output[OutputIndex(0, k, 4, ido, l1)] = output4;
            output[OutputIndex(0, k, 5, ido, l1)] = output5;
            output[OutputIndex(0, k, 6, ido, l1)] = output6;
            output[OutputIndex(0, k, 7, ido, l1)] = output7;
        }

        private static int[] Factorize(int length)
        {
            var factors = new List<int>();
            int remaining = length;
            while ((remaining & 7) == 0)
            {
                factors.Add(8);
                remaining >>= 3;
            }

            while ((remaining & 3) == 0)
            {
                factors.Add(4);
                remaining >>= 2;
            }

            if ((remaining & 1) == 0)
            {
                remaining >>= 1;
                factors.Add(2);
                (factors[0], factors[^1]) = (factors[^1], factors[0]);
            }

            if (remaining != 1)
            {
                throw new ArgumentException("Only power-of-two complex FFT lengths are supported.", nameof(length));
            }

            return factors.ToArray();
        }

        private static Factor[] BuildFactors(int length, int[] radices)
        {
            return BuildFactors(
                length,
                radices,
                new SinCos2PiByN(length),
                rootFactor: 1);
        }

        private static Factor[] BuildFactors(
            int length,
            int[] radices,
            SinCos2PiByN twiddle,
            int rootFactor)
        {
            var factors = new Factor[radices.Length];
            int l1 = 1;
            for (int factorIndex = 0; factorIndex < factors.Length; factorIndex++)
            {
                int radix = radices[factorIndex];
                int ido = length / (l1 * radix);
                var values = new Value[(radix - 1) * (ido - 1)];
                for (int j = 1; j < radix; j++)
                {
                    for (int i = 1; i < ido; i++)
                    {
                        values[((j - 1) * (ido - 1)) + i - 1] = twiddle.Get(rootFactor * j * l1 * i);
                    }
                }

                factors[factorIndex] = new Factor(radix, values);
                l1 *= radix;
            }

            return factors;
        }

        private static Value Twiddle(Value[] values, int x, int i, int ido)
            => values[(i - 1) + (x * (ido - 1))];

        private static int InputIndex(int a, int b, int c, int ido, int radix)
            => a + (ido * (b + (radix * c)));

        private static int OutputIndex(int a, int b, int c, int ido, int l1)
            => a + (ido * (b + (l1 * c)));
    }

    private readonly record struct Factor(int Radix, Value[] Twiddles);

    private readonly record struct Value(double Real, double Imaginary);

    private static Value Add(Value left, Value right)
        => new(left.Real + right.Real, left.Imaginary + right.Imaginary);

    private static Value Subtract(Value left, Value right)
        => new(left.Real - right.Real, left.Imaginary - right.Imaginary);

    private static Value Scale(Value value, double scale)
        => new(value.Real * scale, value.Imaginary * scale);

    private static void Pair(out Value sum, out Value difference, Value left, Value right)
    {
        sum = Add(left, right);
        difference = Subtract(left, right);
    }

    private static void PairInPlace(ref Value left, ref Value right)
    {
        Value originalLeft = left;
        left = Add(left, right);
        right = Subtract(originalLeft, right);
    }

    private static Value SpecialMultiply(Value left, Value right, bool forward)
    {
        return forward
            ? new Value(
                (left.Real * right.Real) + (left.Imaginary * right.Imaginary),
                (left.Imaginary * right.Real) - (left.Real * right.Imaginary))
            : new Value(
                (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
                (left.Real * right.Imaginary) + (left.Imaginary * right.Real));
    }

    private static Value RotateX90(Value value, bool forward)
        => forward
            ? new Value(value.Imaginary, -value.Real)
            : new Value(-value.Imaginary, value.Real);

    private static Value RotateX45(Value value, bool forward)
    {
        const double HalfSqrt2 = 0.707106781186547524400844362104849;
        return forward
            ? new Value(
                HalfSqrt2 * (value.Real + value.Imaginary),
                HalfSqrt2 * (value.Imaginary - value.Real))
            : new Value(
                HalfSqrt2 * (value.Real - value.Imaginary),
                HalfSqrt2 * (value.Imaginary + value.Real));
    }

    private static Value RotateX135(Value value, bool forward)
    {
        const double HalfSqrt2 = 0.707106781186547524400844362104849;
        return forward
            ? new Value(
                HalfSqrt2 * (value.Imaginary - value.Real),
                HalfSqrt2 * (-value.Real - value.Imaginary))
            : new Value(
                HalfSqrt2 * (-value.Real - value.Imaginary),
                HalfSqrt2 * (value.Real - value.Imaginary));
    }

    private sealed class SinCos2PiByN
    {
        private readonly int _length;
        private readonly int _mask;
        private readonly int _shift;
        private readonly Value[] _first;
        private readonly Value[] _second;

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
            _first = new Value[_mask + 1];
            _first[0] = new Value(1.0, 0.0);
            for (int i = 1; i < _first.Length; i++)
            {
                _first[i] = Calculate(i, length, angle);
            }

            _second = new Value[(nValue + _mask) / (_mask + 1)];
            _second[0] = new Value(1.0, 0.0);
            for (int i = 1; i < _second.Length; i++)
            {
                _second[i] = Calculate(i * (_mask + 1), length, angle);
            }
        }

        public Value Get(int index)
        {
            if (2 * index <= _length)
            {
                return Multiply(
                    _first[index & _mask],
                    _second[index >> _shift],
                    conjugate: false);
            }

            index = _length - index;
            return Multiply(
                _first[index & _mask],
                _second[index >> _shift],
                conjugate: true);
        }

        private static Value Multiply(
            Value left,
            Value right,
            bool conjugate)
        {
            double real = (left.Real * right.Real) - (left.Imaginary * right.Imaginary);
            double imaginary = (left.Real * right.Imaginary) + (left.Imaginary * right.Real);
            return new Value(real, conjugate ? -imaginary : imaginary);
        }

        private static Value Calculate(int index, int length, double angle)
        {
            int x = index << 3;
            if (x < 4 * length)
            {
                if (x < 2 * length)
                {
                    if (x < length)
                    {
                        return new Value(Math.Cos(x * angle), Math.Sin(x * angle));
                    }

                    return new Value(
                        Math.Sin((2 * length - x) * angle),
                        Math.Cos((2 * length - x) * angle));
                }

                x -= 2 * length;
                if (x < length)
                {
                    return new Value(-Math.Sin(x * angle), Math.Cos(x * angle));
                }

                return new Value(
                    -Math.Cos((2 * length - x) * angle),
                    Math.Sin((2 * length - x) * angle));
            }

            x = 8 * length - x;
            if (x < 2 * length)
            {
                if (x < length)
                {
                    return new Value(Math.Cos(x * angle), -Math.Sin(x * angle));
                }

                return new Value(
                    Math.Sin((2 * length - x) * angle),
                    -Math.Cos((2 * length - x) * angle));
            }

            x -= 2 * length;
            if (x < length)
            {
                return new Value(-Math.Sin(x * angle), -Math.Cos(x * angle));
            }

            return new Value(
                -Math.Cos((2 * length - x) * angle),
                -Math.Sin((2 * length - x) * angle));
        }
    }
}
