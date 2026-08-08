// Radix-2/4/8 complex FFT adapted from pocketfft's BSD-3-Clause implementation.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.Dsp;

public static class PocketFftComplex
{
    private const byte SwapComplexComponents = 0b0101;
    private const byte SelectImaginaryComponents = 0b1010;
    private const double Pass8HalfSqrt2 = 0.707106781186547524400844362104849;
    private static readonly Vector256<double> NegateAllComponents = Vector256.Create(-0.0);
    private static readonly Vector256<double> NegateEvenComponents =
        Vector256.Create(-0.0, 0.0, -0.0, 0.0);
    private static readonly Vector256<double> NegateOddComponents =
        Vector256.Create(0.0, -0.0, 0.0, -0.0);
    private static readonly Vector256<double> Pass8HalfSqrt2Vector =
        Vector256.Create(Pass8HalfSqrt2);
    private static readonly SingleCreationCache<int, Plan> Plans = new();
    private static readonly SingleCreationCache<(int Length, int RootLength), Plan> RootedPlans = new();
    private static readonly SingleCreationCache<int, SinCos2PiByN> Roots = new();
    [ThreadStatic]
    private static Complex[]? _packetStage;
    [ThreadStatic]
    private static Complex[]? _packetFirst;
    [ThreadStatic]
    private static Complex[]? _packetSecond;
    [ThreadStatic]
    private static Complex[]? _realInput;
    [ThreadStatic]
    private static Value[]? _planValues;
    [ThreadStatic]
    private static Value[]? _planScratch;

    public static Complex[] Forward(ReadOnlySpan<Complex> input)
        => Transform(input, forward: true);

    private static void TransformWithRootLength(
        ReadOnlySpan<Complex> input,
        int rootLength,
        bool forward,
        Span<Complex> output)
    {
        ValidateLength(input.Length, nameof(input));
        RootedPlans.GetOrAdd(
                (input.Length, rootLength),
                static key => new Plan(key.Length, key.RootLength))
            .Transform(input, output, forward);
    }

    private static Complex[] TransformDuccPacketized(ReadOnlySpan<Complex> input, bool forward)
    {
        var output = new Complex[input.Length];
        TransformDuccPacketized(input, forward, output);
        return output;
    }

