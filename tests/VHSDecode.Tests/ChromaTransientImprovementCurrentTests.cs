using System.Buffers.Binary;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ChromaTransientImprovementCurrentTests
{
    private const string InputHash =
        "0B924EF9F271460D79EF3D5912090BC766323DE6F4931B571CAD86FCC7BDE222";

    [Theory(DisplayName = "Current CTI matches pinned PR 341 Numba output")]
    [InlineData(
        0,
        2,
        1.0,
        1.25,
        "677BA461A9412F4136D80E7C9390472B7A6E7EAF2DC77B7244CF5DD98CCBB458")]
    [InlineData(
        64,
        3,
        0.35,
        0.0,
        "365DF3319847F3AAE73F83E1312BA2928C3D23425C740E72113DF4222E413E7A")]
    [InlineData(
        192,
        0,
        1.0,
        2.5,
        "A9B1F877BB6349A5E82F4A0C49DCF101994B0C7FE26D1B2041F510CBF4CA3CE7")]
    [InlineData(
        128,
        20,
        0.5,
        0.75,
        InputHash)]
    public void CurrentCtiMatchesPinnedNumbaOutput(
        int lineStart,
        long width,
        double mix,
        double noiseFloor,
        string expectedHash)
    {
        double[] samples = BuildInput();
        Assert.Equal(InputHash, Float32BitsSha256(samples));

        ChromaTransientImprovement.ApplyInPlace(
            samples,
            lineStart,
            lineLength: 64,
            noiseFloor,
            width,
            mix);

        Assert.Equal(expectedHash, Float32BitsSha256(samples));
    }

    [Theory(DisplayName = "Current CTI preserves pinned negative-width behavior")]
    [InlineData(-1)]
    [InlineData(-4)]
    public void CurrentCtiPreservesPinnedNegativeWidthBehavior(long width)
    {
        double[] samples = BuildInput();

        ChromaTransientImprovement.ApplyInPlace(
            samples,
            lineStart: 0,
            lineLength: 64,
            baseNoiseFloor: 1.0,
            width,
            mix: 1.0);

        Assert.Equal(InputHash, Float32BitsSha256(samples));
    }

    [Theory(DisplayName = "Disabled current CTI preserves float64 samples bit for bit")]
    [InlineData(-1, 1.0)]
    [InlineData(2, 0.0)]
    public void DisabledCurrentCtiPreservesFloat64SamplesBitForBit(
        long width,
        double mix)
    {
        double[] samples = BuildFloat64Input();
        long[] expectedBits = samples
            .Select(BitConverter.DoubleToInt64Bits)
            .ToArray();

        ChromaTransientImprovement.ApplyInPlace(
            samples,
            lineStart: 0,
            lineLength: 64,
            baseNoiseFloor: 1.0,
            width,
            mix);

        Assert.Equal(
            expectedBits,
            samples.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact(DisplayName = "Parallel current CTI matches serial output bit for bit")]
    public void ParallelCurrentCtiMatchesSerialOutputBitForBit()
    {
        double[] expected = BuildInput();
        double[] actual = (double[])expected.Clone();

        ChromaTransientImprovement.ApplyInPlace(
            expected,
            lineStart: 64,
            lineLength: 64,
            baseNoiseFloor: 1.25,
            width: 2,
            mix: 1.0);
        ChromaTransientImprovement.ApplyInPlace(
            actual,
            lineStart: 64,
            lineLength: 64,
            baseNoiseFloor: 1.25,
            width: 2,
            mix: 1.0,
            workerThreads: 4);

        Assert.Equal(
            expected.Select(BitConverter.DoubleToInt64Bits),
            actual.Select(BitConverter.DoubleToInt64Bits));
    }

    [Theory(DisplayName = "Pinned CTI reciprocal estimate is CPU independent")]
    [InlineData(0x427564F4u, 0x3C858800u)]
    [InlineData(0x42C24200u, 0x3C28A800u)]
    [InlineData(0x4391B180u, 0x3B60E000u)]
    [InlineData(0x411BD000u, 0x3DD24000u)]
    [InlineData(0x00000001u, 0x7F800000u)]
    [InlineData(0x7F000000u, 0x00000000u)]
    [InlineData(0x7FC12345u, 0x7FC12345u)]
    [InlineData(0x807FFFFFu, 0xFF800000u)]
    public void PinnedCurrentCtiReciprocalEstimateIsCpuIndependent(
        uint inputBits,
        uint expectedBits)
    {
        float input = BitConverter.UInt32BitsToSingle(inputBits);

        float actual =
            ChromaTransientImprovement.PinnedReciprocalEstimate(input);

        Assert.Equal(expectedBits, BitConverter.SingleToUInt32Bits(actual));
    }

    private static double[] BuildInput()
    {
        var samples = new double[8 * 64];
        for (int index = 0; index < samples.Length; index++)
        {
            int integer = ((index * 7_919 + 104_729) % 65_521) - 32_760;
            samples[index] = (float)integer * 0.01f;
        }

        return samples;
    }

    private static double[] BuildFloat64Input()
    {
        var samples = new double[2 * 64];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = (index * 0.125) + (1.0 / 3.0);
        }

        return samples;
    }

    private static string Float32BitsSha256(ReadOnlySpan<double> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(int))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(int), sizeof(int)),
                BitConverter.SingleToInt32Bits((float)values[index]));
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
