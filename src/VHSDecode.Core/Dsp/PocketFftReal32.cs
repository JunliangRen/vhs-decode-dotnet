// Float32 mixed-radix real FFT adapted from pocketfft/DUCC's BSD-3-Clause implementation.
using System.Buffers;

namespace VHSDecode.Core.Dsp;

internal static class PocketFftReal32
{
    private static readonly SingleCreationCache<int, Plan> Plans = new();

    internal static Complex32[] Forward(ReadOnlySpan<float> input)
    {
        ValidateLength(input.Length, nameof(input));

        return Plans.GetOrAdd(
            input.Length,
            static length => new Plan(length)).Forward(input);
    }

    internal static Complex32[] ForwardDucc(ReadOnlySpan<float> input)
    {
        ValidateLength(input.Length, nameof(input));
        return Plans.GetOrAdd(
            input.Length,
            static length => new Plan(length)).ForwardDucc(input);
    }

    internal static float[] Inverse(
        ReadOnlySpan<Complex32> input,
        int outputLength)
    {
        ValidateLength(outputLength, nameof(outputLength));

        if (input.Length != (outputLength / 2) + 1)
        {
            throw new ArgumentException(
                "Half-spectrum length does not match the requested real output length.",
                nameof(input));
        }

        return Plans.GetOrAdd(
            outputLength,
            static length => new Plan(length)).Inverse(input);
    }

    internal static Complex32[] ForwardAnyLength(
        ReadOnlySpan<float> input,
        int workerThreads = 1)
    {
        ValidateSupportedEvenLength(input.Length, nameof(input));
        return Plans.GetOrAdd(
            input.Length,
            static length => new Plan(length)).ForwardDucc(
                input,
                workerThreads);
    }

    internal static void ForwardAnyLength(
        ReadOnlySpan<float> input,
        Complex32[] complexInput,
        Complex32[] transformScratch,
        Span<Complex32> output,
        int workerThreads = 1)
    {
        ArgumentNullException.ThrowIfNull(complexInput);
        ArgumentNullException.ThrowIfNull(transformScratch);
        ValidateSupportedEvenLength(input.Length, nameof(input));
        int complexLength = input.Length / 2;
        ValidateWorkspaceLength(
            complexInput.Length,
            complexLength,
            nameof(complexInput));
        ValidateWorkspaceLength(
            transformScratch.Length,
            complexLength,
            nameof(transformScratch));
        ValidateWorkspaceLength(
            output.Length,
            complexLength + 1,
            nameof(output));
        Plans.GetOrAdd(
                input.Length,
                static length => new Plan(length))
            .ForwardDucc(
                input,
                complexInput,
                transformScratch,
                output,
                workerThreads);
    }

    internal static float[] InverseAnyLength(
        ReadOnlySpan<Complex32> input,
        int outputLength,
        int workerThreads = 1)
    {
        ValidateSupportedEvenLength(outputLength, nameof(outputLength));
        if (input.Length != (outputLength / 2) + 1)
        {
            throw new ArgumentException(
                "Half-spectrum length does not match the requested real output length.",
                nameof(input));
        }

        return Plans.GetOrAdd(
            outputLength,
            static length => new Plan(length)).InverseDucc(
                input,
                workerThreads);
    }

    internal static void InverseAnyLength(
        ReadOnlySpan<Complex32> input,
        int outputLength,
        Complex32[] complexInput,
        Complex32[] transformScratch,
        Span<float> output,
        int workerThreads = 1)
    {
        ArgumentNullException.ThrowIfNull(complexInput);
        ArgumentNullException.ThrowIfNull(transformScratch);
        ValidateSupportedEvenLength(outputLength, nameof(outputLength));
        int complexLength = outputLength / 2;
        ValidateWorkspaceLength(
            input.Length,
            complexLength + 1,
            nameof(input));
        ValidateWorkspaceLength(
            complexInput.Length,
            complexLength,
            nameof(complexInput));
        ValidateWorkspaceLength(
            transformScratch.Length,
            complexLength,
            nameof(transformScratch));
        ValidateWorkspaceLength(
            output.Length,
            outputLength,
            nameof(output));
        Plans.GetOrAdd(
                outputLength,
                static length => new Plan(length))
            .InverseDucc(
                input,
                complexInput,
                transformScratch,
                output,
                workerThreads);
    }

