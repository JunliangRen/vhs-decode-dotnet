using System.Numerics;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ChromaReductionNumericsCompatibilityTests
{
    [Fact(DisplayName = "Current RF float32 chroma shift matches pinned PR 341 reduction order")]
    public void CurrentRfFloat32ChromaShiftMatchesPinnedReductionOrder()
    {
        float[] expected =
        [
            0.265625f,
            2.515625f,
            -8.234375f,
            -0.609375f,
            8.265625f,
            1.0000000200408773e20f,
            0.265625f,
            -1.0000000200408773e20f
        ];
        float[] input =
        [
            1e20f,
            1.0f,
            -1e20f,
            1.0f,
            3.25f,
            -7.5f,
            0.125f,
            9.0f
        ];

        float[] actual =
            VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32CurrentInPlace(
                input,
                move: -3);

        Assert.Equal(
            expected.Select(BitConverter.SingleToUInt32Bits),
            actual.Select(BitConverter.SingleToUInt32Bits));
    }

    [Fact(DisplayName = "Current RF double storage preserves float32 shift semantics")]
    public void CurrentRfDoubleStoragePreservesFloat32ShiftSemantics()
    {
        double[] input =
        [
            1e20f,
            1.0f,
            -1e20f,
            1.0f,
            3.25f,
            -7.5f,
            0.125f,
            9.0f
        ];

        double[] actual =
            VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32CurrentInPlace(
                input,
                move: -3);

        uint[] expectedBits =
        [
            0x3E880000,
            0x40210000,
            0xC103C000,
            0xBF1C0000,
            0x41044000,
            0x60AD78EC,
            0x3E880000,
            0xE0AD78EC
        ];
        Assert.Equal(
            expectedBits,
            actual.Select(value => BitConverter.SingleToUInt32Bits((float)value)));
    }

    [Fact(DisplayName = "Current RF fused double chroma shift preserves the pinned implementation bits")]
    public void CurrentRfFusedDoubleChromaShiftPreservesPinnedImplementationBits()
    {
        if (Environment.GetEnvironmentVariable(
                "VHSDECODE_REQUIRE_AVX_CURRENT_CHROMA_SHIFT") == "1")
        {
            Assert.True(
                Avx.IsSupported,
                "The CI current RF chroma shift run requires AVX support.");
        }

        int[] lengths = [0, 1, 2, 3, 4, 5, 7, 8, 31, 32, 33, 257, 521];
        int[] moves = [-513, -23, -3, -1, 0, 1, 3, 23, 513];
        foreach (int length in lengths)
        {
            double[] input = Enumerable.Range(0, length)
                .Select(index => index % 11 switch
                {
                    0 => 1e20 + index,
                    1 => -1e20 - index,
                    2 => Math.PI * (index + 1),
                    3 => -Math.E * (index + 1),
                    _ => Math.Sin(index * 0.371) * 12_345.6789
                })
                .ToArray();
            foreach (int move in moves)
            {
                double[] expected = PinnedCurrentDoubleChromaShift(input, move);
                double[] actual = VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32CurrentInPlace(
                    input.ToArray(),
                    move);

                Assert.Equal(
                    expected.Select(BitConverter.DoubleToUInt64Bits),
                    actual.Select(BitConverter.DoubleToUInt64Bits));
            }
        }

        double[][] specialInputs =
        [
            [double.NaN, 1.0, 2.0, 3.0],
            [
                BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001UL),
                BitConverter.UInt64BitsToDouble(0x7FF8_0000_1000_0000UL),
                BitConverter.UInt64BitsToDouble(0x7FF0_0000_0000_0001UL),
                1.0
            ],
            [double.PositiveInfinity, 1.0, -2.0, 3.0],
            [double.NegativeInfinity, -0.0, 0.0, 7.0],
            [double.PositiveInfinity, double.NegativeInfinity, -0.0, 0.0],
            [-0.0, 0.0, -0.0, 0.0]
        ];
        foreach (double[] input in specialInputs)
        {
            foreach (int move in moves)
            {
                double[] expected = PinnedCurrentDoubleChromaShift(input, move);
                double[] actual = VhsChromaDecoder.ShiftChromaAndRemoveDcFloat32CurrentInPlace(
                    input.ToArray(),
                    move);

                Assert.Equal(
                    expected.Select(BitConverter.DoubleToUInt64Bits),
                    actual.Select(BitConverter.DoubleToUInt64Bits));
            }
        }
    }

    [Fact(DisplayName = "RF float64 chroma DC removal uses Numba fast-math mean")]
    public void RfFloat64ChromaDcRemovalUsesNumbaFastMathMean()
    {
        const int length = 32;
        var input = new double[length];
        input[0] = 1e20;
        input[8] = 1.0;
        input[16] = -1e20;
        input[24] = 1.0;
        Complex[] identity = RfDemodulator.IdentityFilter(length);
        double[] magnitudes = Enumerable.Repeat(1.0, length).ToArray();
        var filters = new DecodeFilterSet(
            identity,
            identity,
            identity,
            identity,
            identity,
            identity,
            null,
            magnitudes,
            magnitudes,
            magnitudes,
            magnitudes,
            magnitudes,
            magnitudes,
            null,
            ChromaBurst: identity,
            ChromaBurstMagnitude: magnitudes);

        Complex[] spectrum = PocketFftComplex.ForwardReal(input);
        for (int i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] *= identity[i];
        }

        double[] filtered = PocketFftComplex.Inverse(spectrum)
            .Select(value => value.Real)
            .ToArray();
        double[] expected = VhsChromaDecoder.ShiftChromaAndRemoveDc(filtered, move: 0);

        using var pipeline = new RfBlockDecodePipeline(
            new Pcm16StreamSampleLoader(),
            filters,
            sampleRateHz: 32.0);
        double[] actual = pipeline.DecodePreparedBlock(input).Demodulated.Chroma
            ?? throw new InvalidOperationException("Expected chroma output.");

        Assert.Equal(0xBFB0000000000000UL, BitConverter.DoubleToUInt64Bits(actual[1]));
        Assert.Equal(
            expected.Select(BitConverter.DoubleToUInt64Bits),
            actual.Select(BitConverter.DoubleToUInt64Bits));
    }

    private static double[] PinnedCurrentDoubleChromaShift(double[] input, int move)
    {
        double[] chroma = input.ToArray();
        if (chroma.Length == 0)
        {
            return chroma;
        }

        PinnedQuantizeToFloat32InPlace(chroma);
        int normalizedMove = ((move % chroma.Length) + chroma.Length) % chroma.Length;
        float[] wrapped = chroma
            .AsSpan(chroma.Length - normalizedMove, normalizedMove)
            .ToArray()
            .Select(value => (float)value)
            .ToArray();
        int firstWrappedIndex = chroma.Length - normalizedMove;
        double meanAccumulator = 0.0;
        for (int index = firstWrappedIndex - 1; index >= 0; index--)
        {
            meanAccumulator += chroma[index];
            chroma[index + normalizedMove] = chroma[index];
        }

        for (int index = 0; index < normalizedMove; index++)
        {
            meanAccumulator += wrapped[index];
            chroma[index] = wrapped[index];
        }

        meanAccumulator /= chroma.Length;
        for (int index = 0; index < chroma.Length; index++)
        {
            chroma[index] = (float)(chroma[index] - meanAccumulator);
        }

        return chroma;
    }

    private static unsafe void PinnedQuantizeToFloat32InPlace(Span<double> values)
    {
        int index = 0;
        if (Avx.IsSupported)
        {
            fixed (double* valuesPointer = values)
            {
                int vectorizedEnd = values.Length - (values.Length % 4);
                for (; index < vectorizedEnd; index += 4)
                {
                    Avx.Store(
                        valuesPointer + index,
                        Avx.ConvertToVector256Double(
                            Avx.ConvertToVector128Single(
                                Avx.LoadVector256(valuesPointer + index))));
                }
            }
        }

        for (; index < values.Length; index++)
        {
            values[index] = (float)values[index];
        }
    }
}
