// Float32 radix-2/4/8 complex FFT adapted from pocketfft's BSD-3-Clause implementation.
using System.Collections.Concurrent;

namespace VHSDecode.Core.Dsp;

internal static class PocketFftComplex32
{
    private static readonly ConcurrentDictionary<(int Length, int RootLength), Plan> RootedPlans = new();

    internal static Complex32[] ForwardDucc(ReadOnlySpan<Complex32> input)
    {
        ValidateLength(input.Length, nameof(input));
        return input.Length is > 300 and <= 100_000
            ? TransformDuccVectorized(input)
            : new Plan(input.Length, input.Length).Transform(input);
    }

    private static Complex32[] TransformWithRootLength(
        ReadOnlySpan<Complex32> input,
        int rootLength)
    {
        ValidateLength(input.Length, nameof(input));
        return RootedPlans.GetOrAdd(
                (input.Length, rootLength),
                static key => new Plan(key.Length, key.RootLength))
            .Transform(input);
    }

    private static Complex32[] TransformDuccVectorized(
        ReadOnlySpan<Complex32> input)
    {
        const int VectorLength = 4;
        int laneLength = input.Length / VectorLength;
        Complex32[] firstPass = Plan.TransformInitialRadix4(input);
        var lanes = new Complex32[VectorLength][];
        var laneInput = new Complex32[laneLength];
        for (int lane = 0; lane < VectorLength; lane++)
        {
            for (int i = 0; i < laneLength; i++)
            {
                laneInput[i] = firstPass[(lane * laneLength) + i];
            }

            lanes[lane] = TransformWithRootLength(laneInput, input.Length);
        }

        var output = new Complex32[input.Length];
        for (int i = 0; i < laneLength; i++)
        {
            for (int lane = 0; lane < VectorLength; lane++)
            {
                output[(i * VectorLength) + lane] = lanes[lane][i];
            }
        }

        return output;
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
        private readonly Factor[] _factors;
        private readonly int _length;

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

        internal Complex32[] Transform(ReadOnlySpan<Complex32> input)
        {
            var values = new Value[_length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = new Value(input[i].Real, input[i].Imaginary);
            }

            Execute(values);
            var output = new Complex32[_length];
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = new Complex32(values[i].Real, values[i].Imaginary);
            }

            return output;
        }

        internal static Complex32[] TransformInitialRadix4(
            ReadOnlySpan<Complex32> input)
        {
            var values = new Value[input.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = new Value(input[i].Real, input[i].Imaginary);
            }

            Factor factor = BuildFactors(
                input.Length,
                [4],
                new SinCos2PiByN(input.Length),
                rootFactor: 1)[0];
            var outputValues = new Value[input.Length];
            Pass4(
                input.Length / 4,
                l1: 1,
                values,
                outputValues,
                factor.Twiddles);
            var output = new Complex32[input.Length];
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = new Complex32(
                    outputValues[i].Real,
                    outputValues[i].Imaginary);
            }

