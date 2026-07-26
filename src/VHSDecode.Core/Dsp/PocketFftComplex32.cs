// Float32 mixed-radix complex FFT adapted from pocketfft/DUCC's BSD-3-Clause implementation.
using System.Collections.Concurrent;

namespace VHSDecode.Core.Dsp;

internal static class PocketFftComplex32
{
    private static readonly ConcurrentDictionary<(int Length, int RootLength), Plan> RootedPlans = new();

    internal static Complex32[] ForwardDucc(ReadOnlySpan<Complex32> input)
    {
        ValidateLength(input.Length, nameof(input));
        if (input.Length is > 300 and <= 100_000)
        {
            return TransformDuccVectorized(input);
        }

        return input.Length > 10_000
            ? TransformLargeMultipass(input, input.Length)
            : new Plan(input.Length, input.Length).Transform(input);
    }

    internal static Complex32[] ForwardAnyLengthDucc(
        ReadOnlySpan<Complex32> input)
    {
        ValidateSupportedLength(input.Length, nameof(input));
        if (input.Length is > 300 and <= 100_000
            && input.Length % 4 == 0)
        {
            return TransformDuccVectorized(input);
        }

        return TransformWithRootLength(input, input.Length);
    }

    internal static Complex32[] InverseAnyLengthDucc(
        ReadOnlySpan<Complex32> input)
    {
        Complex32[] transformed = BackwardAnyLengthDucc(input);
        float normalization = 1.0f / input.Length;
        for (int i = 0; i < transformed.Length; i++)
        {
            transformed[i] = new Complex32(
                transformed[i].Real * normalization,
                transformed[i].Imaginary * normalization);
        }

        return transformed;
    }

    internal static Complex32[] BackwardAnyLengthDucc(
        ReadOnlySpan<Complex32> input)
    {
        ValidateSupportedLength(input.Length, nameof(input));
        var conjugated = new Complex32[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            conjugated[i] = new Complex32(
                input[i].Real,
                -input[i].Imaginary);
        }

        Complex32[] transformed = ForwardAnyLengthDucc(conjugated);
        for (int i = 0; i < transformed.Length; i++)
        {
            float imaginary = transformed[i].Imaginary;
            transformed[i] = new Complex32(
                transformed[i].Real,
                imaginary == 0.0f ? 0.0f : -imaginary);
        }

        return transformed;
    }

    private static Complex32[] TransformWithRootLength(
        ReadOnlySpan<Complex32> input,
        int rootLength)
    {
        ValidateSupportedLength(input.Length, nameof(input));
        if (input.Length > 10_000)
        {
            return TransformLargeMultipass(input, rootLength);
        }

        return RootedPlans.GetOrAdd(
                (input.Length, rootLength),
                static key => new Plan(key.Length, key.RootLength))
            .Transform(input);
    }

    private static Complex32[] TransformLargeMultipass(
        ReadOnlySpan<Complex32> input,
        int rootLength)
    {
        int length = input.Length;
        int[] packets = BuildBalancedPackets(length);
        var roots = new SinCos2PiByN(rootLength);
        int rootFactor = rootLength / length;
        Complex32[] source = input.ToArray();
        var destination = new Complex32[length];
        int l1 = 1;

        foreach (int packetLength in packets)
        {
            int ido = length / (packetLength * l1);
            Plan packetPlan = RootedPlans.GetOrAdd(
                (packetLength, rootLength),
                static key => new Plan(key.Length, key.RootLength));
            var packet = new Complex32[packetLength];

            if (l1 == 1)
            {
                for (int i = 0; i < ido; i++)
                {
                    for (int m = 0; m < packetLength; m++)
                    {
                        packet[m] = source[i + (ido * m)];
                    }

                    Complex32[] transformed = packetPlan.Transform(packet);
                    for (int m = 0; m < packetLength; m++)
                    {
                        Complex32 value = transformed[m];
                        if (i != 0 && m != 0)
                        {
                            Value rotated = SpecialMultiply(
                                new Value(value.Real, value.Imaginary),
                                roots.Get(rootFactor * m * i));
                            value = new Complex32(
                                rotated.Real,
                                rotated.Imaginary);
                        }

                        source[i + (ido * m)] = value;
                    }
                }
            }
            else if (ido == 1)
            {
                for (int n = 0; n < l1; n++)
                {
                    int inputOffset = n * packetLength;
                    source.AsSpan(inputOffset, packetLength).CopyTo(packet);
                    Complex32[] transformed = packetPlan.Transform(packet);
                    for (int m = 0; m < packetLength; m++)
                    {
                        destination[n + (m * l1)] = transformed[m];
                    }
                }

                (source, destination) = (destination, source);
            }
            else
            {
                throw new InvalidOperationException(
                    "Unexpected DUCC large-transform packet layout.");
            }

            l1 *= packetLength;
        }

        return source;
    }

