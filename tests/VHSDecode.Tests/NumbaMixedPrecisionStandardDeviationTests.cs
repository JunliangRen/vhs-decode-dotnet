using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class NumbaMixedPrecisionStandardDeviationTests
{
    [Theory(DisplayName = "Numba mixed-precision std preserves scalar bits for raw doubles, tails and offsets")]
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
    [InlineData(257)]
    [InlineData(1_024)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    public void PreservesScalarBitsWithTailsAndOffsets(int length)
    {
        Check(BuildInput(length, quantized: false));
        Check(BuildInput(length, quantized: true));
    }

    [Theory(DisplayName = "Numba mixed-precision std retains nominal LD field-span bits")]
    [InlineData(951_360)]
    [InlineData(983_072)]
    public void RetainsNominalFieldSpanBits(int length)
    {
        // Default 40MHz NTSC read-window alignment can span 30 or 31 LD strides.
        Check(BuildInput(length, quantized: false));
        Check(BuildInput(length, quantized: true));
    }

    [Fact(DisplayName = "Numba mixed-precision std retains original float mean and ordered double variance")]
    public void RetainsMeanAndVarianceRoundingOrder()
    {
        Check(Repeat(257, [16_777_216.0, 1.0, -16_777_216.0, 1.0]));
        Check(Repeat(257, [100_000_000.0, -100_000_000.0, 0.00000001, -0.00000001]));
        Check(Repeat(4_097,
        [
            1.0,
            Math.BitIncrement(1.0),
            1.0 + Math.ScaleB(1.0, -24),
            Math.BitDecrement(1.0 + Math.ScaleB(1.0, -24)),
            Math.BitIncrement(1.0 + Math.ScaleB(1.0, -24))
        ]));
        Check([7_592_572.0, 7_592_572.5, -7_592_572.0, -7_592_572.5, 0.125]);
    }

    [Fact(DisplayName = "Numba mixed-precision std retains float intermediate overflow")]
    public void RetainsFloatIntermediateOverflow()
    {
        Check(Enumerable.Repeat((double)float.MaxValue, 9).ToArray());
        Check(Enumerable.Repeat(-(double)float.MaxValue, 9).ToArray());
        // The mean is finite here while float products overflow to positive infinity.
        // Replacing float products with double products would change this result.
        double[] productOverflow = Repeat(8, [(double)float.MaxValue / 8.0, -(double)float.MaxValue / 8.0]);
        Assert.Equal(double.PositiveInfinity, NumbaReduction.StandardDeviationFloat32InputToFloat64(productOverflow));
        Check(productOverflow);
        Check([double.MaxValue, -double.MaxValue, 1.0, 2.0]);
    }

    [Theory(DisplayName = "Numba mixed-precision std preserves special-value classification and signed zeros")]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(257)]
    public void PreservesSpecialValuesAndSourceBits(int length)
    {
        Check(new double[length]);
        Check(Enumerable.Repeat(-0.0, length).ToArray());
        Check(Repeat(length, [0.0, -0.0, -0.0, 0.0]));
        Check(Repeat(length,
            [double.Epsilon, -double.Epsilon, (double)float.Epsilon, -(double)float.Epsilon, 0.0, -0.0]));

        ulong[] specialBits =
        [
            0x7FF8_2468_AC00_0001UL,
            0xFFF8_5432_1000_0001UL,
            0x7FF0_1234_5678_9ABCUL,
            0xFFF0_4321_8765_DCBAUL,
            0x7FF0_0000_0000_0000UL,
            0xFFF0_0000_0000_0000UL,
            0x7FEF_FFFF_FFFF_FFFFUL,
            0xFFEF_FFFF_FFFF_FFFFUL
        ];
        foreach (ulong bits in specialBits)
        {
            foreach (int position in new[] { 0, 1, length / 2, length - 1 }.Distinct())
            {
                double[] values = BuildInput(length, quantized: false);
                values[position] = BitConverter.UInt64BitsToDouble(bits);
                Check(values);
            }
        }
        Check(Repeat(length,
        [
            1.0,
            BitConverter.UInt64BitsToDouble(specialBits[0]),
            -0.0,
            BitConverter.UInt64BitsToDouble(specialBits[1]),
            double.NegativeInfinity,
            double.PositiveInfinity
        ]));
    }

    [Fact(DisplayName = "Empty mixed-precision std preserves the platform scalar NaN bits")]
    public void EmptyInputPreservesPlatformScalarNanBits()
    {
        double expected = ScalarReference([]);
        double actual = NumbaReduction.StandardDeviationFloat32InputToFloat64([]);
        Assert.True(double.IsNaN(expected));
        Assert.Equal(BitConverter.DoubleToUInt64Bits(expected), BitConverter.DoubleToUInt64Bits(actual));
    }

    private static void Check(double[] input)
    {
        const int offset = 5;
        var guarded = new double[input.Length + (2 * offset)];
        Array.Fill(guarded, BitConverter.UInt64BitsToDouble(0xFFE4_1122_3344_5566UL));
        input.CopyTo(guarded, offset);
        double[] before = guarded.ToArray();
        ReadOnlySpan<double> values = guarded.AsSpan(offset, input.Length);
        double expected = ScalarReference(values);
        double actual = NumbaReduction.StandardDeviationFloat32InputToFloat64(values);
        if (input.Length > 0 && double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual));
            // The frozen old helper itself can choose another NaN sign/payload
            // across JIT call sites. Retain the raw diagnostic rather than hiding it.
            if (BitConverter.DoubleToUInt64Bits(expected) != BitConverter.DoubleToUInt64Bits(actual))
            {
                Console.WriteLine($"Std NaN payload diagnostic n={input.Length}: "
                    + $"scalar={BitConverter.DoubleToUInt64Bits(expected):X16}, "
                    + $"actual={BitConverter.DoubleToUInt64Bits(actual):X16}.");
            }
        }
        else
        {
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected), BitConverter.DoubleToUInt64Bits(actual));
        }
        Assert.True(MemoryMarshal.AsBytes(before.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(guarded.AsSpan())),
            "Mixed-precision standard deviation changed its input or guard values.");
    }

    // Independent former implementation: both accumulation chains stay sequential.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double ScalarReference(ReadOnlySpan<double> values)
    {
        float mean = 0.0f;
        for (int i = 0; i < values.Length; i++)
        {
            mean += (float)values[i];
        }
        mean /= values.Length;

        double squaredDistanceSum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            float distance = (float)values[i] - mean;
            squaredDistanceSum += distance * distance;
        }
        return Math.Sqrt(squaredDistanceSum / values.Length);
    }

    private static double[] Repeat(int length, double[] pattern)
        => Enumerable.Range(0, length).Select(index => pattern[index % pattern.Length]).ToArray();

    private static double[] BuildInput(int length, bool quantized)
    {
        var input = new double[length];
        uint state = 0xA1C3_7E59;
        for (int index = 0; index < input.Length; index++)
        {
            state = unchecked((state * 1_664_525U) + 1_013_904_223U);
            double value = (unchecked((int)state) / 2_147_483_648.0) * 0.75
                + (index % 7) * Math.ScaleB(1.0, -30);
            input[index] = quantized ? (double)(float)value : value;
        }
        return input;
    }
}
