using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaU16SimdTests
{
    private static readonly double[] BoundaryValues =
    [
        double.NegativeInfinity,
        double.PositiveInfinity,
        double.NaN,
        BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001UL)),
        BitConverter.Int64BitsToDouble(0x7FF0_0000_0000_0001L),
        double.MinValue,
        -double.Epsilon,
        -0.0,
        0.0,
        double.Epsilon,
        -32768.0,
        -32767.0,
        Math.BitDecrement(-32767.0),
        Math.BitIncrement(-32767.0),
        -1.0,
        1.0,
        Math.BitDecrement(32768.0),
        32768.0,
        Math.BitIncrement(32768.0),
        -50_339.0,
        80_733.0,
        long.MinValue,
        long.MaxValue,
        double.MinValue,
        double.MaxValue
    ];

    [Theory(DisplayName = "VHS chroma uint16 SIMD matches scalar prefix and tail semantics")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(1_023)]
    [InlineData(1_024)]
    [InlineData(1_025)]
    public void VhsChromaU16SimdMatchesScalarPrefixAndTailSemantics(int length)
    {
        double[] input = BuildInput(length);

        ushort[] expected = ConvertScalar(input);
        ushort[] actual = VhsChromaDecoder.ChromaToU16(input);

        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "VHS chroma uint16 SIMD preserves arbitrary double bit patterns")]
    public void VhsChromaU16SimdPreservesArbitraryDoubleBitPatterns()
    {
        const int length = 65_539;
        var input = new double[length];
        ulong state = 0xD1B5_4A32_D192_ED03UL;
        for (int i = 0; i < input.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            input[i] = BitConverter.Int64BitsToDouble(unchecked((long)state));
        }

        for (int i = 0; i < BoundaryValues.Length; i++)
        {
            input[(i * 2_621) % input.Length] = BoundaryValues[i];
        }

        Assert.Equal(ConvertScalar(input), VhsChromaDecoder.ChromaToU16(input));
    }

    [Fact(DisplayName = "VHS chroma uint16 conversion fills a caller-owned destination")]
    public void VhsChromaU16ConversionFillsCallerOwnedDestination()
    {
        double[] input = BuildInput(65_539);
        var destination = new ushort[input.Length];

        VhsChromaDecoder.ChromaToU16(input, destination);
        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsChromaDecoder.ChromaToU16(input, destination);
        long callerOwnedAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        ushort[] allocated = VhsChromaDecoder.ChromaToU16(input);
        long allocatingPathBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(allocated, destination);
        Assert.True(
            callerOwnedAllocated < 4_096,
            $"Caller-owned VHS chroma conversion allocated {callerOwnedAllocated:N0} bytes.");
        Assert.True(
            allocatingPathBytes - callerOwnedAllocated >= input.Length * sizeof(ushort),
            $"Caller-owned VHS chroma output saved only "
                + $"{allocatingPathBytes - callerOwnedAllocated:N0} bytes.");
    }

    [Theory(DisplayName = "VHS automatic chroma gain fills a caller-owned destination")]
    [InlineData(false)]
    [InlineData(true)]
    public void VhsAutomaticChromaGainFillsCallerOwnedDestination(bool useComb)
    {
        const int lineLength = 64;
        const int lines = 20;
        double[] input = BuildInput(lineLength * lines);
        var destination = new ushort[input.Length];
        Array.Fill(destination, (ushort)0xDEAD);

        ushort[] expected = useComb
            ? VhsChromaDecoder.ApplyAutomaticChromaGainWithCombToU16(
                input,
                burstAbsRef: 50_000.0,
                burstStart: 8,
                burstEnd: 24,
                lineLength,
                lines,
                burstDetectedLine: 17,
                lineDistance: 1,
                retainFloat32: true,
                useFloat32Rms: true)
            : VhsChromaDecoder.ApplyAutomaticChromaGainToU16(
                input,
                burstAbsRef: 50_000.0,
                burstStart: 8,
                burstEnd: 24,
                lineLength,
                lines,
                burstDetectedLine: 17,
                useFloat32Rms: true);
        ushort[] actual = useComb
            ? VhsChromaDecoder.ApplyAutomaticChromaGainWithCombToU16(
                input,
                burstAbsRef: 50_000.0,
                burstStart: 8,
                burstEnd: 24,
                lineLength,
                lines,
                burstDetectedLine: 17,
                lineDistance: 1,
                retainFloat32: true,
                useFloat32Rms: true,
                output: destination)
            : VhsChromaDecoder.ApplyAutomaticChromaGainToU16(
                input,
                burstAbsRef: 50_000.0,
                burstStart: 8,
                burstEnd: 24,
                lineLength,
                lines,
                burstDetectedLine: 17,
                useFloat32Rms: true,
                output: destination);

        Assert.Same(destination, actual);
        Assert.Equal(expected, actual);
    }

    private static double[] BuildInput(int length)
    {
        var input = new double[length];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = i < BoundaryValues.Length
                ? BoundaryValues[i]
                : ((i * 104_729L) % 200_003L) - 100_001.0;
        }

        return input;
    }

    private static ushort[] ConvertScalar(ReadOnlySpan<double> chroma)
    {
        var output = new ushort[chroma.Length];
        int saturatingLength = chroma.Length & ~3;
        int index = 0;
        for (; index < saturatingLength; index++)
        {
            double shifted = chroma[index] + 32767.0;
            output[index] = !double.IsFinite(shifted) || shifted <= 0.0
                ? ushort.MinValue
                : shifted >= ushort.MaxValue
                    ? ushort.MaxValue
                    : (ushort)shifted;
        }

        for (; index < chroma.Length; index++)
        {
            double shifted = chroma[index] + 32767.0;
            output[index] = !double.IsFinite(shifted)
                || shifted < long.MinValue
                || shifted > long.MaxValue
                ? ushort.MinValue
                : unchecked((ushort)(long)shifted);
        }

        return output;
    }
}