    private static int[] BuildBalancedPackets(int length)
    {
        var primeFactors = new List<int>();
        int remaining = length;
        while ((remaining & 1) == 0)
        {
            primeFactors.Add(2);
            remaining >>= 1;
        }

        for (int divisor = 3;
            divisor <= remaining / divisor;
            divisor += 2)
        {
            while (remaining % divisor == 0)
            {
                primeFactors.Add(divisor);
                remaining /= divisor;
            }
        }

        if (remaining > 1)
        {
            primeFactors.Add(remaining);
        }

        primeFactors.Sort(static (left, right) => right.CompareTo(left));
        int first = 1;
        int second = 1;
        foreach (int factor in primeFactors)
        {
            if (first > second)
            {
                second *= factor;
            }
            else
            {
                first *= factor;
            }
        }

        return [first, second];
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

    private static void ValidateSupportedLength(
        int length,
        string parameterName)
    {
        if (length < 2)
        {
            throw new ArgumentException(
                "Complex FFT length must be at least two.",
                parameterName);
        }

        int remaining = length;
        foreach (int radix in new[] { 2, 3, 5, 7, 11 })
        {
            while (remaining % radix == 0)
            {
                remaining /= radix;
            }
        }

        if (remaining != 1)
        {
            throw new ArgumentException(
                "Complex FFT length may contain only factors 2, 3, 5, 7, and 11.",
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
                    case 3:
                        Pass3(ido, l1, source, destination, factor.Twiddles);
                        break;
                    case 5:
                        Pass5(ido, l1, source, destination, factor.Twiddles);
                        break;
                    case 7:
                        Pass7(ido, l1, source, destination, factor.Twiddles);
                        break;
                    case 11:
                        Pass11(ido, l1, source, destination, factor.Twiddles);
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

        private static void Pass3(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles)
        {
            const float TwiddleReal = -0.5f;
            const float TwiddleImaginary =
                -0.8660254037844386467637231707529362f;
            for (int k = 0; k < l1; k++)
            {
                Pass3Index(
                    0,
                    k,
                    ido,
                    l1,
                    input,
                    output,
                    twiddles,
                    TwiddleReal,
                    TwiddleImaginary,
                    applyTwiddle: false);
                for (int i = 1; i < ido; i++)
                {
                    Pass3Index(
                        i,
                        k,
                        ido,
                        l1,
                        input,
                        output,
                        twiddles,
                        TwiddleReal,
                        TwiddleImaginary,
                        applyTwiddle: true);
                }
            }
        }

        private static void Pass3Index(
            int i,
            int k,
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles,
            float twiddleReal,
            float twiddleImaginary,
            bool applyTwiddle)
        {
            Value t0 = input[InputIndex(i, 0, k, ido, 3)];
            Pair(
                out Value t1,
                out Value t2,
                input[InputIndex(i, 1, k, ido, 3)],
                input[InputIndex(i, 2, k, ido, 3)]);
            output[OutputIndex(i, k, 0, ido, l1)] = Add(t0, t1);
            var ca = new Value(
                t0.Real + (t1.Real * twiddleReal),
                t0.Imaginary + (t1.Imaginary * twiddleReal));
            var cb = new Value(
                -t2.Imaginary * twiddleImaginary,
                t2.Real * twiddleImaginary);
            Value first = Add(ca, cb);
            Value second = Subtract(ca, cb);
            output[OutputIndex(i, k, 1, ido, l1)] = applyTwiddle
                ? SpecialMultiply(first, Twiddle(twiddles, 0, i, ido))
                : first;
            output[OutputIndex(i, k, 2, ido, l1)] = applyTwiddle
                ? SpecialMultiply(second, Twiddle(twiddles, 1, i, ido))
                : second;
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

        private static void Pass5(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                Pass5Index(
                    0,
                    k,
                    ido,
                    l1,
                    input,
                    output,
                    twiddles,
                    applyTwiddle: false);
                for (int i = 1; i < ido; i++)
                {
                    Pass5Index(
                        i,
                        k,
                        ido,
                        l1,
                        input,
                        output,
                        twiddles,
                        applyTwiddle: true);
                }
            }
        }

        private static void Pass5Index(
            int i,
            int k,
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles,
            bool applyTwiddle)
        {
            const float Twiddle1Real =
                0.3090169943749474241022934171828191f;
            const float Twiddle1Imaginary =
                -0.9510565162951535721164393333793821f;
            const float Twiddle2Real =
                -0.8090169943749474241022934171828191f;
            const float Twiddle2Imaginary =
                -0.5877852522924731291687059546390728f;

            Value t0 = input[InputIndex(i, 0, k, ido, 5)];
            Pair(
                out Value t1,
                out Value t4,
                input[InputIndex(i, 1, k, ido, 5)],
                input[InputIndex(i, 4, k, ido, 5)]);
            Pair(
                out Value t2,
                out Value t3,
                input[InputIndex(i, 2, k, ido, 5)],
                input[InputIndex(i, 3, k, ido, 5)]);
            output[OutputIndex(i, k, 0, ido, l1)] = new Value(
                (t0.Real + t1.Real) + t2.Real,
                (t0.Imaginary + t1.Imaginary) + t2.Imaginary);

            StorePass5Pair(
                i,
                k,
                ido,
                l1,
                output,
                twiddles,
                t0,
                t1,
                t2,
                t3,
                t4,
                1,
                4,
                Twiddle1Real,
                Twiddle2Real,
                Twiddle1Imaginary,
                Twiddle2Imaginary,
                applyTwiddle);
            StorePass5Pair(
                i,
                k,
                ido,
                l1,
                output,
                twiddles,
                t0,
                t1,
                t2,
                t3,
                t4,
                2,
                3,
                Twiddle2Real,
                Twiddle1Real,
                Twiddle2Imaginary,
                -Twiddle1Imaginary,
                applyTwiddle);
        }

        private static void StorePass5Pair(
            int i,
            int k,
            int ido,
            int l1,
            Value[] output,
            Value[] twiddles,
            Value t0,
            Value t1,
            Value t2,
            Value t3,
            Value t4,
            int firstOutput,
            int secondOutput,
            float firstRealCoefficient,
            float secondRealCoefficient,
            float firstImaginaryCoefficient,
            float secondImaginaryCoefficient,
            bool applyTwiddle)
        {
            var ca = new Value(
                (t0.Real + (firstRealCoefficient * t1.Real))
                    + (secondRealCoefficient * t2.Real),
                (t0.Imaginary + (firstRealCoefficient * t1.Imaginary))
                    + (secondRealCoefficient * t2.Imaginary));
            var cb = new Value(
                -((firstImaginaryCoefficient * t4.Imaginary)
                    + (secondImaginaryCoefficient * t3.Imaginary)),
                (firstImaginaryCoefficient * t4.Real)
                    + (secondImaginaryCoefficient * t3.Real));
            Value first = Add(ca, cb);
            Value second = Subtract(ca, cb);
            output[OutputIndex(i, k, firstOutput, ido, l1)] = applyTwiddle
                ? SpecialMultiply(
                    first,
                    Twiddle(twiddles, firstOutput - 1, i, ido))
                : first;
            output[OutputIndex(i, k, secondOutput, ido, l1)] = applyTwiddle
                ? SpecialMultiply(
                    second,
                    Twiddle(twiddles, secondOutput - 1, i, ido))
                : second;
        }

        private static void Pass7(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                Pass7Index(
                    0,
                    k,
                    ido,
                    l1,
                    input,
                    output,
                    twiddles,
                    applyTwiddle: false);
                for (int i = 1; i < ido; i++)
                {
                    Pass7Index(
                        i,
                        k,
                        ido,
                        l1,
                        input,
                        output,
                        twiddles,
                        applyTwiddle: true);
                }
            }
        }

        private static void Pass7Index(
            int i,
            int k,
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles,
            bool applyTwiddle)
        {
            const float Twiddle1Real =
                0.6234898018587335305250048840042398f;
            const float Twiddle1Imaginary =
                -0.7818314824680298087084445266740578f;
            const float Twiddle2Real =
                -0.2225209339563144042889025644967948f;
            const float Twiddle2Imaginary =
                -0.9749279121818236070181316829939312f;
            const float Twiddle3Real =
                -0.9009688679024191262361023195074451f;
            const float Twiddle3Imaginary =
                -0.433883739117558120475768332848359f;

            Value t1 = input[InputIndex(i, 0, k, ido, 7)];
            Pair(
                out Value t2,
                out Value t7,
                input[InputIndex(i, 1, k, ido, 7)],
                input[InputIndex(i, 6, k, ido, 7)]);
            Pair(
                out Value t3,
                out Value t6,
                input[InputIndex(i, 2, k, ido, 7)],
                input[InputIndex(i, 5, k, ido, 7)]);
            Pair(
                out Value t4,
                out Value t5,
                input[InputIndex(i, 3, k, ido, 7)],
                input[InputIndex(i, 4, k, ido, 7)]);
            output[OutputIndex(i, k, 0, ido, l1)] = new Value(
                ((t1.Real + t2.Real) + t3.Real) + t4.Real,
                ((t1.Imaginary + t2.Imaginary) + t3.Imaginary)
                    + t4.Imaginary);

            StorePass7Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7,
                1, 6,
                Twiddle1Real, Twiddle2Real, Twiddle3Real,
                Twiddle1Imaginary, Twiddle2Imaginary, Twiddle3Imaginary,
                applyTwiddle);
            StorePass7Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7,
                2, 5,
                Twiddle2Real, Twiddle3Real, Twiddle1Real,
                Twiddle2Imaginary, -Twiddle3Imaginary, -Twiddle1Imaginary,
                applyTwiddle);
            StorePass7Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7,
                3, 4,
                Twiddle3Real, Twiddle1Real, Twiddle2Real,
                Twiddle3Imaginary, -Twiddle1Imaginary, Twiddle2Imaginary,
                applyTwiddle);
        }

