using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class HiFiHeadSwitchProcessorTests
{
    // Oracles execute v0.4.0 (43155200) headswitch_interpolate_boundaries and Numba
    // smooth with NumPy 2.4.6, SciPy 1.18.0 and Numba 0.66.0, including smoothing.
    public static TheoryData<string, int[], string> ExtrapolationCases => new()
    {
        { "leading", [0, 3], "421C0000 41E00000 41880000 40C00000 3FC00000 40155555 40C00000 C0E00000" },
        { "trailing", [5, 8], "3F800000 3F000000 40155555 BEAAAAAB C0A00000 C1800000 C1D80000 C2020000" },
        { "leading-masked-second-anchor", [0, 3, 4, 6], "41000000 40E00000 40D55555 40C00000 40AAAAAB 40AAAAAB 3F9C71C5 BF000000" },
        { "trailing-masked-second-anchor", [1, 4, 5, 8], "3F800000 BFA00000 C0000000 C0600000 C0A00000 C0D00000 C1000000 C10C0000" },
        { "both-edges", [0, 2, 6, 8], "C1900000 C1200000 C0000000 3F000000 40400000 41000000 41A80000 41DC0000" },
        { "leading-two-anchors", [0, 6], "428C0000 42400000 422A0000 42140000 41D00000 41700000 41180000 40800000" },
        { "trailing-two-anchors", [2, 8], "3F800000 40400000 40A00000 41100000 41200000 41300000 41400000 41500000" },
        { "negative-slice-mask", [-2, 2, 3, 8], "C0000000 C0000000 C0000000 C0E00000 C1400000 C1B00000 C1B00000 C1B00000" }
    };

    [Theory]
    [MemberData(nameof(ExtrapolationCases))]
    public void ExactEdgeExtrapolationMatchesV040FloatBits(
        string scenario, int[] endpoints, string expectedBits)
    {
        Assert.NotEmpty(scenario);
        float[] audio = [1, 3, -2, 6, -5, 8, 4, -7];
        HiFiSampleRange[] boundaries = Enumerable.Range(0, endpoints.Length / 2)
            .Select(index => new HiFiSampleRange(endpoints[index * 2], endpoints[(index * 2) + 1]))
            .ToArray();

        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(audio, boundaries);

        Assert.Equal(expectedBits, Bits(output));
        Assert.Equal<float>([1, 3, -2, 6, -5, 8, 4, -7], audio);
    }

    [Theory]
    [InlineData(0, 3, "DFF9CCD9 DFBB59A3 DF79CCD9 DEF9CCD9 3FABF854 4580A0F4 45C0E68E C7800040")]
    [InlineData(5, 8, "60AD78EC 3F1E064B DE268891 DE268891 00000000 5EF9CCD9 5F79CCD9 5F9C2008")]
    public void ExactEdgeExtrapolationPreservesV040MixedMagnitudeArithmetic(
        int start, int end, string expectedBits)
    {
        uint[] inputBits = [0x60AD78EC, 0x9F0DABC6, 0x3F9E064B, 0xDEF9CCD9,
            0x402DF854, 0xBD000000, 0x4640E6AE, 0xC7800040];
        float[] audio = inputBits.Select(BitConverter.UInt32BitsToSingle).ToArray();

        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(
            audio, [new HiFiSampleRange(start, end)]);

        Assert.Equal(expectedBits, Bits(output));
    }

    [Theory]
    [InlineData(7.0, 9.0, 0, 16, "C810D8EE756CD637300DF169175F47D64F3702FAD432F478800596E6F1444FDA")]
    [InlineData(23.0, 25.0, 16, 32, "0C43F2957858EF1A2EE3E2CEC548164D548995C05A42C6588927998CD6DD10D7")]
    public void DetectedPeakPaddingAtExactBlockEdgeMatchesV040FloatBits(
        double peakStart, double peakEnd, int gapStart, int gapEnd, string expectedHash)
    {
        var processor = new HiFiHeadSwitchProcessor(192_000, 59.94);
        List<HiFiSampleRange> boundaries = processor.CalculateBoundaries(
            [new HiFiHeadSwitchPeak((int)peakStart + 1, peakStart, peakEnd, 1.0)]);
        Assert.Equal(new HiFiSampleRange(gapStart, gapEnd), Assert.Single(boundaries));
        float[] audio = Enumerable.Range(0, 32).Select(index => (float)index).ToArray();

        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(audio, boundaries);

        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
    }

    [Fact]
    public void GapBeyondBlockStartRetainsV040SampleAndHold()
    {
        float[] audio = [1, 2, 3, 4, 5, 6, 7, 8];

        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(
            audio, [new HiFiSampleRange(-2, 3)]);

        Assert.Equal<float>([4, 4, 4, 4, 5, 6, 7, 8], output);
    }

    [Fact]
    public void GapBeyondBlockEndRetainsV040SampleAndHold()
    {
        float[] audio = [1, 2, 3, 4, 5, 6, 7, 8];

        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(
            audio, [new HiFiSampleRange(5, 10)]);

        Assert.Equal<float>([1, 2, 3, 4, 5, 6, 6, 6], output);
    }

    // Upstream has NaNs or exceptions without two valid anchors. Preserve the
    // source audio in these degenerate cases instead of inventing an extrapolation.
    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 8)]
    [InlineData(0, 8)]
    [InlineData(-2, 8)]
    [InlineData(0, 10)]
    [InlineData(-2, 10)]
    public void GapWithInsufficientAnchorsPreservesAudio(int start, int end)
    {
        float[] audio = [1, 2, 3, 4, 5, 6, 7, 8];

        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(
            audio, [new HiFiSampleRange(start, end)]);

        Assert.Equal(audio, output);
        Assert.NotSame(audio, output);
    }

    [Theory]
    [InlineData(-8, -2)]
    [InlineData(8, 14)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    public void EmptyOrNonIntersectingGapPreservesAudio(int start, int end)
    {
        float[] audio = [1, 3, -2, 6, -5, 8, 4, -7];

        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(
            audio, [new HiFiSampleRange(start, end)]);

        Assert.Equal(audio, output);
    }

    [Fact]
    public void EmptyAudioWithGapRemainsEmpty()
    {
        float[] output = HiFiHeadSwitchProcessor.InterpolateBoundaries(
            [], [new HiFiSampleRange(-2, 3)]);

        Assert.Empty(output);
    }

    private static string Bits(IEnumerable<float> values)
        => string.Join(" ", values.Select(value => BitConverter.SingleToUInt32Bits(value).ToString("X8")));
}