    private static void TransformDuccPacketized(
        ReadOnlySpan<Complex> input,
        bool forward,
        Span<Complex> output)
    {
        ValidateLength(input.Length, nameof(input));
        if (input.Length <= 10_000)
        {
            throw new ArgumentException(
                "DUCC packetization is only used for transforms longer than 10000 samples.",
                nameof(input));
        }

        if (output.Length != input.Length)
        {
            throw new ArgumentException("FFT output length must match the input length.", nameof(output));
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
        SinCos2PiByN roots = Roots.GetOrAdd(length, static value => new SinCos2PiByN(value));
        Complex[] stage = EnsureCapacity(ref _packetStage, length);
        Complex[] firstPacketBuffer = EnsureCapacity(ref _packetFirst, firstPacketLength);
        Span<Complex> firstPacket = firstPacketBuffer.AsSpan(0, firstPacketLength);
        for (int i = 0; i < secondPacketLength; i++)
        {
            for (int m = 0; m < firstPacketLength; m++)
            {
                firstPacket[m] = input[i + (secondPacketLength * m)];
            }

            TransformWithRootLength(firstPacket, length, forward, firstPacket);
            for (int m = 0; m < firstPacketLength; m++)
            {
                Complex value = firstPacket[m];
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

        Complex[] secondPacketBuffer = EnsureCapacity(ref _packetSecond, secondPacketLength);
        Span<Complex> secondPacket = secondPacketBuffer.AsSpan(0, secondPacketLength);
        for (int k = 0; k < firstPacketLength; k++)
        {
            int offset = secondPacketLength * k;
            stage.AsSpan(offset, secondPacketLength).CopyTo(secondPacket);
            TransformWithRootLength(secondPacket, length, forward, secondPacket);
            for (int m = 0; m < secondPacketLength; m++)
            {
                output[k + (firstPacketLength * m)] = secondPacket[m];
            }
        }

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

    internal static void InverseDuccInPlace(Complex[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateLength(values.Length, nameof(values));
        if (values.Length > 10_000)
        {
            TransformDuccPacketized(values, forward: false, values);
        }
        else
        {
            Plans.GetOrAdd(values.Length, static length => new Plan(length))
                .Transform(values, values, forward: false);
        }
    }

    internal static Complex[] ForwardDuccReal(ReadOnlySpan<double> input)
    {
        ValidateLength(input.Length, nameof(input));
        int length = input.Length;
        int complexLength = length / 2;
        Complex[] complexInput = EnsureCapacity(ref _realInput, complexLength);
        for (int i = 0; i < complexLength; i++)
        {
            complexInput[i] = new Complex(input[2 * i], input[(2 * i) + 1]);
        }

        Complex[] transformed = complexLength > 10_000
            ? TransformDuccPacketized(complexInput.AsSpan(0, complexLength), forward: true)
            : Forward(complexInput.AsSpan(0, complexLength));
        SinCos2PiByN roots = Roots.GetOrAdd(length, static value => new SinCos2PiByN(value));
        var output = new Complex[complexLength + 1];
        output[0] = new Complex(transformed[0].Real + transformed[0].Imaginary, 0.0);
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
            output[i] = new Complex(
                0.5 * (even.Real + rotated.Real),
                0.5 * (even.Imaginary + rotated.Imaginary));
            output[xi] = new Complex(
                0.5 * (even.Real - rotated.Real),
                0.5 * (rotated.Imaginary - even.Imaginary));
        }

        output[^1] = new Complex(transformed[0].Real - transformed[0].Imaginary, 0.0);
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

    internal static void ForwardDuccRealFull(
        ReadOnlySpan<double> input,
        Span<Complex> output,
        Span<Complex> transformScratch)
    {
        ValidateLength(input.Length, nameof(input));
        if (output.Length != input.Length)
        {
            throw new ArgumentException(
                "Full-spectrum output length must match the real input length.",
                nameof(output));
        }

        int length = input.Length;
        int complexLength = length / 2;
        ValidateLength(complexLength, nameof(input));
        if (transformScratch.Length < complexLength)
        {
            throw new ArgumentException(
                "Real FFT scratch must hold the packed complex transform.",
                nameof(transformScratch));
        }

        if (output.Overlaps(transformScratch[..complexLength]))
        {
            throw new ArgumentException(
                "Full-spectrum output and real FFT scratch must not overlap.",
                nameof(transformScratch));
        }

        Complex[] complexInput = EnsureCapacity(ref _realInput, complexLength);
        for (int i = 0; i < complexLength; i++)
        {
            complexInput[i] = new Complex(input[2 * i], input[(2 * i) + 1]);
        }

        Span<Complex> transformed = transformScratch[..complexLength];
        if (complexLength > 10_000)
        {
            TransformDuccPacketized(
                complexInput.AsSpan(0, complexLength),
                forward: true,
                transformed);
        }
        else
        {
            Plans.GetOrAdd(complexLength, static value => new Plan(value))
                .Transform(complexInput.AsSpan(0, complexLength), transformed, forward: true);
        }

        SinCos2PiByN roots = Roots.GetOrAdd(length, static value => new SinCos2PiByN(value));
        output[0] = new Complex(transformed[0].Real + transformed[0].Imaginary, 0.0);
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
            output[i] = new Complex(
                0.5 * (even.Real + rotated.Real),
                0.5 * (even.Imaginary + rotated.Imaginary));
            output[xi] = new Complex(
                0.5 * (even.Real - rotated.Real),
                0.5 * (rotated.Imaginary - even.Imaginary));
        }

        output[complexLength] = new Complex(
            transformed[0].Real - transformed[0].Imaginary,
            0.0);
        for (int i = 1; i < complexLength; i++)
        {
            output[^i] = Complex.Conjugate(output[i]);
        }

        // scipy.fft.fft(real) preserves negative imaginary zero at both real-only bins.
        output[0] = new Complex(output[0].Real, -0.0);
        output[complexLength] = new Complex(output[complexLength].Real, -0.0);
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
        SinCos2PiByN roots = Roots.GetOrAdd(outputLength, static value => new SinCos2PiByN(value));
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

    internal static void ForwardReal(
        ReadOnlySpan<double> input,
        Span<Complex> output)
    {
        ValidateLength(input.Length, nameof(input));
        if (output.Length != input.Length)
        {
            throw new ArgumentException(
                "FFT output length must match the input length.",
                nameof(output));
        }

        for (int i = 0; i < output.Length; i++)
        {
            output[i] = new Complex(input[i], 0.0);
        }

        Plans.GetOrAdd(input.Length, static length => new Plan(length))
            .Transform(output, output, forward: true);
    }

    public static Complex[] Inverse(ReadOnlySpan<Complex> input)
        => Transform(input, forward: false);

    internal static void Inverse(
        ReadOnlySpan<Complex> input,
        Span<Complex> output)
    {
        ValidateLength(input.Length, nameof(input));
        if (output.Length != input.Length)
        {
            throw new ArgumentException(
                "FFT output length must match the input length.",
                nameof(output));
        }

        Plans.GetOrAdd(input.Length, static length => new Plan(length))
            .Transform(input, output, forward: false);
    }

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

    private static T[] EnsureCapacity<T>(ref T[]? buffer, int length)
    {
        if (buffer is null || buffer.Length < length)
        {
            buffer = new T[length];
        }

        return buffer;
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
            var output = new Complex[_length];
            Transform(input, output, forward);
            return output;
        }

        public void Transform(ReadOnlySpan<Complex> input, Span<Complex> output, bool forward)
        {
            if (input.Length != _length)
            {
                throw new ArgumentException("FFT input length does not match the plan length.", nameof(input));
            }

            if (output.Length != _length)
            {
                throw new ArgumentException("FFT output length does not match the plan length.", nameof(output));
            }

            Value[] values = EnsureCapacity(ref _planValues, _length);
            Value[] scratch = EnsureCapacity(ref _planScratch, _length);
            for (int i = 0; i < _length; i++)
            {
                values[i] = new Value(input[i].Real, input[i].Imaginary);
            }

            Value[] transformed = Execute(
                values,
                scratch,
                forward,
                forward ? 1.0 : 1.0 / _length);
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = new Complex(transformed[i].Real, transformed[i].Imaginary);
            }
        }

        private Value[] Execute(
            Value[] data,
            Value[] scratch,
            bool forward,
            double normalization)
        {
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

            if (normalization != 1.0)
            {
                for (int i = 0; i < _length; i++)
                {
                    source[i] = Scale(source[i], normalization);
                }
            }

            return source;
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
            if (forward)
            {
                Pass8Core<ForwardPass8Direction>(
                    ido,
                    l1,
                    input,
                    output,
                    twiddles);
            }
            else
            {
                Pass8Core<BackwardPass8Direction>(
                    ido,
                    l1,
                    input,
                    output,
                    twiddles);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Pass8Core<TDirection>(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles)
            where TDirection : struct, IPass8Direction
        {
            int outputStride = ido * l1;
            // Plan construction guarantees every unchecked radix-8 access is in range.
            System.Diagnostics.Debug.Assert((long)ido * l1 * 8 <= input.Length);
            System.Diagnostics.Debug.Assert((long)ido * l1 * 8 <= output.Length);
            System.Diagnostics.Debug.Assert((long)(ido - 1) * 7 <= twiddles.Length);
            System.Diagnostics.Debug.Assert(Unsafe.SizeOf<Value>() == 2 * sizeof(double));
            ref Value inputStart = ref MemoryMarshal.GetArrayDataReference(input);
            ref Value outputStart = ref MemoryMarshal.GetArrayDataReference(output);
            ref Value twiddleStart = ref MemoryMarshal.GetArrayDataReference(twiddles);
            for (int k = 0; k < l1; k++)
            {
                int inputBase = ido * 8 * k;
                int outputBase = ido * k;
                Pass8FirstIndex<TDirection>(
                    input,
                    output,
                    inputBase,
                    outputBase,
                    ido,
                    outputStride);
                int i = 1;
                if (Avx.IsSupported)
                {
                    int twiddleStride = ido - 1;
                    for (; i + 1 < ido; i += 2)
                    {
                        int inputIndex = inputBase + i;
                        Pair(
                            out Vector256<double> a1,
                            out Vector256<double> a5,
                            LoadTwoValues(ref inputStart, inputIndex + ido),
                            LoadTwoValues(ref inputStart, inputIndex + (5 * ido)));
                        Pair(
                            out Vector256<double> a3,
                            out Vector256<double> a7,
                            LoadTwoValues(ref inputStart, inputIndex + (3 * ido)),
                            LoadTwoValues(ref inputStart, inputIndex + (7 * ido)));
                        a7 = TDirection.RotateX90(a7);
                        PairInPlace(ref a1, ref a3);
                        a3 = TDirection.RotateX90(a3);
                        PairInPlace(ref a5, ref a7);
                        a5 = TDirection.RotateX45(a5);
                        a7 = TDirection.RotateX135(a7);
                        Pair(
                            out Vector256<double> a0,
                            out Vector256<double> a4,
                            LoadTwoValues(ref inputStart, inputIndex),
                            LoadTwoValues(ref inputStart, inputIndex + (4 * ido)));
                        Pair(
                            out Vector256<double> a2,
                            out Vector256<double> a6,
                            LoadTwoValues(ref inputStart, inputIndex + (2 * ido)),
                            LoadTwoValues(ref inputStart, inputIndex + (6 * ido)));
                        PairInPlace(ref a0, ref a2);

                        int outputIndex = outputBase + i;
                        int twiddleIndex = i - 1;
                        StoreTwoValues(ref outputStart, outputIndex, Avx.Add(a0, a1));
                        StoreTwoValues(
                            ref outputStart,
                            outputIndex + (4 * outputStride),
                            TDirection.SpecialMultiply(
                                Avx.Subtract(a0, a1),
                                LoadTwoValues(
                                    ref twiddleStart,
                                    twiddleIndex + (3 * twiddleStride))));
                        StoreTwoValues(
                            ref outputStart,
                            outputIndex + (2 * outputStride),
                            TDirection.SpecialMultiply(
                                Avx.Add(a2, a3),
                                LoadTwoValues(
                                    ref twiddleStart,
                                    twiddleIndex + twiddleStride)));
                        StoreTwoValues(
                            ref outputStart,
                            outputIndex + (6 * outputStride),
                            TDirection.SpecialMultiply(
                                Avx.Subtract(a2, a3),
                                LoadTwoValues(
                                    ref twiddleStart,
                                    twiddleIndex + (5 * twiddleStride))));
                        a6 = TDirection.RotateX90(a6);
                        PairInPlace(ref a4, ref a6);
                        StoreTwoValues(
                            ref outputStart,
                            outputIndex + outputStride,
                            TDirection.SpecialMultiply(
                                Avx.Add(a4, a5),
                                LoadTwoValues(ref twiddleStart, twiddleIndex)));
                        StoreTwoValues(
                            ref outputStart,
                            outputIndex + (5 * outputStride),
                            TDirection.SpecialMultiply(
                                Avx.Subtract(a4, a5),
                                LoadTwoValues(
                                    ref twiddleStart,
                                    twiddleIndex + (4 * twiddleStride))));
                        StoreTwoValues(
                            ref outputStart,
                            outputIndex + (3 * outputStride),
                            TDirection.SpecialMultiply(
                                Avx.Add(a6, a7),
                                LoadTwoValues(
                                    ref twiddleStart,
                                    twiddleIndex + (2 * twiddleStride))));
                        StoreTwoValues(
                            ref outputStart,
                            outputIndex + (7 * outputStride),
                            TDirection.SpecialMultiply(
                                Avx.Subtract(a6, a7),
                                LoadTwoValues(
                                    ref twiddleStart,
                                    twiddleIndex + (6 * twiddleStride))));
                    }
                }

                for (; i < ido; i++)
                {
                    int inputIndex = inputBase + i;
                    Pair(
                        out Value a1,
                        out Value a5,
                        Unsafe.Add(ref inputStart, inputIndex + ido),
                        Unsafe.Add(ref inputStart, inputIndex + (5 * ido)));
                    Pair(
                        out Value a3,
                        out Value a7,
                        Unsafe.Add(ref inputStart, inputIndex + (3 * ido)),
                        Unsafe.Add(ref inputStart, inputIndex + (7 * ido)));
                    a7 = TDirection.RotateX90(a7);
                    PairInPlace(ref a1, ref a3);
                    a3 = TDirection.RotateX90(a3);
                    PairInPlace(ref a5, ref a7);
                    a5 = TDirection.RotateX45(a5);
                    a7 = TDirection.RotateX135(a7);
                    Pair(
                        out Value a0,
                        out Value a4,
                        Unsafe.Add(ref inputStart, inputIndex),
                        Unsafe.Add(ref inputStart, inputIndex + (4 * ido)));
                    Pair(
                        out Value a2,
                        out Value a6,
                        Unsafe.Add(ref inputStart, inputIndex + (2 * ido)),
                        Unsafe.Add(ref inputStart, inputIndex + (6 * ido)));
                    PairInPlace(ref a0, ref a2);
                    int outputIndex = outputBase + i;
                    Unsafe.Add(ref outputStart, outputIndex) = Add(a0, a1);
                    Unsafe.Add(ref outputStart, outputIndex + (4 * outputStride)) = TDirection.SpecialMultiply(
                        Subtract(a0, a1),
                        Unsafe.Add(ref twiddleStart, TwiddleIndex(3, i, ido)));
                    Unsafe.Add(ref outputStart, outputIndex + (2 * outputStride)) = TDirection.SpecialMultiply(
                        Add(a2, a3),
                        Unsafe.Add(ref twiddleStart, TwiddleIndex(1, i, ido)));
                    Unsafe.Add(ref outputStart, outputIndex + (6 * outputStride)) = TDirection.SpecialMultiply(
                        Subtract(a2, a3),
                        Unsafe.Add(ref twiddleStart, TwiddleIndex(5, i, ido)));
                    a6 = TDirection.RotateX90(a6);
                    PairInPlace(ref a4, ref a6);
                    Unsafe.Add(ref outputStart, outputIndex + outputStride) = TDirection.SpecialMultiply(
                        Add(a4, a5),
                        Unsafe.Add(ref twiddleStart, TwiddleIndex(0, i, ido)));
                    Unsafe.Add(ref outputStart, outputIndex + (5 * outputStride)) = TDirection.SpecialMultiply(
                        Subtract(a4, a5),
                        Unsafe.Add(ref twiddleStart, TwiddleIndex(4, i, ido)));
                    Unsafe.Add(ref outputStart, outputIndex + (3 * outputStride)) = TDirection.SpecialMultiply(
                        Add(a6, a7),
                        Unsafe.Add(ref twiddleStart, TwiddleIndex(2, i, ido)));
                    Unsafe.Add(ref outputStart, outputIndex + (7 * outputStride)) = TDirection.SpecialMultiply(
                        Subtract(a6, a7),
                        Unsafe.Add(ref twiddleStart, TwiddleIndex(6, i, ido)));
                }
            }
        }

        private static void Pass8FirstIndex<TDirection>(
            Value[] input,
            Value[] output,
            int inputBase,
            int outputBase,
            int inputStride,
            int outputStride)
            where TDirection : struct, IPass8Direction
        {
            int input1 = inputBase + inputStride;
            int input2 = input1 + inputStride;
            int input3 = input2 + inputStride;
            int input4 = input3 + inputStride;
            int input5 = input4 + inputStride;
            int input6 = input5 + inputStride;
            int input7 = input6 + inputStride;
            Pair(
                out Value a1,
                out Value a5,
                input[input1],
                input[input5]);
            Pair(
                out Value a3,
                out Value a7,
                input[input3],
                input[input7]);
            PairInPlace(ref a1, ref a3);
            a3 = TDirection.RotateX90(a3);
            a7 = TDirection.RotateX90(a7);
            PairInPlace(ref a5, ref a7);
            a5 = TDirection.RotateX45(a5);
            a7 = TDirection.RotateX135(a7);
            Pair(
                out Value a0,
                out Value a4,
                input[inputBase],
                input[input4]);
            Pair(
                out Value a2,
                out Value a6,
                input[input2],
                input[input6]);
            Pair(out Value output0, out Value output4, Add(a0, a2), a1);
            Pair(out Value output2, out Value output6, Subtract(a0, a2), a3);
            a6 = TDirection.RotateX90(a6);
            Pair(out Value output1, out Value output5, Add(a4, a6), a5);
            Pair(out Value output3, out Value output7, Subtract(a4, a6), a7);
            output[outputBase] = output0;
            outputBase += outputStride;
            output[outputBase] = output1;
            outputBase += outputStride;
            output[outputBase] = output2;
            outputBase += outputStride;
            output[outputBase] = output3;
            outputBase += outputStride;
            output[outputBase] = output4;
            outputBase += outputStride;
            output[outputBase] = output5;
            outputBase += outputStride;
            output[outputBase] = output6;
            outputBase += outputStride;
            output[outputBase] = output7;
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
            => values[TwiddleIndex(x, i, ido)];

        private static int TwiddleIndex(int x, int i, int ido)
            => (i - 1) + (x * (ido - 1));

        private static int InputIndex(int a, int b, int c, int ido, int radix)
            => a + (ido * (b + (radix * c)));

        private static int OutputIndex(int a, int b, int c, int ido, int l1)
            => a + (ido * (b + (l1 * c)));
    }

    private readonly record struct Factor(int Radix, Value[] Twiddles);

    private readonly record struct Value(double Real, double Imaginary);

    private interface IPass8Direction
    {
        static abstract Value SpecialMultiply(Value left, Value right);

        static abstract Vector256<double> SpecialMultiply(
            Vector256<double> left,
            Vector256<double> right);

        static abstract Value RotateX90(Value value);

        static abstract Vector256<double> RotateX90(Vector256<double> value);

        static abstract Value RotateX45(Value value);

        static abstract Vector256<double> RotateX45(Vector256<double> value);

        static abstract Value RotateX135(Value value);

        static abstract Vector256<double> RotateX135(Vector256<double> value);
    }

    private readonly struct ForwardPass8Direction : IPass8Direction
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value SpecialMultiply(Value left, Value right)
            => new(
                (left.Real * right.Real) + (left.Imaginary * right.Imaginary),
                (left.Imaginary * right.Real) - (left.Real * right.Imaginary));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> SpecialMultiply(
            Vector256<double> left,
            Vector256<double> right)
        {
            Vector256<double> products = Avx.Multiply(left, right);
            Vector256<double> real = Avx.Add(
                products,
                Avx.Permute(products, SwapComplexComponents));
            Vector256<double> crossProducts = Avx.Multiply(
                left,
                Avx.Permute(right, SwapComplexComponents));
            Vector256<double> imaginary = Avx.Subtract(
                crossProducts,
                Avx.Permute(crossProducts, SwapComplexComponents));
            return Avx.Blend(real, imaginary, SelectImaginaryComponents);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value RotateX90(Value value)
            => new(value.Imaginary, -value.Real);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> RotateX90(Vector256<double> value)
            => Avx.Xor(
                Avx.Permute(value, SwapComplexComponents),
                NegateOddComponents);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value RotateX45(Value value)
        {
            return new Value(
                Pass8HalfSqrt2 * (value.Real + value.Imaginary),
                Pass8HalfSqrt2 * (value.Imaginary - value.Real));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> RotateX45(Vector256<double> value)
        {
            Vector256<double> swapped = Avx.Permute(value, SwapComplexComponents);
            return Avx.Multiply(
                Avx.Blend(
                    Avx.Add(value, swapped),
                    Avx.Subtract(value, swapped),
                    SelectImaginaryComponents),
                Pass8HalfSqrt2Vector);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value RotateX135(Value value)
        {
            return new Value(
                Pass8HalfSqrt2 * (value.Imaginary - value.Real),
                Pass8HalfSqrt2 * (-value.Real - value.Imaginary));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> RotateX135(Vector256<double> value)
        {
            Vector256<double> swapped = Avx.Permute(value, SwapComplexComponents);
            Vector256<double> difference = Avx.Subtract(swapped, value);
            Vector256<double> negativeSum = Avx.Subtract(
                Avx.Xor(swapped, NegateAllComponents),
                value);
            return Avx.Multiply(
                Avx.Blend(difference, negativeSum, SelectImaginaryComponents),
                Pass8HalfSqrt2Vector);
        }
    }

    private readonly struct BackwardPass8Direction : IPass8Direction
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value SpecialMultiply(Value left, Value right)
            => new(
                (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
                (left.Real * right.Imaginary) + (left.Imaginary * right.Real));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> SpecialMultiply(
            Vector256<double> left,
            Vector256<double> right)
        {
            Vector256<double> products = Avx.Multiply(left, right);
            Vector256<double> real = Avx.Subtract(
                products,
                Avx.Permute(products, SwapComplexComponents));
            Vector256<double> crossProducts = Avx.Multiply(
                left,
                Avx.Permute(right, SwapComplexComponents));
            Vector256<double> crossSum = Avx.Add(
                crossProducts,
                Avx.Permute(crossProducts, SwapComplexComponents));
            Vector256<double> imaginary = Avx.Permute(
                crossSum,
                SwapComplexComponents);
            return Avx.Blend(real, imaginary, SelectImaginaryComponents);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value RotateX90(Value value)
            => new(-value.Imaginary, value.Real);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> RotateX90(Vector256<double> value)
            => Avx.Xor(
                Avx.Permute(value, SwapComplexComponents),
                NegateEvenComponents);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value RotateX45(Value value)
        {
            return new Value(
                Pass8HalfSqrt2 * (value.Real - value.Imaginary),
                Pass8HalfSqrt2 * (value.Imaginary + value.Real));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> RotateX45(Vector256<double> value)
        {
            Vector256<double> swapped = Avx.Permute(value, SwapComplexComponents);
            return Avx.Multiply(
                Avx.Blend(
                    Avx.Subtract(value, swapped),
                    Avx.Add(value, swapped),
                    SelectImaginaryComponents),
                Pass8HalfSqrt2Vector);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Value RotateX135(Value value)
        {
            return new Value(
                Pass8HalfSqrt2 * (-value.Real - value.Imaginary),
                Pass8HalfSqrt2 * (value.Real - value.Imaginary));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> RotateX135(Vector256<double> value)
        {
            Vector256<double> swapped = Avx.Permute(value, SwapComplexComponents);
            Vector256<double> negativeSum = Avx.Subtract(
                Avx.Xor(value, NegateAllComponents),
                swapped);
            Vector256<double> difference = Avx.Subtract(swapped, value);
            return Avx.Multiply(
                Avx.Blend(negativeSum, difference, SelectImaginaryComponents),
                Pass8HalfSqrt2Vector);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Add(Value left, Value right)
        => new(left.Real + right.Real, left.Imaginary + right.Imaginary);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Subtract(Value left, Value right)
        => new(left.Real - right.Real, left.Imaginary - right.Imaginary);

    private static Value Scale(Value value, double scale)
        => new(value.Real * scale, value.Imaginary * scale);

    private static void Pair(out Value sum, out Value difference, Value left, Value right)
    {
        sum = Add(left, right);
        difference = Subtract(left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pair(
        out Vector256<double> sum,
        out Vector256<double> difference,
        Vector256<double> left,
        Vector256<double> right)
    {
        sum = Avx.Add(left, right);
        difference = Avx.Subtract(left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PairInPlace(ref Value left, ref Value right)
    {
        Value originalLeft = left;
        left = Add(left, right);
        right = Subtract(originalLeft, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PairInPlace(
        ref Vector256<double> left,
        ref Vector256<double> right)
    {
        Vector256<double> originalLeft = left;
        left = Avx.Add(left, right);
        right = Avx.Subtract(originalLeft, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> LoadTwoValues(ref Value start, int index)
    {
        ref byte address = ref Unsafe.As<Value, byte>(ref Unsafe.Add(ref start, index));
        return Unsafe.ReadUnaligned<Vector256<double>>(ref address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreTwoValues(
        ref Value start,
        int index,
        Vector256<double> value)
    {
        ref byte address = ref Unsafe.As<Value, byte>(ref Unsafe.Add(ref start, index));
        Unsafe.WriteUnaligned(ref address, value);
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