        private static void StorePass7Pair(
            int i,
            int k,
            int ido,
            int l1,
            Value[] output,
            Value[] twiddles,
            Value t1,
            Value t2,
            Value t3,
            Value t4,
            Value t5,
            Value t6,
            Value t7,
            int firstOutput,
            int secondOutput,
            float real1,
            float real2,
            float real3,
            float imaginary1,
            float imaginary2,
            float imaginary3,
            bool applyTwiddle)
        {
            var ca = new Value(
                ((t1.Real + (real1 * t2.Real)) + (real2 * t3.Real))
                    + (real3 * t4.Real),
                ((t1.Imaginary + (real1 * t2.Imaginary))
                    + (real2 * t3.Imaginary))
                    + (real3 * t4.Imaginary));
            var cb = new Value(
                -(((imaginary1 * t7.Imaginary)
                    + (imaginary2 * t6.Imaginary))
                    + (imaginary3 * t5.Imaginary)),
                ((imaginary1 * t7.Real)
                    + (imaginary2 * t6.Real))
                    + (imaginary3 * t5.Real));
            Value first = Add(ca, cb);
            Value second = Subtract(ca, cb);
            output[OutputIndex(i, k, firstOutput, ido, l1)] = applyTwiddle
                ? SpecialMultiply(
                    first,
                    Twiddle(twiddles, firstOutput - 1, i, ido))
                : first;
            output[OutputIndex(i, k, secondOutput, ido, l1)] = applyTwiddle
                ? SpecialMultiply(
                    second,
                    Twiddle(twiddles, secondOutput - 1, i, ido))
                : second;
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

        private static void Pass11(
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                Pass11Index(
                    0,
                    k,
                    ido,
                    l1,
                    input,
                    output,
                    twiddles,
                    applyTwiddle: false);
                for (int i = 1; i < ido; i++)
                {
                    Pass11Index(
                        i,
                        k,
                        ido,
                        l1,
                        input,
                        output,
                        twiddles,
                        applyTwiddle: true);
                }
            }
        }

