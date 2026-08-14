using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppRealDft32Tests
{
    [Theory(DisplayName = "IPP real DFT32 rejects odd and undersized lengths before probing native runtime")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(13)]
    public void RejectsUnsupportedLengthsBeforeNativeProbe(int length)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new IppRealDft32(length));
        Assert.Contains("even integer", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP mixed-radix real DFT32 agrees numerically and round-trips")]
    [InlineData(14)]
    [InlineData(90)]
    [InlineData(3_564)]
    public void AgreesNumericallyAndRoundTrips(int length)
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        float[] input = BuildInput(length);
        Complex32[] expected = PocketFftReal32.ForwardAnyLength(input);
        var actual = new IppComplex32[expected.Length];
        var reconstructed = new float[length];

        using var dft = new IppRealDft32(length);
        Assert.Equal(length, dft.Length);
        Assert.Equal(expected.Length, dft.SpectrumLength);
        dft.Forward(input, actual);
        dft.Inverse(actual, reconstructed);

        for (int index = 0; index < expected.Length; index++)
        {
            double realTolerance = 2.5e-4 * Math.Max(1.0, Math.Abs(expected[index].Real));
            double imaginaryTolerance = 2.5e-4 * Math.Max(1.0, Math.Abs(expected[index].Imaginary));
            Assert.InRange(
                Math.Abs(expected[index].Real - actual[index].Real),
                0.0,
                realTolerance);
            Assert.InRange(
                Math.Abs(expected[index].Imaginary - actual[index].Imaginary),
                0.0,
                imaginaryTolerance);
        }

        for (int index = 0; index < input.Length; index++)
        {
            double tolerance = 2.5e-5 * Math.Max(1.0, Math.Abs(input[index]));
            Assert.InRange(
                Math.Abs(input[index] - reconstructed[index]),
                0.0,
                tolerance);
        }
    }

    [Fact(DisplayName = "IPP mixed-radix real DFT32 is deterministic across dirty reuse")]
    public void IsDeterministicAcrossDirtyReuse()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 3_564;
        float[] input = BuildInput(Length);
        var firstSpectrum = new IppComplex32[(Length / 2) + 1];
        var secondSpectrum = new IppComplex32[firstSpectrum.Length];
        var firstOutput = new float[Length];
        var secondOutput = new float[Length];

        using var dft = new IppRealDft32(Length);
        dft.Forward(input, firstSpectrum);
        dft.Inverse(firstSpectrum, firstOutput);
        Array.Fill(secondSpectrum, new IppComplex32(float.NaN, float.NegativeInfinity));
        Array.Fill(secondOutput, float.NaN);
        dft.Forward(input, secondSpectrum);
        dft.Inverse(secondSpectrum, secondOutput);

        Assert.Equal(firstSpectrum, secondSpectrum);
        Assert.Equal(firstOutput, secondOutput);
    }

    [Fact(DisplayName = "IPP current Super-Gaussian filter remains numerically close to Exact")]
    public void SuperGaussianFilterRemainsNumericallyCloseToExact()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 355_255;
        const double FscHz = 4_406_000.0;
        const double CarrierHz = 732_400.0;
        double[] input = BuildInput(Length)
            .Select(static value => (double)(value * 16_384.0F))
            .ToArray();
        double[] exact = new ChromaSuperGaussianFinalFilter(
                Length,
                FscHz,
                CarrierHz,
                DspBackend.Exact)
            .ApplyInPlace((double[])input.Clone(), workerThreads: 20);
        double[] accelerated = new ChromaSuperGaussianFinalFilter(
                Length,
                FscHz,
                CarrierHz,
                DspBackend.IppFast)
            .ApplyInPlace((double[])input.Clone(), workerThreads: 20);

        double sumSquared = 0.0;
        double maximum = 0.0;
        for (int index = 0; index < exact.Length; index++)
        {
            double difference = Math.Abs(exact[index] - accelerated[index]);
            maximum = Math.Max(maximum, difference);
            sumSquared += difference * difference;
        }

        double rootMeanSquare = Math.Sqrt(sumSquared / exact.Length);
        Assert.InRange(rootMeanSquare, 0.0, 0.1);
        Assert.InRange(maximum, 0.0, 2.0);
    }

    [Fact(DisplayName = "IPP Super-Gaussian staging conversions preserve scalar bits")]
    public void SuperGaussianStagingConversionsPreserveScalarBits()
    {
        double[] source = BuildStagingDoubleValues();
        float[] expectedFloat = source
            .Select(static value => (float)value)
            .ToArray();
        var actualFloat = new float[source.Length + 3];
        Array.Fill(actualFloat, float.NaN);

        ChromaSuperGaussianFinalFilter.CopyFloat64ToFloat32(
            source,
            actualFloat.AsSpan(1, source.Length));

        for (int index = 0; index < source.Length; index++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(expectedFloat[index]),
                BitConverter.SingleToUInt32Bits(actualFloat[index + 1]));
        }

        double[] expectedDouble = expectedFloat
            .Select(static value => (double)value)
            .ToArray();
        var actualDouble = new double[expectedFloat.Length + 3];
        Array.Fill(actualDouble, double.NaN);

        ChromaSuperGaussianFinalFilter.CopyFloat32ToFloat64(
            expectedFloat,
            actualDouble.AsSpan(2, expectedFloat.Length));

        for (int index = 0; index < expectedFloat.Length; index++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(expectedDouble[index]),
                BitConverter.DoubleToUInt64Bits(actualDouble[index + 2]));
        }
    }

    [Fact(DisplayName = "Super-Gaussian mask SIMD preserves scalar complex bits")]
    public void SuperGaussianMaskSimdPreservesScalarComplexBits()
    {
        Assert.Equal(2 * sizeof(float), Unsafe.SizeOf<Complex32>());
        if (Environment.GetEnvironmentVariable(
                "VHSDECODE_REQUIRE_AVX_SUPER_GAUSSIAN_MASK") == "1")
        {
            Assert.True(Avx.IsSupported, "The AVX Super-Gaussian mask path is required by this test run.");
        }

        double[] values = BuildStagingDoubleValues();
        var mask = new double[values.Length];
        var expected = new IppComplex32[values.Length];
        var actual = new IppComplex32[values.Length];
        var actualManaged = new Complex32[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            float real = (float)values[index];
            float imaginary = (float)values[(index * 7 + 3) % values.Length];
            double factor = values[(index * 5 + 1) % values.Length];
            mask[index] = factor;
            expected[index] = ApplyScalarMask(real, imaginary, factor);
            actual[index] = new IppComplex32(real, imaginary);
            actualManaged[index] = new Complex32(real, imaginary);
        }

        ChromaSuperGaussianFinalFilter.ApplyIppMask(actual, mask);
        ChromaSuperGaussianFinalFilter.ApplyManagedMask(actualManaged, mask);

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(expected[index].Real),
                BitConverter.SingleToUInt32Bits(actual[index].Real));
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(expected[index].Imaginary),
                BitConverter.SingleToUInt32Bits(actual[index].Imaginary));
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(expected[index].Real),
                BitConverter.SingleToUInt32Bits(actualManaged[index].Real));
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(expected[index].Imaginary),
                BitConverter.SingleToUInt32Bits(actualManaged[index].Imaginary));
        }

        (float Real, float Imaginary, double Factor)[] finiteCases =
            BuildFiniteMaskCases();
        AssertManagedMaskMatchesScalar(finiteCases);
        AssertManagedMaskMatchesScalar(finiteCases[..^1]);
        AssertManagedMaskMatchesScalar(finiteCases[..^2]);
        AssertManagedMaskMatchesScalar(finiteCases[..^3]);
        AssertManagedMaskMatchesScalar(finiteCases[..1]);
        AssertManagedMaskMatchesScalar(finiteCases[..2]);
        AssertManagedMaskMatchesScalar(finiteCases[..3]);
        AssertManagedMaskMatchesScalar(
            BuildMaskCases(),
            exceptionalVectorIndex: 8);
        Assert.Equal(
            0,
            ChromaSuperGaussianFinalFilter.ApplyManagedMask([], []));
        Assert.Throws<ArgumentException>(
            () => ChromaSuperGaussianFinalFilter.ApplyManagedMask(
                new Complex32[1],
                []));
    }

    [Theory(DisplayName = "Super-Gaussian reflect padding preserves scalar layout")]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16)]
    public void SuperGaussianReflectPaddingPreservesScalarLayout(int padLeft)
    {
        double[] input = BuildStagingDoubleValues();
        int padRight = padLeft + 1;
        var expected = new float[input.Length + padLeft + padRight];
        var actual = new float[expected.Length];
        for (int index = 0; index < padLeft; index++)
        {
            expected[index] = (float)input[padLeft - index];
        }
        for (int index = 0; index < input.Length; index++)
        {
            expected[padLeft + index] = (float)input[index];
        }
        for (int index = 0; index < padRight; index++)
        {
            expected[padLeft + input.Length + index] =
                (float)input[input.Length - index - 2];
        }

        ChromaSuperGaussianFinalFilter.FillReflectPad(input, actual, padLeft);

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(expected[index]),
                BitConverter.SingleToUInt32Bits(actual[index]));
        }
    }

    [Fact(DisplayName = "IPP Super-Gaussian contexts are released by repeated pipeline disposal")]
    public void PipelineDisposalReleasesRepeatedSuperGaussianContexts()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 3_564;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var filter = new ChromaSuperGaussianFinalFilter(
                Length,
                fscHz: 4_406_000.0,
                colorUnderCarrierHz: 732_400.0,
                dspBackend: DspBackend.IppFast);
            using TbcFieldDecodePipeline pipeline = BuildPipeline(filter);

            pipeline.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => filter.Apply(new double[Length]));
        }
    }

    private static float[] BuildInput(int length)
    {
        var input = new float[length];
        for (int index = 0; index < input.Length; index++)
        {
            input[index] = (float)(
                Math.Sin(index * 0.031)
                + (0.25 * Math.Cos(index * 0.173))
                + (((index % 17) - 8) * 0.0003));
        }

        return input;
    }

    private static double[] BuildStagingDoubleValues()
    {
        double[] edgeValues =
        [
            0.0,
            -0.0,
            double.Epsilon,
            -double.Epsilon,
            float.Epsilon,
            -float.Epsilon,
            float.MaxValue,
            -float.MaxValue,
            double.PositiveInfinity,
            double.NegativeInfinity,
            BitConverter.UInt64BitsToDouble(0x7FF8_1234_5678_9ABC),
            BitConverter.UInt64BitsToDouble(0xFFF8_1234_5678_9ABC)
        ];
        var values = new double[37];
        edgeValues.CopyTo(values, 0);
        for (int index = edgeValues.Length; index < values.Length; index++)
        {
            values[index] =
                (Math.Sin(index * 0.37) * 1000.0)
                + (((index % 5) - 2) * 0.125);
        }

        return values;
    }

    private static (float Real, float Imaginary, double Factor)[] BuildMaskCases()
    {
        double[] values = BuildStagingDoubleValues();
        const int EdgeValueCount = 12;
        var cases = new List<(float Real, float Imaginary, double Factor)>(
            (EdgeValueCount * EdgeValueCount * EdgeValueCount) + values.Length);
        for (int realIndex = 0; realIndex < EdgeValueCount; realIndex++)
        {
            for (int imaginaryIndex = 0;
                imaginaryIndex < EdgeValueCount;
                imaginaryIndex++)
            {
                for (int factorIndex = 0;
                    factorIndex < EdgeValueCount;
                    factorIndex++)
                {
                    cases.Add((
                        (float)values[realIndex],
                        (float)values[imaginaryIndex],
                        values[factorIndex]));
                }
            }
        }

        for (int index = 0; index < values.Length; index++)
        {
            cases.Add((
                (float)values[index],
                (float)values[(index * 7 + 3) % values.Length],
                values[(index * 5 + 1) % values.Length]));
        }

        return cases.ToArray();
    }

    private static (float Real, float Imaginary, double Factor)[]
        BuildFiniteMaskCases()
    {
        double[] values = BuildStagingDoubleValues();
        const int FiniteEdgeValueCount = 8;
        var cases = new List<(float Real, float Imaginary, double Factor)>(539);
        for (int realIndex = 0;
            realIndex < FiniteEdgeValueCount;
            realIndex++)
        {
            for (int imaginaryIndex = 0;
                imaginaryIndex < FiniteEdgeValueCount;
                imaginaryIndex++)
            {
                for (int factorIndex = 0;
                    factorIndex < FiniteEdgeValueCount;
                    factorIndex++)
                {
                    cases.Add((
                        (float)values[realIndex],
                        (float)values[imaginaryIndex],
                        values[factorIndex]));
                }
            }
        }

        for (int index = 12; index < values.Length; index++)
        {
            cases.Add((
                (float)values[index],
                (float)values[12 + ((index * 7 + 3) % 25)],
                values[12 + ((index * 5 + 1) % 25)]));
        }

        cases.Add((float.Epsilon, -float.Epsilon, double.Epsilon));
        cases.Add((-float.Epsilon, float.Epsilon, -double.Epsilon));
        return cases.ToArray();
    }

    private static void AssertManagedMaskMatchesScalar(
        (float Real, float Imaginary, double Factor)[] cases,
        int? exceptionalVectorIndex = null)
    {
        double maskStartSentinel = BitConverter.UInt64BitsToDouble(
            0x7FF8_1357_2468_ACE0UL);
        double maskEndSentinel = BitConverter.UInt64BitsToDouble(
            0xFFF8_0246_8ACE_1357UL);
        var maskStorage = new double[cases.Length + 2];
        maskStorage[0] = maskStartSentinel;
        maskStorage[^1] = maskEndSentinel;
        Span<double> mask = maskStorage.AsSpan(1, cases.Length);

        var expectedStorage = new Complex32[cases.Length + 2];
        var actualStorage = new Complex32[cases.Length + 2];
        var startSentinel = new Complex32(
            BitConverter.UInt32BitsToSingle(0x7FC1_3579U),
            BitConverter.UInt32BitsToSingle(0xFFC2_468AU));
        var endSentinel = new Complex32(
            BitConverter.UInt32BitsToSingle(0x7FC3_579BU),
            BitConverter.UInt32BitsToSingle(0xFFC4_68ACU));
        expectedStorage[0] = actualStorage[0] = startSentinel;
        expectedStorage[^1] = actualStorage[^1] = endSentinel;
        Span<Complex32> expected = expectedStorage.AsSpan(1, cases.Length);
        Span<Complex32> actual = actualStorage.AsSpan(1, cases.Length);
        for (int index = 0; index < cases.Length; index++)
        {
            (float real, float imaginary, double factor) = cases[index];
            mask[index] = factor;
            if (exceptionalVectorIndex is null)
            {
                IppComplex32 scalar = ApplyScalarMask(
                    real,
                    imaginary,
                    factor);
                expected[index] = new Complex32(
                    scalar.Real,
                    scalar.Imaginary);
            }
            else
            {
                expected[index] = new Complex32(real, imaginary);
            }
            actual[index] = new Complex32(real, imaginary);
        }

        if (exceptionalVectorIndex is not null)
        {
            // NaN payload bits vary across JIT shapes, so exercise the exact
            // production scalar fallback for exceptional lanes.
            ChromaSuperGaussianFinalFilter.ApplyManagedMaskScalar(
                expected,
                mask);
        }
        int vectorizedPrefixLength =
            ChromaSuperGaussianFinalFilter.ApplyManagedMask(actual, mask);
        int expectedVectorizedPrefixLength = Avx.IsSupported
            ? exceptionalVectorIndex
                ?? (cases.Length - (cases.Length % 4))
            : 0;
        Assert.Equal(
            expectedVectorizedPrefixLength,
            vectorizedPrefixLength);
        for (int index = 0; index < expected.Length; index++)
        {
            uint expectedReal = BitConverter.SingleToUInt32Bits(
                expected[index].Real);
            uint actualReal = BitConverter.SingleToUInt32Bits(
                actual[index].Real);
            Assert.True(
                expectedReal == actualReal,
                $"Real lane {index} differed: expected {expectedReal:X8}, actual {actualReal:X8}.");

            uint expectedImaginary = BitConverter.SingleToUInt32Bits(
                expected[index].Imaginary);
            uint actualImaginary = BitConverter.SingleToUInt32Bits(
                actual[index].Imaginary);
            Assert.True(
                expectedImaginary == actualImaginary,
                $"Imaginary lane {index} differed: expected {expectedImaginary:X8}, actual {actualImaginary:X8}.");
        }

        AssertComplexBitsEqual(startSentinel, expectedStorage[0]);
        AssertComplexBitsEqual(endSentinel, expectedStorage[^1]);
        AssertComplexBitsEqual(startSentinel, actualStorage[0]);
        AssertComplexBitsEqual(endSentinel, actualStorage[^1]);
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(maskStartSentinel),
            BitConverter.DoubleToUInt64Bits(maskStorage[0]));
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(maskEndSentinel),
            BitConverter.DoubleToUInt64Bits(maskStorage[^1]));
    }

    private static void AssertComplexBitsEqual(
        Complex32 expected,
        Complex32 actual)
    {
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected.Real),
            BitConverter.SingleToUInt32Bits(actual.Real));
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected.Imaginary),
            BitConverter.SingleToUInt32Bits(actual.Imaginary));
    }

    private static IppComplex32 ApplyScalarMask(
        float realValue,
        float imaginaryValue,
        double factor)
    {
        double real = realValue;
        double imaginary = imaginaryValue;
        return new IppComplex32(
            (float)((real * factor) - (imaginary * 0.0)),
            (float)((real * 0.0) + (imaginary * factor)));
    }

    private static TbcFieldDecodePipeline BuildPipeline(
        ChromaSuperGaussianFinalFilter filter)
    {
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 1_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0,
            numPulses: 5);
        var frameSpec = new TbcFrameSpec(
            "PAL",
            OutputLineLength: 4,
            OutputLineCount: 20,
            OutputSampleRateHz: 4_000_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 100.0,
            hzIre: 3.5,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 10.0);
        var chromaOptions = new VhsChromaFieldOptions(
            ColorSystem: "PAL",
            OutputLineLength: 99,
            OutputLineCount: 36,
            OutputSampleRateHz: 17_734_475.0,
            FscMHz: 4.433_618_75,
            ColorUnderCarrierHz: 732_400.0,
            BurstStart: 0,
            BurstEnd: 1,
            BurstAbsRef: 1.0,
            ChromaRotation: null,
            DisableComb: false,
            DisablePhaseCorrection: false,
            EnableColorKiller: false,
            DetectChromaTrackPhase: false)
        {
            SuperGaussianFinalFilter = filter
        };

        return new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(frameSpec, converter),
            converter,
            "PAL",
            TbcDropoutDetectionOptions.Disabled,
            syncDetectionOptions: SyncDetectionOptions.Disabled,
            chromaFieldOptions: chromaOptions,
            decodeType: "vhs");
    }
}