    private static void ValidateWorkspaceLength(
        int actual,
        int expected,
        string parameterName)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                "FFT workspace length does not match the transform length.",
                parameterName);
        }
    }

    private static void ValidateSupportedEvenLength(
        int length,
        string parameterName)
    {
        if (length < 4 || (length & 1) != 0)
        {
            throw new ArgumentException(
                "Real FFT length must be an even number of at least four.",
                parameterName);
        }

        int remaining = length / 2;
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
                "Half of the real FFT length may contain only factors 2, 3, 5, 7, and 11.",
                parameterName);
        }
    }

    private static void ValidateLength(int length, string parameterName)
    {
        if (length < 2 || (length & (length - 1)) != 0)
        {
            throw new ArgumentException(
                "Real FFT length must be a power of two of at least two.",
                parameterName);
        }
    }

    private sealed class Plan
    {
        private readonly Factor[] _factors;
        private readonly int _length;
        private readonly UnityRoots _roots;

        internal Plan(int length)
        {
            _length = length;
            _roots = new UnityRoots(length);
            int[] radices = Factorize(length);
            _factors = BuildFactors(length, radices, _roots);
        }

        internal Complex32[] Forward(ReadOnlySpan<float> input)
        {
            float[] packed = ArrayPool<float>.Shared.Rent(_length);
            try
            {
                input.CopyTo(packed);
                ExecuteForward(packed);
                var output = new Complex32[(_length / 2) + 1];
                output[0] = new Complex32(packed[0], 0.0f);
                for (int i = 1; i < output.Length - 1; i++)
                {
                    output[i] = new Complex32(
                        packed[(2 * i) - 1],
                        packed[2 * i]);
                }

                output[^1] = new Complex32(packed[_length - 1], 0.0f);
                return output;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(packed);
            }
        }

        internal Complex32[] ForwardDucc(
            ReadOnlySpan<float> input,
            int workerThreads = 1)
            => _length > 1000
                ? ForwardComplexified(input, workerThreads)
                : Forward(input);

        internal void ForwardDucc(
            ReadOnlySpan<float> input,
            Complex32[] complexInput,
            Complex32[] transformScratch,
            Span<Complex32> output,
            int workerThreads)
        {
            if (_length > 1000)
            {
                ForwardComplexified(
                    input,
                    complexInput,
                    transformScratch,
                    output,
                    workerThreads);
                return;
            }

            Forward(input).CopyTo(output);
        }

        private Complex32[] ForwardComplexified(
            ReadOnlySpan<float> input,
            int workerThreads)
        {
            int complexLength = _length / 2;
            var complexInput = new Complex32[complexLength];
            for (int i = 0; i < complexInput.Length; i++)
            {
                complexInput[i] = new Complex32(
                    input[2 * i],
                    input[(2 * i) + 1]);
            }

            Complex32[] transformed =
                (_length & (_length - 1)) == 0
                    ? PocketFftComplex32.ForwardDuccOwned(
                        complexInput,
                        workerThreads)
                    : PocketFftComplex32.ForwardAnyLengthDuccOwned(
                        complexInput,
                        workerThreads);
            var output = new Complex32[complexLength + 1];
            output[0] = new Complex32(
                transformed[0].Real + transformed[0].Imaginary,
                0.0f);

            for (int i = 1, inverseIndex = complexLength - 1;
                i <= inverseIndex;
                i++, inverseIndex--)
            {
                Complex32 current = transformed[i];
                Complex32 inverse = transformed[inverseIndex];
                float evenReal = current.Real + inverse.Real;
                float evenImaginary = current.Imaginary - inverse.Imaginary;
                float oddReal = current.Imaginary + inverse.Imaginary;
                float oddImaginary = inverse.Real - current.Real;
                FloatTwiddle root = _roots.Get(i);
                MultiplyConjugate(
                    root.Real,
                    root.Imaginary,
                    oddReal,
                    oddImaginary,
                    out float rotatedReal,
                    out float rotatedImaginary);
                output[i] = new Complex32(
                    0.5f * (evenReal + rotatedReal),
                    0.5f * (evenImaginary + rotatedImaginary));
                output[inverseIndex] = new Complex32(
                    0.5f * (evenReal - rotatedReal),
                    0.5f * (rotatedImaginary - evenImaginary));
            }

            output[^1] = new Complex32(
                transformed[0].Real - transformed[0].Imaginary,
                0.0f);
            return output;
        }

        private void ForwardComplexified(
            ReadOnlySpan<float> input,
            Complex32[] complexInput,
            Complex32[] transformScratch,
            Span<Complex32> output,
            int workerThreads)
        {
            int complexLength = _length / 2;
            for (int i = 0; i < complexLength; i++)
            {
                complexInput[i] = new Complex32(
                    input[2 * i],
                    input[(2 * i) + 1]);
            }

            Complex32[] transformed =
                (_length & (_length - 1)) == 0
                    ? PocketFftComplex32.ForwardDuccOwned(
                        complexInput,
                        transformScratch,
                        workerThreads)
                    : PocketFftComplex32.ForwardAnyLengthDuccOwned(
                        complexInput,
                        transformScratch,
                        workerThreads);
            output[0] = new Complex32(
                transformed[0].Real + transformed[0].Imaginary,
                0.0f);

            for (int i = 1, inverseIndex = complexLength - 1;
                i <= inverseIndex;
                i++, inverseIndex--)
            {
                Complex32 current = transformed[i];
                Complex32 inverse = transformed[inverseIndex];
                float evenReal = current.Real + inverse.Real;
                float evenImaginary = current.Imaginary - inverse.Imaginary;
                float oddReal = current.Imaginary + inverse.Imaginary;
                float oddImaginary = inverse.Real - current.Real;
                FloatTwiddle root = _roots.Get(i);
                MultiplyConjugate(
                    root.Real,
                    root.Imaginary,
                    oddReal,
                    oddImaginary,
                    out float rotatedReal,
                    out float rotatedImaginary);
                output[i] = new Complex32(
                    0.5f * (evenReal + rotatedReal),
                    0.5f * (evenImaginary + rotatedImaginary));
                output[inverseIndex] = new Complex32(
                    0.5f * (evenReal - rotatedReal),
                    0.5f * (rotatedImaginary - evenImaginary));
            }

            output[^1] = new Complex32(
                transformed[0].Real - transformed[0].Imaginary,
                0.0f);
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

        internal float[] InverseDucc(
            ReadOnlySpan<Complex32> input,
            int workerThreads = 1)
            => _length > 1000
                ? InverseComplexified(input, workerThreads)
                : Inverse(input);

        internal void InverseDucc(
            ReadOnlySpan<Complex32> input,
            Complex32[] complexInput,
            Complex32[] transformScratch,
            Span<float> output,
            int workerThreads)
        {
            if (_length > 1000)
            {
                InverseComplexified(
                    input,
                    complexInput,
                    transformScratch,
                    output,
                    workerThreads);
                return;
            }

            Inverse(input).CopyTo(output);
        }

        private float[] InverseComplexified(
            ReadOnlySpan<Complex32> input,
            int workerThreads)
        {
            int complexLength = _length / 2;
            var complexInput = new Complex32[complexLength];
            complexInput[0] = new Complex32(
                input[0].Real + input[^1].Real,
                input[0].Real - input[^1].Real);

            for (int i = 1, inverseIndex = complexLength - 1;
                i <= inverseIndex;
                i++, inverseIndex--)
            {
                Complex32 first = input[i];
                Complex32 second = new(
                    input[inverseIndex].Real,
                    -input[inverseIndex].Imaginary);
                float evenReal = first.Real + second.Real;
                float evenImaginary =
                    first.Imaginary + second.Imaginary;
                float oddReal = first.Real - second.Real;
                float oddImaginary =
                    first.Imaginary - second.Imaginary;
                FloatTwiddle root = _roots.Get(i);
                float rotatedReal =
                    (oddReal * root.Real)
                    - (oddImaginary * root.Imaginary);
                float rotatedImaginary =
                    (oddReal * root.Imaginary)
                    + (oddImaginary * root.Real);
                complexInput[i] = new Complex32(
                    evenReal - rotatedImaginary,
                    evenImaginary + rotatedReal);
                complexInput[inverseIndex] = new Complex32(
                    evenReal + rotatedImaginary,
                    -evenImaginary + rotatedReal);
            }

            Complex32[] transformed =
                PocketFftComplex32.BackwardAnyLengthDuccOwned(
                    complexInput,
                    workerThreads);
            float normalization = 1.0f / _length;
            var output = new float[_length];
            for (int i = 0; i < transformed.Length; i++)
            {
                output[2 * i] =
                    transformed[i].Real * normalization;
                output[(2 * i) + 1] =
                    transformed[i].Imaginary * normalization;
            }

            return output;
        }

        private void InverseComplexified(
            ReadOnlySpan<Complex32> input,
            Complex32[] complexInput,
            Complex32[] transformScratch,
            Span<float> output,
            int workerThreads)
        {
            int complexLength = _length / 2;
            complexInput[0] = new Complex32(
                input[0].Real + input[^1].Real,
                input[0].Real - input[^1].Real);

            for (int i = 1, inverseIndex = complexLength - 1;
                i <= inverseIndex;
                i++, inverseIndex--)
            {
                Complex32 first = input[i];
                Complex32 second = new(
                    input[inverseIndex].Real,
                    -input[inverseIndex].Imaginary);
                float evenReal = first.Real + second.Real;
                float evenImaginary =
                    first.Imaginary + second.Imaginary;
                float oddReal = first.Real - second.Real;
                float oddImaginary =
                    first.Imaginary - second.Imaginary;
                FloatTwiddle root = _roots.Get(i);
                float rotatedReal =
                    (oddReal * root.Real)
                    - (oddImaginary * root.Imaginary);
                float rotatedImaginary =
                    (oddReal * root.Imaginary)
                    + (oddImaginary * root.Real);
                complexInput[i] = new Complex32(
                    evenReal - rotatedImaginary,
                    evenImaginary + rotatedReal);
                complexInput[inverseIndex] = new Complex32(
                    evenReal + rotatedImaginary,
                    -evenImaginary + rotatedReal);
            }

            Complex32[] transformed =
                PocketFftComplex32.BackwardAnyLengthDuccOwned(
                    complexInput,
                    transformScratch,
                    workerThreads);
            float normalization = 1.0f / _length;
            for (int i = 0; i < transformed.Length; i++)
            {
                output[2 * i] =
                    transformed[i].Real * normalization;
                output[(2 * i) + 1] =
                    transformed[i].Imaginary * normalization;
            }
        }

        private void ExecuteForward(float[] data)
        {
            float[] scratch = ArrayPool<float>.Shared.Rent(_length);
            try
            {
                float[] source = data;
                float[] destination = scratch;
                int l1 = _length;
                for (int pass = 0; pass < _factors.Length; pass++)
                {
                    Factor factor = _factors[_factors.Length - pass - 1];
                    int ido = _length / l1;
                    l1 /= factor.Radix;
                    bool generic = false;
                    if (factor.Radix == 4)
                    {
                        Radix4Forward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else if (factor.Radix == 2)
                    {
                        Radix2Forward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else if (factor.Radix == 3)
                    {
                        Radix3Forward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else if (factor.Radix == 5)
                    {
                        Radix5Forward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else
                    {
                        RadixGenericForward(
                            ido,
                            factor.Radix,
                            l1,
                            source,
                            destination,
                            factor.Twiddles,
                            factor.GenericTwiddles);
                        generic = true;
                    }

                    if (!generic)
                    {
                        (source, destination) = (destination, source);
                    }
                }

                if (!ReferenceEquals(source, data))
                {
                    source.AsSpan(0, _length).CopyTo(data);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        private void ExecuteBackward(float[] data, float normalization)
        {
            float[] scratch = ArrayPool<float>.Shared.Rent(_length);
            try
            {
                float[] source = data;
                float[] destination = scratch;
                int l1 = 1;
                foreach (Factor factor in _factors)
                {
                    int ido = _length / (factor.Radix * l1);
                    if (factor.Radix == 4)
                    {
                        Radix4Backward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else if (factor.Radix == 2)
                    {
                        Radix2Backward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else if (factor.Radix == 3)
                    {
                        Radix3Backward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else if (factor.Radix == 5)
                    {
                        Radix5Backward(
                            ido,
                            l1,
                            source,
                            destination,
                            factor.Twiddles);
                    }
                    else
                    {
                        RadixGenericBackward(
                            ido,
                            factor.Radix,
                            l1,
                            source,
                            destination,
                            factor.Twiddles,
                            factor.GenericTwiddles);
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
                ArrayPool<float>.Shared.Return(scratch);
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

            for (int divisor = 3;
                divisor <= remaining / divisor;
                divisor += 2)
            {
                while (remaining % divisor == 0)
                {
                    if (divisor > 11)
                    {
                        throw new ArgumentException(
                            "Only real FFT radices up to 11 are supported.",
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
                        "Only real FFT radices up to 11 are supported.",
                        nameof(length));
                }

                factors.Add(remaining);
            }

            return factors.ToArray();
        }

        private static Factor[] BuildFactors(
            int length,
            int[] radices,
            UnityRoots roots)
        {
            var factors = new Factor[radices.Length];
            int l1 = 1;
            for (int factorIndex = 0;
                factorIndex < factors.Length;
                factorIndex++)
            {
                int radix = radices[factorIndex];
                int ido = length / (radix * l1);
                float[] twiddles = factorIndex == radices.Length - 1
                    ? []
                    : new float[(radix - 1) * (ido - 1)];
                for (int j = 1; j < radix; j++)
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

                float[] genericTwiddles = radix > 5
                    ? new float[2 * radix]
                    : [];
                if (radix > 5)
                {
                    genericTwiddles[0] = 1.0f;
                    genericTwiddles[1] = 0.0f;
                    for (int i = 2, inverse = (2 * radix) - 2;
                        i <= inverse;
                        i += 2, inverse -= 2)
                    {
                        FloatTwiddle value = roots.Get(
                            (i / 2) * (length / radix));
                        genericTwiddles[i] = value.Real;
                        genericTwiddles[i + 1] = value.Imaginary;
                        genericTwiddles[inverse] = value.Real;
                        genericTwiddles[inverse + 1] =
                            -value.Imaginary;
                    }
                }

                factors[factorIndex] = new Factor(
                    radix,
                    twiddles,
                    genericTwiddles);
                l1 *= radix;
            }

            return factors;
        }

        private static void Radix2Forward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                float left = input[ForwardInput(0, k, 0, ido, l1)];
                float right = input[ForwardInput(0, k, 1, ido, l1)];
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
                        out float tr2,
                        out float ti2);
                    float real = input[ForwardInput(i - 1, k, 0, ido, l1)];
                    output[ForwardOutput(i - 1, 0, k, ido, 2)] = real + tr2;
                    output[ForwardOutput(ic - 1, 1, k, ido, 2)] = real - tr2;
                    float imaginary = input[ForwardInput(i, k, 0, ido, l1)];
                    output[ForwardOutput(i, 0, k, ido, 2)] = ti2 + imaginary;
                    output[ForwardOutput(ic, 1, k, ido, 2)] = ti2 - imaginary;
                }
            }
        }

        private static void Radix3Forward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            const float TauReal = -0.5f;
            const float TauImaginary =
                0.8660254037844386467637231707529362f;
            for (int k = 0; k < l1; k++)
            {
                float cr2 =
                    input[ForwardInput(0, k, 1, ido, l1)]
                    + input[ForwardInput(0, k, 2, ido, l1)];
                output[ForwardOutput(0, 0, k, ido, 3)] =
                    input[ForwardInput(0, k, 0, ido, l1)] + cr2;
                output[ForwardOutput(0, 2, k, ido, 3)] =
                    TauImaginary
                    * (input[ForwardInput(0, k, 2, ido, l1)]
                        - input[ForwardInput(0, k, 1, ido, l1)]);
                output[ForwardOutput(ido - 1, 1, k, ido, 3)] =
                    input[ForwardInput(0, k, 0, ido, l1)]
                    + (TauReal * cr2);
            }

            if (ido == 1)
            {
                return;
            }

            int stride = ido - 1;
            for (int k = 0; k < l1; k++)
            {
                for (int i = 2; i < ido; i += 2)
                {
                    int inverse = ido - i;
                    MultiplyConjugate(
                        twiddles[i - 2],
                        twiddles[i - 1],
                        input[ForwardInput(i - 1, k, 1, ido, l1)],
                        input[ForwardInput(i, k, 1, ido, l1)],
                        out float dr2,
                        out float di2);
                    MultiplyConjugate(
                        twiddles[stride + i - 2],
                        twiddles[stride + i - 1],
                        input[ForwardInput(i - 1, k, 2, ido, l1)],
                        input[ForwardInput(i, k, 2, ido, l1)],
                        out float dr3,
                        out float di3);
                    Rearrange(
                        ref dr2,
                        ref di2,
                        ref dr3,
                        ref di3);
                    float c0Real =
                        input[ForwardInput(i - 1, k, 0, ido, l1)];
                    float c0Imaginary =
                        input[ForwardInput(i, k, 0, ido, l1)];
                    output[ForwardOutput(i - 1, 0, k, ido, 3)] =
                        c0Real + dr2;
                    output[ForwardOutput(i, 0, k, ido, 3)] =
                        c0Imaginary + di2;
                    float tr2 = c0Real + (TauReal * dr2);
                    float ti2 = c0Imaginary + (TauReal * di2);
                    float tr3 = TauImaginary * dr3;
                    float ti3 = TauImaginary * di3;
                    output[ForwardOutput(i - 1, 2, k, ido, 3)] =
                        tr2 + tr3;
                    output[ForwardOutput(inverse - 1, 1, k, ido, 3)] =
                        tr2 - tr3;
                    output[ForwardOutput(i, 2, k, ido, 3)] =
                        ti3 + ti2;
                    output[ForwardOutput(inverse, 1, k, ido, 3)] =
                        ti3 - ti2;
                }
            }
        }

        private static void Radix5Forward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            const float Tr11 =
                0.3090169943749474241022934171828191f;
            const float Ti11 =
                0.9510565162951535721164393333793821f;
            const float Tr12 =
                -0.8090169943749474241022934171828191f;
            const float Ti12 =
                0.5877852522924731291687059546390728f;
            for (int k = 0; k < l1; k++)
            {
                Pair(
                    out float cr2,
                    out float ci5,
                    input[ForwardInput(0, k, 4, ido, l1)],
                    input[ForwardInput(0, k, 1, ido, l1)]);
                Pair(
                    out float cr3,
                    out float ci4,
                    input[ForwardInput(0, k, 3, ido, l1)],
                    input[ForwardInput(0, k, 2, ido, l1)]);
                float c0 = input[ForwardInput(0, k, 0, ido, l1)];
                output[ForwardOutput(0, 0, k, ido, 5)] =
                    (c0 + cr2) + cr3;
                output[ForwardOutput(ido - 1, 1, k, ido, 5)] =
                    (c0 + (Tr11 * cr2)) + (Tr12 * cr3);
                output[ForwardOutput(0, 2, k, ido, 5)] =
                    (Ti11 * ci5) + (Ti12 * ci4);
                output[ForwardOutput(ido - 1, 3, k, ido, 5)] =
                    (c0 + (Tr12 * cr2)) + (Tr11 * cr3);
                output[ForwardOutput(0, 4, k, ido, 5)] =
                    (Ti12 * ci5) - (Ti11 * ci4);
            }

            if (ido == 1)
            {
                return;
            }

            int stride = ido - 1;
            for (int k = 0; k < l1; k++)
            {
                for (int i = 2, inverse = ido - 2;
                    i < ido;
                    i += 2, inverse -= 2)
                {
                    MultiplyConjugate(
                        twiddles[i - 2],
                        twiddles[i - 1],
                        input[ForwardInput(i - 1, k, 1, ido, l1)],
                        input[ForwardInput(i, k, 1, ido, l1)],
                        out float dr2,
                        out float di2);
                    MultiplyConjugate(
                        twiddles[stride + i - 2],
                        twiddles[stride + i - 1],
                        input[ForwardInput(i - 1, k, 2, ido, l1)],
                        input[ForwardInput(i, k, 2, ido, l1)],
                        out float dr3,
                        out float di3);
                    MultiplyConjugate(
                        twiddles[(2 * stride) + i - 2],
                        twiddles[(2 * stride) + i - 1],
                        input[ForwardInput(i - 1, k, 3, ido, l1)],
                        input[ForwardInput(i, k, 3, ido, l1)],
                        out float dr4,
                        out float di4);
                    MultiplyConjugate(
                        twiddles[(3 * stride) + i - 2],
                        twiddles[(3 * stride) + i - 1],
                        input[ForwardInput(i - 1, k, 4, ido, l1)],
                        input[ForwardInput(i, k, 4, ido, l1)],
                        out float dr5,
                        out float di5);
                    Rearrange(
                        ref dr2,
                        ref di2,
                        ref dr5,
                        ref di5);
                    Rearrange(
                        ref dr3,
                        ref di3,
                        ref dr4,
                        ref di4);
                    float c0Real =
                        input[ForwardInput(i - 1, k, 0, ido, l1)];
                    float c0Imaginary =
                        input[ForwardInput(i, k, 0, ido, l1)];
                    output[ForwardOutput(i - 1, 0, k, ido, 5)] =
                        (c0Real + dr2) + dr3;
                    output[ForwardOutput(i, 0, k, ido, 5)] =
                        (c0Imaginary + di2) + di3;
                    float tr2 =
                        (c0Real + (Tr11 * dr2)) + (Tr12 * dr3);
                    float ti2 =
                        (c0Imaginary + (Tr11 * di2)) + (Tr12 * di3);
                    float tr3 =
                        (c0Real + (Tr12 * dr2)) + (Tr11 * dr3);
                    float ti3 =
                        (c0Imaginary + (Tr12 * di2)) + (Tr11 * di3);
                    float tr5 = (Ti11 * dr5) + (Ti12 * dr4);
                    float ti5 = (Ti11 * di5) + (Ti12 * di4);
                    float tr4 = (Ti12 * dr5) - (Ti11 * dr4);
                    float ti4 = (Ti12 * di5) - (Ti11 * di4);
                    output[ForwardOutput(i - 1, 2, k, ido, 5)] =
                        tr2 + tr5;
                    output[ForwardOutput(inverse - 1, 1, k, ido, 5)] =
                        tr2 - tr5;
                    output[ForwardOutput(i, 2, k, ido, 5)] =
                        ti5 + ti2;
                    output[ForwardOutput(inverse, 1, k, ido, 5)] =
                        ti5 - ti2;
                    output[ForwardOutput(i - 1, 4, k, ido, 5)] =
                        tr3 + tr4;
                    output[ForwardOutput(inverse - 1, 3, k, ido, 5)] =
                        tr3 - tr4;
                    output[ForwardOutput(i, 4, k, ido, 5)] =
                        ti4 + ti3;
                    output[ForwardOutput(inverse, 3, k, ido, 5)] =
                        ti4 - ti3;
                }
            }
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

        private static void RadixGenericForward(
            int ido,
            int radix,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles,
            float[] genericTwiddles)
        {
            int halfRadix = (radix + 1) / 2;
            int flattenedLength = ido * l1;

            if (ido > 1)
            {
                for (int j = 1, inverseJ = radix - 1;
                    j < halfRadix;
                    j++, inverseJ--)
                {
                    int twiddleOffset = (j - 1) * (ido - 1);
                    int inverseTwiddleOffset =
                        (inverseJ - 1) * (ido - 1);
                    for (int k = 0; k < l1; k++)
                    {
                        int currentTwiddle = twiddleOffset;
                        int inverseTwiddle = inverseTwiddleOffset;
                        for (int i = 1; i <= ido - 2; i += 2)
                        {
                            int firstRealIndex =
                                GenericC1(i, k, j, ido, l1);
                            int firstImaginaryIndex =
                                GenericC1(i + 1, k, j, ido, l1);
                            int inverseRealIndex =
                                GenericC1(i, k, inverseJ, ido, l1);
                            int inverseImaginaryIndex =
                                GenericC1(
                                    i + 1,
                                    k,
                                    inverseJ,
                                    ido,
                                    l1);
                            float t1 = input[firstRealIndex];
                            float t2 = input[firstImaginaryIndex];
                            float t3 = input[inverseRealIndex];
                            float t4 = input[inverseImaginaryIndex];
                            float x1 =
                                (twiddles[currentTwiddle] * t1)
                                + (twiddles[currentTwiddle + 1] * t2);
                            float x2 =
                                (twiddles[currentTwiddle] * t2)
                                - (twiddles[currentTwiddle + 1] * t1);
                            float x3 =
                                (twiddles[inverseTwiddle] * t3)
                                + (twiddles[inverseTwiddle + 1] * t4);
                            float x4 =
                                (twiddles[inverseTwiddle] * t4)
                                - (twiddles[inverseTwiddle + 1] * t3);
                            input[firstRealIndex] = x3 + x1;
                            input[inverseImaginaryIndex] = x3 - x1;
                            input[firstImaginaryIndex] = x2 + x4;
                            input[inverseRealIndex] = x2 - x4;
                            currentTwiddle += 2;
                            inverseTwiddle += 2;
                        }
                    }
                }
            }

            for (int j = 1, inverseJ = radix - 1;
                j < halfRadix;
                j++, inverseJ--)
            {
                for (int k = 0; k < l1; k++)
                {
                    int inverseIndex =
                        GenericC1(0, k, inverseJ, ido, l1);
                    int index = GenericC1(0, k, j, ido, l1);
                    MinusPlusInPlace(
                        ref input[inverseIndex],
                        ref input[index]);
                }
            }

            for (int l = 1, inverseL = radix - 1;
                l < halfRadix;
                l++, inverseL--)
            {
                for (int flattened = 0;
                    flattened < flattenedLength;
                    flattened++)
                {
                    output[GenericC2(flattened, l, flattenedLength)] =
                        (input[GenericC2(flattened, 0, flattenedLength)]
                            + (genericTwiddles[2 * l]
                                * input[GenericC2(
                                    flattened,
                                    1,
                                    flattenedLength)]))
                            + (genericTwiddles[4 * l]
                                * input[GenericC2(
                                    flattened,
                                    2,
                                    flattenedLength)]);
                    output[GenericC2(
                        flattened,
                        inverseL,
                        flattenedLength)] =
                        (genericTwiddles[(2 * l) + 1]
                            * input[GenericC2(
                                flattened,
                                radix - 1,
                                flattenedLength)])
                        + (genericTwiddles[(4 * l) + 1]
                            * input[GenericC2(
                                flattened,
                                radix - 2,
                                flattenedLength)]);
                }

                int angleIndex = 2 * l;
                int j = 3;
                int inverseJ = radix - 3;
                for (;
                    j < halfRadix - 3;
                    j += 4, inverseJ -= 4)
                {
                    angleIndex += l;
                    if (angleIndex >= radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar1 = genericTwiddles[2 * angleIndex];
                    float ai1 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex >= radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar2 = genericTwiddles[2 * angleIndex];
                    float ai2 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex >= radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar3 = genericTwiddles[2 * angleIndex];
                    float ai3 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex >= radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar4 = genericTwiddles[2 * angleIndex];
                    float ai4 = genericTwiddles[(2 * angleIndex) + 1];
                    for (int flattened = 0;
                        flattened < flattenedLength;
                        flattened++)
                    {
                        int outputIndex =
                            GenericC2(flattened, l, flattenedLength);
                        output[outputIndex] +=
                            ((ar1 * input[GenericC2(
                                flattened,
                                j,
                                flattenedLength)])
                            + (ar2 * input[GenericC2(
                                flattened,
                                j + 1,
                                flattenedLength)]))
                            + ((ar3 * input[GenericC2(
                                flattened,
                                j + 2,
                                flattenedLength)])
                            + (ar4 * input[GenericC2(
                                flattened,
                                j + 3,
                                flattenedLength)]));
                        int inverseOutputIndex = GenericC2(
                            flattened,
                            inverseL,
                            flattenedLength);
                        output[inverseOutputIndex] +=
                            ((ai1 * input[GenericC2(
                                flattened,
                                inverseJ,
                                flattenedLength)])
                            + (ai2 * input[GenericC2(
                                flattened,
                                inverseJ - 1,
                                flattenedLength)]))
                            + ((ai3 * input[GenericC2(
                                flattened,
                                inverseJ - 2,
                                flattenedLength)])
                            + (ai4 * input[GenericC2(
                                flattened,
                                inverseJ - 3,
                                flattenedLength)]));
                    }
                }

                for (;
                    j < halfRadix - 1;
                    j += 2, inverseJ -= 2)
                {
                    angleIndex += l;
                    if (angleIndex >= radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar1 = genericTwiddles[2 * angleIndex];
                    float ai1 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex >= radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar2 = genericTwiddles[2 * angleIndex];
                    float ai2 = genericTwiddles[(2 * angleIndex) + 1];
                    for (int flattened = 0;
                        flattened < flattenedLength;
                        flattened++)
                    {
                        int outputIndex =
                            GenericC2(flattened, l, flattenedLength);
                        output[outputIndex] +=
                            (ar1 * input[GenericC2(
                                flattened,
                                j,
                                flattenedLength)])
                            + (ar2 * input[GenericC2(
                                flattened,
                                j + 1,
                                flattenedLength)]);
                        int inverseOutputIndex = GenericC2(
                            flattened,
                            inverseL,
                            flattenedLength);
                        output[inverseOutputIndex] +=
                            (ai1 * input[GenericC2(
                                flattened,
                                inverseJ,
                                flattenedLength)])
                            + (ai2 * input[GenericC2(
                                flattened,
                                inverseJ - 1,
                                flattenedLength)]);
                    }
                }

                for (; j < halfRadix; j++, inverseJ--)
                {
                    angleIndex += l;
                    if (angleIndex >= radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar = genericTwiddles[2 * angleIndex];
                    float ai = genericTwiddles[(2 * angleIndex) + 1];
                    for (int flattened = 0;
                        flattened < flattenedLength;
                        flattened++)
                    {
                        output[GenericC2(
                            flattened,
                            l,
                            flattenedLength)] +=
                            ar * input[GenericC2(
                                flattened,
                                j,
                                flattenedLength)];
                        output[GenericC2(
                            flattened,
                            inverseL,
                            flattenedLength)] +=
                            ai * input[GenericC2(
                                flattened,
                                inverseJ,
                                flattenedLength)];
                    }
                }
            }

            for (int flattened = 0;
                flattened < flattenedLength;
                flattened++)
            {
                output[GenericC2(flattened, 0, flattenedLength)] =
                    input[GenericC2(flattened, 0, flattenedLength)];
            }

            for (int j = 1; j < halfRadix; j++)
            {
                for (int flattened = 0;
                    flattened < flattenedLength;
                    flattened++)
                {
                    output[GenericC2(flattened, 0, flattenedLength)] +=
                        input[GenericC2(
                            flattened,
                            j,
                            flattenedLength)];
                }
            }

            for (int k = 0; k < l1; k++)
            {
                for (int i = 0; i < ido; i++)
                {
                    input[GenericPacked(i, 0, k, ido, radix)] =
                        output[GenericC1(i, k, 0, ido, l1)];
                }
            }

            for (int j = 1, inverseJ = radix - 1;
                j < halfRadix;
                j++, inverseJ--)
            {
                int packedIndex = (2 * j) - 1;
                for (int k = 0; k < l1; k++)
                {
                    input[GenericPacked(
                        ido - 1,
                        packedIndex,
                        k,
                        ido,
                        radix)] =
                        output[GenericC1(0, k, j, ido, l1)];
                    input[GenericPacked(
                        0,
                        packedIndex + 1,
                        k,
                        ido,
                        radix)] =
                        output[GenericC1(
                            0,
                            k,
                            inverseJ,
                            ido,
                            l1)];
                }
            }

            if (ido == 1)
            {
                return;
            }

            for (int j = 1, inverseJ = radix - 1;
                j < halfRadix;
                j++, inverseJ--)
            {
                int packedIndex = (2 * j) - 1;
                for (int k = 0; k < l1; k++)
                {
                    for (int i = 1, inverse = ido - i - 2;
                        i <= ido - 2;
                        i += 2, inverse -= 2)
                    {
                        input[GenericPacked(
                            i,
                            packedIndex + 1,
                            k,
                            ido,
                            radix)] =
                            output[GenericC1(i, k, j, ido, l1)]
                            + output[GenericC1(
                                i,
                                k,
                                inverseJ,
                                ido,
                                l1)];
                        input[GenericPacked(
                            inverse,
                            packedIndex,
                            k,
                            ido,
                            radix)] =
                            output[GenericC1(i, k, j, ido, l1)]
                            - output[GenericC1(
                                i,
                                k,
                                inverseJ,
                                ido,
                                l1)];
                        input[GenericPacked(
                            i + 1,
                            packedIndex + 1,
                            k,
                            ido,
                            radix)] =
                            output[GenericC1(i + 1, k, j, ido, l1)]
                            + output[GenericC1(
                                i + 1,
                                k,
                                inverseJ,
                                ido,
                                l1)];
                        input[GenericPacked(
                            inverse + 1,
                            packedIndex,
                            k,
                            ido,
                            radix)] =
                            output[GenericC1(
                                i + 1,
                                k,
                                inverseJ,
                                ido,
                                l1)]
                            - output[GenericC1(
                                i + 1,
                                k,
                                j,
                                ido,
                                l1)];
                    }
                }
            }
        }

        private static void Radix2Backward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            for (int k = 0; k < l1; k++)
            {
                float left = input[BackwardInput(0, 0, k, ido, 2)];
                float right = input[BackwardInput(ido - 1, 1, k, ido, 2)];
                output[BackwardOutput(0, k, 0, ido, l1)] = left + right;
                output[BackwardOutput(0, k, 1, ido, l1)] = left - right;
            }

            if ((ido & 1) == 0)
            {
                for (int k = 0; k < l1; k++)
                {
                    output[BackwardOutput(ido - 1, k, 0, ido, l1)] =
                        2.0f * input[BackwardInput(ido - 1, 0, k, ido, 2)];
                    output[BackwardOutput(ido - 1, k, 1, ido, l1)] =
                        -2.0f * input[BackwardInput(0, 1, k, ido, 2)];
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
                    float c1 = input[BackwardInput(i - 1, 0, k, ido, 2)];
                    float c2 = input[BackwardInput(ic - 1, 1, k, ido, 2)];
                    output[BackwardOutput(i - 1, k, 0, ido, l1)] = c1 + c2;
                    float tr2 = c1 - c2;
                    float c3 = input[BackwardInput(i, 0, k, ido, 2)];
                    float c4 = input[BackwardInput(ic, 1, k, ido, 2)];
                    float ti2 = c3 + c4;
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

        private static void Radix3Backward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            const float TauReal = -0.5f;
            const float TauImaginary =
                0.8660254037844386467637231707529362f;
            for (int k = 0; k < l1; k++)
            {
                float tr2 =
                    2.0f * input[BackwardInput(ido - 1, 1, k, ido, 3)];
                float cr2 = input[BackwardInput(0, 0, k, ido, 3)]
                    + (TauReal * tr2);
                output[BackwardOutput(0, k, 0, ido, l1)] =
                    input[BackwardInput(0, 0, k, ido, 3)] + tr2;
                float ci3 =
                    2.0f * TauImaginary
                    * input[BackwardInput(0, 2, k, ido, 3)];
                output[BackwardOutput(0, k, 2, ido, l1)] =
                    cr2 + ci3;
                output[BackwardOutput(0, k, 1, ido, l1)] =
                    cr2 - ci3;
            }

            if (ido == 1)
            {
                return;
            }

            int stride = ido - 1;
            for (int k = 0; k < l1; k++)
            {
                for (int i = 2, inverse = ido - 2;
                    i < ido;
                    i += 2, inverse -= 2)
                {
                    float tr2 =
                        input[BackwardInput(i - 1, 2, k, ido, 3)]
                        + input[BackwardInput(
                            inverse - 1,
                            1,
                            k,
                            ido,
                            3)];
                    float ti2 =
                        input[BackwardInput(i, 2, k, ido, 3)]
                        - input[BackwardInput(inverse, 1, k, ido, 3)];
                    float c0Real =
                        input[BackwardInput(i - 1, 0, k, ido, 3)];
                    float c0Imaginary =
                        input[BackwardInput(i, 0, k, ido, 3)];
                    float cr2 = c0Real + (TauReal * tr2);
                    float ci2 = c0Imaginary + (TauReal * ti2);
                    output[BackwardOutput(i - 1, k, 0, ido, l1)] =
                        c0Real + tr2;
                    output[BackwardOutput(i, k, 0, ido, l1)] =
                        c0Imaginary + ti2;
                    float cr3 = TauImaginary
                        * (input[BackwardInput(i - 1, 2, k, ido, 3)]
                            - input[BackwardInput(
                                inverse - 1,
                                1,
                                k,
                                ido,
                                3)]);
                    float ci3 = TauImaginary
                        * (input[BackwardInput(i, 2, k, ido, 3)]
                            + input[BackwardInput(
                                inverse,
                                1,
                                k,
                                ido,
                                3)]);
                    Pair(out float dr3, out float dr2, cr2, ci3);
                    Pair(out float di2, out float di3, ci2, cr3);
                    MultiplyConjugate(
                        twiddles[i - 2],
                        twiddles[i - 1],
                        di2,
                        dr2,
                        out output[BackwardOutput(i, k, 1, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 1, ido, l1)]);
                    MultiplyConjugate(
                        twiddles[stride + i - 2],
                        twiddles[stride + i - 1],
                        di3,
                        dr3,
                        out output[BackwardOutput(i, k, 2, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 2, ido, l1)]);
                }
            }
        }

        private static void Radix5Backward(
            int ido,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles)
        {
            const float Tr11 =
                0.3090169943749474241022934171828191f;
            const float Ti11 =
                0.9510565162951535721164393333793821f;
            const float Tr12 =
                -0.8090169943749474241022934171828191f;
            const float Ti12 =
                0.5877852522924731291687059546390728f;
            for (int k = 0; k < l1; k++)
            {
                float ti5 =
                    2.0f * input[BackwardInput(0, 2, k, ido, 5)];
                float ti4 =
                    2.0f * input[BackwardInput(0, 4, k, ido, 5)];
                float tr2 =
                    2.0f * input[BackwardInput(ido - 1, 1, k, ido, 5)];
                float tr3 =
                    2.0f * input[BackwardInput(ido - 1, 3, k, ido, 5)];
                float c0 = input[BackwardInput(0, 0, k, ido, 5)];
                output[BackwardOutput(0, k, 0, ido, l1)] =
                    (c0 + tr2) + tr3;
                float cr2 = (c0 + (Tr11 * tr2)) + (Tr12 * tr3);
                float cr3 = (c0 + (Tr12 * tr2)) + (Tr11 * tr3);
                MultiplyConjugate(
                    Ti11,
                    Ti12,
                    ti5,
                    ti4,
                    out float ci5,
                    out float ci4);
                output[BackwardOutput(0, k, 4, ido, l1)] =
                    cr2 + ci5;
                output[BackwardOutput(0, k, 1, ido, l1)] =
                    cr2 - ci5;
                output[BackwardOutput(0, k, 3, ido, l1)] =
                    cr3 + ci4;
                output[BackwardOutput(0, k, 2, ido, l1)] =
                    cr3 - ci4;
            }

            if (ido == 1)
            {
                return;
            }

            int stride = ido - 1;
            for (int k = 0; k < l1; k++)
            {
                for (int i = 2, inverse = ido - 2;
                    i < ido;
                    i += 2, inverse -= 2)
                {
                    Pair(
                        out float tr2,
                        out float tr5,
                        input[BackwardInput(i - 1, 2, k, ido, 5)],
                        input[BackwardInput(
                            inverse - 1,
                            1,
                            k,
                            ido,
                            5)]);
                    Pair(
                        out float ti5,
                        out float ti2,
                        input[BackwardInput(i, 2, k, ido, 5)],
                        input[BackwardInput(inverse, 1, k, ido, 5)]);
                    Pair(
                        out float tr3,
                        out float tr4,
                        input[BackwardInput(i - 1, 4, k, ido, 5)],
                        input[BackwardInput(
                            inverse - 1,
                            3,
                            k,
                            ido,
                            5)]);
                    Pair(
                        out float ti4,
                        out float ti3,
                        input[BackwardInput(i, 4, k, ido, 5)],
                        input[BackwardInput(inverse, 3, k, ido, 5)]);
                    float c0Real =
                        input[BackwardInput(i - 1, 0, k, ido, 5)];
                    float c0Imaginary =
                        input[BackwardInput(i, 0, k, ido, 5)];
                    output[BackwardOutput(i - 1, k, 0, ido, l1)] =
                        (c0Real + tr2) + tr3;
                    output[BackwardOutput(i, k, 0, ido, l1)] =
                        (c0Imaginary + ti2) + ti3;
                    float cr2 =
                        (c0Real + (Tr11 * tr2)) + (Tr12 * tr3);
                    float ci2 =
                        (c0Imaginary + (Tr11 * ti2)) + (Tr12 * ti3);
                    float cr3 =
                        (c0Real + (Tr12 * tr2)) + (Tr11 * tr3);
                    float ci3 =
                        (c0Imaginary + (Tr12 * ti2)) + (Tr11 * ti3);
                    MultiplyConjugate(
                        Ti11,
                        Ti12,
                        tr5,
                        tr4,
                        out float cr5,
                        out float cr4);
                    MultiplyConjugate(
                        Ti11,
                        Ti12,
                        ti5,
                        ti4,
                        out float ci5,
                        out float ci4);
                    Pair(out float dr4, out float dr3, cr3, ci4);
                    Pair(out float di3, out float di4, ci3, cr4);
                    Pair(out float dr5, out float dr2, cr2, ci5);
                    Pair(out float di2, out float di5, ci2, cr5);
                    MultiplyConjugate(
                        twiddles[i - 2],
                        twiddles[i - 1],
                        di2,
                        dr2,
                        out output[BackwardOutput(i, k, 1, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 1, ido, l1)]);
                    MultiplyConjugate(
                        twiddles[stride + i - 2],
                        twiddles[stride + i - 1],
                        di3,
                        dr3,
                        out output[BackwardOutput(i, k, 2, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 2, ido, l1)]);
                    MultiplyConjugate(
                        twiddles[(2 * stride) + i - 2],
                        twiddles[(2 * stride) + i - 1],
                        di4,
                        dr4,
                        out output[BackwardOutput(i, k, 3, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 3, ido, l1)]);
                    MultiplyConjugate(
                        twiddles[(3 * stride) + i - 2],
                        twiddles[(3 * stride) + i - 1],
                        di5,
                        dr5,
                        out output[BackwardOutput(i, k, 4, ido, l1)],
                        out output[BackwardOutput(i - 1, k, 4, ido, l1)]);
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

        private static void RadixGenericBackward(
            int ido,
            int radix,
            int l1,
            float[] input,
            float[] output,
            float[] twiddles,
            float[] genericTwiddles)
        {
            int halfRadix = (radix + 1) / 2;
            int flattenedLength = ido * l1;

            for (int k = 0; k < l1; k++)
            {
                for (int i = 0; i < ido; i++)
                {
                    output[GenericC1(i, k, 0, ido, l1)] =
                        input[GenericPacked(i, 0, k, ido, radix)];
                }
            }

            for (int j = 1, inverseJ = radix - 1;
                j < halfRadix;
                j++, inverseJ--)
            {
                int packedIndex = (2 * j) - 1;
                for (int k = 0; k < l1; k++)
                {
                    output[GenericC1(0, k, j, ido, l1)] =
                        2.0f * input[GenericPacked(
                            ido - 1,
                            packedIndex,
                            k,
                            ido,
                            radix)];
                    output[GenericC1(0, k, inverseJ, ido, l1)] =
                        2.0f * input[GenericPacked(
                            0,
                            packedIndex + 1,
                            k,
                            ido,
                            radix)];
                }
            }

            if (ido != 1)
            {
                for (int j = 1, inverseJ = radix - 1;
                    j < halfRadix;
                    j++, inverseJ--)
                {
                    int packedIndex = (2 * j) - 1;
                    for (int k = 0; k < l1; k++)
                    {
                        for (int i = 1, inverse = ido - i - 2;
                            i <= ido - 2;
                            i += 2, inverse -= 2)
                        {
                            output[GenericC1(i, k, j, ido, l1)] =
                                input[GenericPacked(
                                    i,
                                    packedIndex + 1,
                                    k,
                                    ido,
                                    radix)]
                                + input[GenericPacked(
                                    inverse,
                                    packedIndex,
                                    k,
                                    ido,
                                    radix)];
                            output[GenericC1(
                                i,
                                k,
                                inverseJ,
                                ido,
                                l1)] =
                                input[GenericPacked(
                                    i,
                                    packedIndex + 1,
                                    k,
                                    ido,
                                    radix)]
                                - input[GenericPacked(
                                    inverse,
                                    packedIndex,
                                    k,
                                    ido,
                                    radix)];
                            output[GenericC1(i + 1, k, j, ido, l1)] =
                                input[GenericPacked(
                                    i + 1,
                                    packedIndex + 1,
                                    k,
                                    ido,
                                    radix)]
                                - input[GenericPacked(
                                    inverse + 1,
                                    packedIndex,
                                    k,
                                    ido,
                                    radix)];
                            output[GenericC1(
                                i + 1,
                                k,
                                inverseJ,
                                ido,
                                l1)] =
                                input[GenericPacked(
                                    i + 1,
                                    packedIndex + 1,
                                    k,
                                    ido,
                                    radix)]
                                + input[GenericPacked(
                                    inverse + 1,
                                    packedIndex,
                                    k,
                                    ido,
                                    radix)];
                        }
                    }
                }
            }

            for (int l = 1, inverseL = radix - 1;
                l < halfRadix;
                l++, inverseL--)
            {
                for (int flattened = 0;
                    flattened < flattenedLength;
                    flattened++)
                {
                    input[GenericC2(flattened, l, flattenedLength)] =
                        (output[GenericC2(flattened, 0, flattenedLength)]
                            + (genericTwiddles[2 * l]
                                * output[GenericC2(
                                    flattened,
                                    1,
                                    flattenedLength)]))
                            + (genericTwiddles[4 * l]
                                * output[GenericC2(
                                    flattened,
                                    2,
                                    flattenedLength)]);
                    input[GenericC2(
                        flattened,
                        inverseL,
                        flattenedLength)] =
                        (genericTwiddles[(2 * l) + 1]
                            * output[GenericC2(
                                flattened,
                                radix - 1,
                                flattenedLength)])
                        + (genericTwiddles[(4 * l) + 1]
                            * output[GenericC2(
                                flattened,
                                radix - 2,
                                flattenedLength)]);
                }

                int angleIndex = 2 * l;
                int j = 3;
                int inverseJ = radix - 3;
                for (;
                    j < halfRadix - 3;
                    j += 4, inverseJ -= 4)
                {
                    angleIndex += l;
                    if (angleIndex > radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar1 = genericTwiddles[2 * angleIndex];
                    float ai1 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex > radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar2 = genericTwiddles[2 * angleIndex];
                    float ai2 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex > radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar3 = genericTwiddles[2 * angleIndex];
                    float ai3 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex > radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar4 = genericTwiddles[2 * angleIndex];
                    float ai4 = genericTwiddles[(2 * angleIndex) + 1];
                    for (int flattened = 0;
                        flattened < flattenedLength;
                        flattened++)
                    {
                        int inputIndex =
                            GenericC2(flattened, l, flattenedLength);
                        input[inputIndex] +=
                            ((ar1 * output[GenericC2(
                                flattened,
                                j,
                                flattenedLength)])
                            + (ar2 * output[GenericC2(
                                flattened,
                                j + 1,
                                flattenedLength)]))
                            + ((ar3 * output[GenericC2(
                                flattened,
                                j + 2,
                                flattenedLength)])
                            + (ar4 * output[GenericC2(
                                flattened,
                                j + 3,
                                flattenedLength)]));
                        int inverseInputIndex = GenericC2(
                            flattened,
                            inverseL,
                            flattenedLength);
                        input[inverseInputIndex] +=
                            ((ai1 * output[GenericC2(
                                flattened,
                                inverseJ,
                                flattenedLength)])
                            + (ai2 * output[GenericC2(
                                flattened,
                                inverseJ - 1,
                                flattenedLength)]))
                            + ((ai3 * output[GenericC2(
                                flattened,
                                inverseJ - 2,
                                flattenedLength)])
                            + (ai4 * output[GenericC2(
                                flattened,
                                inverseJ - 3,
                                flattenedLength)]));
                    }
                }

                for (;
                    j < halfRadix - 1;
                    j += 2, inverseJ -= 2)
                {
                    angleIndex += l;
                    if (angleIndex > radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar1 = genericTwiddles[2 * angleIndex];
                    float ai1 = genericTwiddles[(2 * angleIndex) + 1];
                    angleIndex += l;
                    if (angleIndex > radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar2 = genericTwiddles[2 * angleIndex];
                    float ai2 = genericTwiddles[(2 * angleIndex) + 1];
                    for (int flattened = 0;
                        flattened < flattenedLength;
                        flattened++)
                    {
                        int inputIndex =
                            GenericC2(flattened, l, flattenedLength);
                        input[inputIndex] +=
                            (ar1 * output[GenericC2(
                                flattened,
                                j,
                                flattenedLength)])
                            + (ar2 * output[GenericC2(
                                flattened,
                                j + 1,
                                flattenedLength)]);
                        int inverseInputIndex = GenericC2(
                            flattened,
                            inverseL,
                            flattenedLength);
                        input[inverseInputIndex] +=
                            (ai1 * output[GenericC2(
                                flattened,
                                inverseJ,
                                flattenedLength)])
                            + (ai2 * output[GenericC2(
                                flattened,
                                inverseJ - 1,
                                flattenedLength)]);
                    }
                }

                for (; j < halfRadix; j++, inverseJ--)
                {
                    angleIndex += l;
                    if (angleIndex > radix)
                    {
                        angleIndex -= radix;
                    }

                    float ar = genericTwiddles[2 * angleIndex];
                    float ai = genericTwiddles[(2 * angleIndex) + 1];
                    for (int flattened = 0;
                        flattened < flattenedLength;
                        flattened++)
                    {
                        input[GenericC2(
                            flattened,
                            l,
                            flattenedLength)] +=
                            ar * output[GenericC2(
                                flattened,
                                j,
                                flattenedLength)];
                        input[GenericC2(
                            flattened,
                            inverseL,
                            flattenedLength)] +=
                            ai * output[GenericC2(
                                flattened,
                                inverseJ,
                                flattenedLength)];
                    }
                }
            }

            for (int j = 1; j < halfRadix; j++)
            {
                for (int flattened = 0;
                    flattened < flattenedLength;
                    flattened++)
                {
                    output[GenericC2(flattened, 0, flattenedLength)] +=
                        output[GenericC2(
                            flattened,
                            j,
                            flattenedLength)];
                }
            }

            for (int j = 1, inverseJ = radix - 1;
                j < halfRadix;
                j++, inverseJ--)
            {
                for (int k = 0; k < l1; k++)
                {
                    int inverseIndex =
                        GenericC1(0, k, inverseJ, ido, l1);
                    int index = GenericC1(0, k, j, ido, l1);
                    float left = input[index];
                    float right = input[inverseIndex];
                    output[inverseIndex] = left + right;
                    output[index] = left - right;
                }
            }

            if (ido == 1)
            {
                return;
            }

            for (int j = 1, inverseJ = radix - 1;
                j < halfRadix;
                j++, inverseJ--)
            {
                for (int k = 0; k < l1; k++)
                {
                    for (int i = 1; i <= ido - 2; i += 2)
                    {
                        output[GenericC1(i, k, j, ido, l1)] =
                            input[GenericC1(i, k, j, ido, l1)]
                            - input[GenericC1(
                                i + 1,
                                k,
                                inverseJ,
                                ido,
                                l1)];
                        output[GenericC1(
                            i,
                            k,
                            inverseJ,
                            ido,
                            l1)] =
                            input[GenericC1(i, k, j, ido, l1)]
                            + input[GenericC1(
                                i + 1,
                                k,
                                inverseJ,
                                ido,
                                l1)];
                        output[GenericC1(i + 1, k, j, ido, l1)] =
                            input[GenericC1(i + 1, k, j, ido, l1)]
                            + input[GenericC1(
                                i,
                                k,
                                inverseJ,
                                ido,
                                l1)];
                        output[GenericC1(
                            i + 1,
                            k,
                            inverseJ,
                            ido,
                            l1)] =
                            input[GenericC1(i + 1, k, j, ido, l1)]
                            - input[GenericC1(
                                i,
                                k,
                                inverseJ,
                                ido,
                                l1)];
                    }
                }
            }

            for (int j = 1; j < radix; j++)
            {
                int twiddleOffset = (j - 1) * (ido - 1);
                for (int k = 0; k < l1; k++)
                {
                    int currentTwiddle = twiddleOffset;
                    for (int i = 1; i <= ido - 2; i += 2)
                    {
                        int realIndex =
                            GenericC1(i, k, j, ido, l1);
                        int imaginaryIndex =
                            GenericC1(i + 1, k, j, ido, l1);
                        float real = output[realIndex];
                        float imaginary = output[imaginaryIndex];
                        output[realIndex] =
                            (twiddles[currentTwiddle] * real)
                            - (twiddles[currentTwiddle + 1]
                                * imaginary);
                        output[imaginaryIndex] =
                            (twiddles[currentTwiddle] * imaginary)
                            + (twiddles[currentTwiddle + 1] * real);
                        currentTwiddle += 2;
                    }
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

        private static void Pair(
            out float sum,
            out float difference,
            float left,
            float right)
        {
            sum = left + right;
            difference = left - right;
        }

        private static void Rearrange(
            ref float firstReal,
            ref float firstImaginary,
            ref float secondReal,
            ref float secondImaginary)
        {
            float sumReal = firstReal + secondReal;
            float differenceReal = secondReal - firstReal;
            float sumImaginary = firstImaginary + secondImaginary;
            float differenceImaginary =
                firstImaginary - secondImaginary;
            firstReal = sumReal;
            firstImaginary = sumImaginary;
            secondReal = differenceImaginary;
            secondImaginary = differenceReal;
        }

        private static void MinusPlusInPlace(
            ref float left,
            ref float right)
        {
            float originalLeft = left;
            left -= right;
            right = originalLeft + right;
        }

        private static int GenericPacked(
            int a,
            int b,
            int c,
            int ido,
            int radix)
            => a + (ido * (b + (radix * c)));

        private static int GenericC1(
            int a,
            int b,
            int c,
            int ido,
            int l1)
            => a + (ido * (b + (l1 * c)));

        private static int GenericC2(
            int a,
            int b,
            int flattenedLength)
            => a + (flattenedLength * b);

        private static int ForwardInput(
            int a,
            int b,
            int c,
            int ido,
            int l1)
            => a + (ido * (b + (l1 * c)));

        private static int ForwardOutput(int a, int b, int c, int ido)
            => a + (ido * (b + (4 * c)));

        private static int ForwardOutput(
            int a,
            int b,
            int c,
            int ido,
            int radix)
            => a + (ido * (b + (radix * c)));

        private static int BackwardInput(int a, int b, int c, int ido)
            => a + (ido * (b + (4 * c)));

        private static int BackwardInput(
            int a,
            int b,
            int c,
            int ido,
            int radix)
            => a + (ido * (b + (radix * c)));

        private static int BackwardOutput(
            int a,
            int b,
            int c,
            int ido,
            int l1)
            => a + (ido * (b + (l1 * c)));
    }

    private sealed record Factor(
        int Radix,
        float[] Twiddles,
        float[] GenericTwiddles);

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