        private static void Pass11Index(
            int i,
            int k,
            int ido,
            int l1,
            Value[] input,
            Value[] output,
            Value[] twiddles,
            bool applyTwiddle)
        {
            const float R1 = 0.8412535328311811688618116489193677f;
            const float I1 = -0.5406408174555975821076359543186917f;
            const float R2 = 0.4154150130018864255292741492296232f;
            const float I2 = -0.9096319953545183714117153830790285f;
            const float R3 = -0.1423148382732851404437926686163697f;
            const float I3 = -0.9898214418809327323760920377767188f;
            const float R4 = -0.6548607339452850640569250724662936f;
            const float I4 = -0.7557495743542582837740358439723444f;
            const float R5 = -0.9594929736144973898903680570663277f;
            const float I5 = -0.2817325568414296977114179153466169f;

            Value t1 = input[InputIndex(i, 0, k, ido, 11)];
            Pair(
                out Value t2,
                out Value t11,
                input[InputIndex(i, 1, k, ido, 11)],
                input[InputIndex(i, 10, k, ido, 11)]);
            Pair(
                out Value t3,
                out Value t10,
                input[InputIndex(i, 2, k, ido, 11)],
                input[InputIndex(i, 9, k, ido, 11)]);
            Pair(
                out Value t4,
                out Value t9,
                input[InputIndex(i, 3, k, ido, 11)],
                input[InputIndex(i, 8, k, ido, 11)]);
            Pair(
                out Value t5,
                out Value t8,
                input[InputIndex(i, 4, k, ido, 11)],
                input[InputIndex(i, 7, k, ido, 11)]);
            Pair(
                out Value t6,
                out Value t7,
                input[InputIndex(i, 5, k, ido, 11)],
                input[InputIndex(i, 6, k, ido, 11)]);
            output[OutputIndex(i, k, 0, ido, l1)] = new Value(
                ((((t1.Real + t2.Real) + t3.Real) + t4.Real) + t5.Real)
                    + t6.Real,
                ((((t1.Imaginary + t2.Imaginary) + t3.Imaginary)
                    + t4.Imaginary) + t5.Imaginary) + t6.Imaginary);

            StorePass11Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11,
                1, 10,
                R1, R2, R3, R4, R5,
                I1, I2, I3, I4, I5,
                applyTwiddle);
            StorePass11Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11,
                2, 9,
                R2, R4, R5, R3, R1,
                I2, I4, -I5, -I3, -I1,
                applyTwiddle);
            StorePass11Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11,
                3, 8,
                R3, R5, R2, R1, R4,
                I3, -I5, -I2, I1, I4,
                applyTwiddle);
            StorePass11Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11,
                4, 7,
                R4, R3, R1, R5, R2,
                I4, -I3, I1, I5, -I2,
                applyTwiddle);
            StorePass11Pair(
                i, k, ido, l1, output, twiddles,
                t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11,
                5, 6,
                R5, R1, R4, R2, R3,
                I5, -I1, I4, -I2, I3,
                applyTwiddle);
        }

        private static void StorePass11Pair(
            int i,
            int k,
            int ido,
            int l1,
            Value[] output,
            Value[] twiddles,
            Value t1,
            Value t2,
            Value t3,
            Value t4,
            Value t5,
            Value t6,
            Value t7,
            Value t8,
            Value t9,
            Value t10,
            Value t11,
            int firstOutput,
            int secondOutput,
            float real1,
            float real2,
            float real3,
            float real4,
            float real5,
            float imaginary1,
            float imaginary2,
            float imaginary3,
            float imaginary4,
            float imaginary5,
            bool applyTwiddle)
        {
            var ca = new Value(
                ((((t1.Real + (t2.Real * real1)) + (t3.Real * real2))
                    + (t4.Real * real3)) + (t5.Real * real4))
                    + (t6.Real * real5),
                ((((t1.Imaginary + (t2.Imaginary * real1))
                    + (t3.Imaginary * real2))
                    + (t4.Imaginary * real3))
                    + (t5.Imaginary * real4))
                    + (t6.Imaginary * real5));
            var cb = new Value(
                -(((((imaginary1 * t11.Imaginary)
                    + (imaginary2 * t10.Imaginary))
                    + (imaginary3 * t9.Imaginary))
                    + (imaginary4 * t8.Imaginary))
                    + (imaginary5 * t7.Imaginary)),
                ((((imaginary1 * t11.Real) + (imaginary2 * t10.Real))
                    + (imaginary3 * t9.Real))
                    + (imaginary4 * t8.Real))
                    + (imaginary5 * t7.Real));
            Value first = Add(ca, cb);
            Value second = Subtract(ca, cb);
            output[OutputIndex(i, k, firstOutput, ido, l1)] = applyTwiddle
                ? SpecialMultiply(
                    first,
                    Twiddle(twiddles, firstOutput - 1, i, ido))
                : first;
            output[OutputIndex(i, k, secondOutput, ido, l1)] = applyTwiddle
                ? SpecialMultiply(
                    second,
                    Twiddle(twiddles, secondOutput - 1, i, ido))
                : second;
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

            for (int divisor = 3;
                divisor <= remaining / divisor;
                divisor += 2)
            {
                while (remaining % divisor == 0)
                {
                    if (divisor > 11)
                    {
                        throw new ArgumentException(
                            "Only complex FFT radices up to 11 are supported.",
                            nameof(length));
                    }

                    factors.Add(divisor);
                    remaining /= divisor;
                }
            }

            if (remaining > 1)
            {
                if (remaining > 11)
                {
                    throw new ArgumentException(
                        "Only complex FFT radices up to 11 are supported.",
                        nameof(length));
                }

                factors.Add(remaining);
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