            return output;
        }

        private void Execute(Value[] data)
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
                        Pass2(ido, l1, source, destination, factor.Twiddles);
                        break;
                    case 4:
                        Pass4(ido, l1, source, destination, factor.Twiddles);
                        break;
                    case 8:
                        Pass8(ido, l1, source, destination, factor.Twiddles);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported complex FFT radix {factor.Radix}.");
                }

                (source, destination) = (destination, source);
                l1 *= factor.Radix;
            }

            if (!ReferenceEquals(source, data))
            {
                source.CopyTo(data, 0);
            }
        }

        private static void Pass2(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles)
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
                        Twiddle(twiddles, 0, i, ido));
                }
            }
        }

        private static void Pass4(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                Pass4FirstIndex(ido, l1, input, output, k);
                for (int i = 1; i < ido; i++)
                {
                    Value c0 = input[InputIndex(i, 0, k, ido, 4)];
                    Value c1 = input[InputIndex(i, 1, k, ido, 4)];
                    Value c2 = input[InputIndex(i, 2, k, ido, 4)];
                    Value c3 = input[InputIndex(i, 3, k, ido, 4)];
                    Pair(out Value t2, out Value t1, c0, c2);
                    Pair(out Value t3, out Value t4, c1, c3);
                    t4 = RotateX90(t4);
                    output[OutputIndex(i, k, 0, ido, l1)] = Add(t2, t3);
                    output[OutputIndex(i, k, 1, ido, l1)] = SpecialMultiply(
                        Add(t1, t4),
                        Twiddle(twiddles, 0, i, ido));
                    output[OutputIndex(i, k, 2, ido, l1)] = SpecialMultiply(
                        Subtract(t2, t3),
                        Twiddle(twiddles, 1, i, ido));
                    output[OutputIndex(i, k, 3, ido, l1)] = SpecialMultiply(
                        Subtract(t1, t4),
                        Twiddle(twiddles, 2, i, ido));
                }
            }
        }

        private static void Pass4FirstIndex(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            int k)
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
            t4 = RotateX90(t4);
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
            Value[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                Pass8FirstIndex(ido, l1, input, output, k);
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
                    a7 = RotateX90(a7);
                    PairInPlace(ref a1, ref a3);
                    a3 = RotateX90(a3);
                    PairInPlace(ref a5, ref a7);
                    a5 = RotateX45(a5);
                    a7 = RotateX135(a7);
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
                        Twiddle(twiddles, 3, i, ido));
                    output[OutputIndex(i, k, 2, ido, l1)] = SpecialMultiply(
                        Add(a2, a3),
                        Twiddle(twiddles, 1, i, ido));
                    output[OutputIndex(i, k, 6, ido, l1)] = SpecialMultiply(
                        Subtract(a2, a3),
                        Twiddle(twiddles, 5, i, ido));
                    a6 = RotateX90(a6);
                    PairInPlace(ref a4, ref a6);
                    output[OutputIndex(i, k, 1, ido, l1)] = SpecialMultiply(
                        Add(a4, a5),
                        Twiddle(twiddles, 0, i, ido));
                    output[OutputIndex(i, k, 5, ido, l1)] = SpecialMultiply(
                        Subtract(a4, a5),
                        Twiddle(twiddles, 4, i, ido));
                    output[OutputIndex(i, k, 3, ido, l1)] = SpecialMultiply(
                        Add(a6, a7),
                        Twiddle(twiddles, 2, i, ido));
                    output[OutputIndex(i, k, 7, ido, l1)] = SpecialMultiply(
                        Subtract(a6, a7),
                        Twiddle(twiddles, 6, i, ido));
                }
            }
        }

        private static void Pass8FirstIndex(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            int k)
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
            a3 = RotateX90(a3);
            a7 = RotateX90(a7);
            PairInPlace(ref a5, ref a7);
            a5 = RotateX45(a5);
            a7 = RotateX135(a7);
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
            a6 = RotateX90(a6);
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

    private readonly record struct Value(float Real, float Imaginary);

    private static Value Add(Value left, Value right)
        => new(left.Real + right.Real, left.Imaginary + right.Imaginary);

    private static Value Subtract(Value left, Value right)
        => new(left.Real - right.Real, left.Imaginary - right.Imaginary);

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

    private static Value SpecialMultiply(Value left, Value right)
    {
        return new Value(
            (left.Real * right.Real) + (left.Imaginary * right.Imaginary),
            (left.Imaginary * right.Real) - (left.Real * right.Imaginary));
    }

    private static Value RotateX90(Value value)
        => new(value.Imaginary, -value.Real);

    private static Value RotateX45(Value value)
    {
        const float HalfSqrt2 = 0.707106781186547524400844362104849f;
        return new Value(
            HalfSqrt2 * (value.Real + value.Imaginary),
            HalfSqrt2 * (value.Imaginary - value.Real));
    }

    private static Value RotateX135(Value value)
    {
        const float HalfSqrt2 = 0.707106781186547524400844362104849f;
        return new Value(
            HalfSqrt2 * (value.Imaginary - value.Real),
            HalfSqrt2 * (-value.Real - value.Imaginary));
    }

    private sealed class SinCos2PiByN
    {
        private readonly DoubleValue[] _first;
        private readonly int _length;
        private readonly int _mask;
        private readonly DoubleValue[] _second;
        private readonly int _shift;

        internal SinCos2PiByN(int length)
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
            _first = new DoubleValue[_mask + 1];
            _first[0] = new DoubleValue(1.0, 0.0);
            for (int i = 1; i < _first.Length; i++)
            {
                _first[i] = Calculate(i, length, angle);
            }

            _second = new DoubleValue[(nValue + _mask) / (_mask + 1)];
            _second[0] = new DoubleValue(1.0, 0.0);
            for (int i = 1; i < _second.Length; i++)
            {
                _second[i] = Calculate(i * (_mask + 1), length, angle);
            }
        }

        internal Value Get(int index)
        {
            bool conjugate = 2 * index > _length;
            if (conjugate)
            {
                index = _length - index;
            }

            DoubleValue left = _first[index & _mask];
            DoubleValue right = _second[index >> _shift];
            double real = (left.Real * right.Real) - (left.Imaginary * right.Imaginary);
            double imaginary = (left.Real * right.Imaginary) + (left.Imaginary * right.Real);
            return new Value(
                (float)real,
                (float)(conjugate ? -imaginary : imaginary));
        }

        private static DoubleValue Calculate(int index, int length, double angle)
        {
            int x = index << 3;
            if (x < 4 * length)
            {
                if (x < 2 * length)
                {
                    if (x < length)
                    {
                        return new DoubleValue(Math.Cos(x * angle), Math.Sin(x * angle));
                    }

                    return new DoubleValue(
                        Math.Sin((2 * length - x) * angle),
                        Math.Cos((2 * length - x) * angle));
                }

                x -= 2 * length;
                if (x < length)
                {
                    return new DoubleValue(-Math.Sin(x * angle), Math.Cos(x * angle));
                }

                return new DoubleValue(
                    -Math.Cos((2 * length - x) * angle),
                    Math.Sin((2 * length - x) * angle));
            }

            x = 8 * length - x;
            if (x < 2 * length)
            {
                if (x < length)
                {
                    return new DoubleValue(Math.Cos(x * angle), -Math.Sin(x * angle));
                }

                return new DoubleValue(
                    Math.Sin((2 * length - x) * angle),
                    -Math.Cos((2 * length - x) * angle));
            }

            x -= 2 * length;
            if (x < length)
            {
                return new DoubleValue(-Math.Sin(x * angle), -Math.Cos(x * angle));
            }

            return new DoubleValue(
                -Math.Cos((2 * length - x) * angle),
                -Math.Sin((2 * length - x) * angle));
        }
    }

    private readonly record struct DoubleValue(double Real, double Imaginary);
}
