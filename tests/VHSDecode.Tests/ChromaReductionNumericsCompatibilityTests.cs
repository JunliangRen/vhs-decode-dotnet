using System.Numerics;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ChromaReductionNumericsCompatibilityTests
{
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
