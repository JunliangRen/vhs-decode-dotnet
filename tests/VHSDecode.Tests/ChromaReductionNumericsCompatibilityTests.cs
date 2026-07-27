using System.Numerics;
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
}
