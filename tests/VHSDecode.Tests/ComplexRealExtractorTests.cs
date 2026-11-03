using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class ComplexRealExtractorTests
{
    private delegate double[] CreateReal(ReadOnlySpan<Complex> input);
    private delegate void FillReal(ReadOnlySpan<Complex> input, Span<double> output);

    [Theory(DisplayName = "Complex real extraction preserves every raw f64 bit, offset and tail")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1_023)]
    [InlineData(1_024)]
    [InlineData(16_384)]
    [InlineData(32_767)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    [InlineData(65_536)]
    public void RetainsRawBitsWithOffsetsAndTails(int length)
    {
        Assert.Equal(16, Unsafe.SizeOf<Complex>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Complex>());
        foreach (int pattern in new[] { 0, 1, 2 })
        {
            Complex[] source = BuildInput(length + 3, pattern);
            Complex[] before = source.ToArray();
            Check(source.AsSpan(1, length));
            EqualBytes(before, source);

            // Deliberately start at an odd-double address, not a whole Complex offset.
            double[] raw = Enumerable.Repeat(Poison, (2 * length) + 3).ToArray();
            Span<Complex> unaligned = MemoryMarshal.Cast<double, Complex>(raw.AsSpan(1, 2 * length));
            source.AsSpan(1, length).CopyTo(unaligned);
            double[] rawBefore = raw.ToArray();
            Check(unaligned);
            EqualBits(rawBefore, raw);
        }
    }

    [Fact(DisplayName = "Complex extraction factory keeps original f64 precision and zero tails")]
    public void RetainsFullPrecisionAndZeroTail()
    {
        Complex[] source =
        [
            new(1.0 + Math.ScaleB(1.0, -40), double.NaN),
            new(Math.BitIncrement(1.0), double.PositiveInfinity),
            new(double.Epsilon, double.NegativeInfinity),
            new(-0.0, double.NaN),
            Complex.Zero,
            Complex.Zero,
            Complex.Zero
        ];
        double[] actual = ComplexRealExtractor.Create(source);
        EqualBits(ScalarReference(source), actual);
        Assert.NotEqual(BitConverter.DoubleToUInt64Bits((float)actual[0]), BitConverter.DoubleToUInt64Bits(actual[0]));
        Assert.NotEqual(BitConverter.DoubleToUInt64Bits((float)actual[2]), BitConverter.DoubleToUInt64Bits(actual[2]));
        Assert.Equal(0x8000_0000_0000_0000UL, BitConverter.DoubleToUInt64Bits(actual[3]));
        foreach (double value in actual.AsSpan(4)) Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(value));
    }

    [Theory(DisplayName = "Existing destination extraction writes only the active span")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(17)]
    public void DestinationExtractionKeepsInactiveTail(int count)
    {
        FillReal copy = typeof(RfDemodulator)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "ExtractReal" && method.GetParameters().Length == 2)
            .CreateDelegate<FillReal>();
        Complex[] source = BuildInput(count + 9, 2);
        double[] destination = Enumerable.Repeat(Poison, count + 11).ToArray();
        double[] expected = destination.ToArray();
        double[] active = ScalarReference(source.AsSpan(2, count));
        active.CopyTo(expected, 3);
        copy(source.AsSpan(2, count), destination.AsSpan(3, count));
        EqualBits(expected, destination);
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => copy(source.AsSpan(2, count), destination.AsSpan(3, count + 1)));
        Assert.Equal("output", error.ParamName);
    }

    [Fact(DisplayName = "Complex extraction outputs stay independent across calls and input mutations")]
    public void OwnsItsOutput()
    {
        Complex[] input = BuildInput(17, 2);
        double[] expected = ScalarReference(input);
        double[] first = ComplexRealExtractor.Create(input);
        double[] second = ComplexRealExtractor.Create(input);
        Assert.NotSame(first, second);
        input[0] = new Complex(913.0, -7.0);
        EqualBits(expected, first);
        EqualBits(expected, second);
        first[1] = 12345.0;
        EqualBits(expected, second);
    }

    [Fact(DisplayName = "Parallel complex extraction retains independent output buffers")]
    public void ParallelOutputsRemainIndependent()
    {
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker =>
        {
            Complex[] input = BuildInput(1_023 + worker, worker % 3);
            double[] expected = ScalarReference(input);
            double[] retained = ComplexRealExtractor.Create(input);
            _ = ComplexRealExtractor.Create(BuildInput(32_769, 2));
            EqualBits(expected, retained);
        });
    }

    private static void Check(ReadOnlySpan<Complex> input)
    {
        Complex[] before = input.ToArray();
        double[] expected = ScalarReference(input);
        double[] actual = ComplexRealExtractor.Create(input);
        Assert.Equal(input.Length, actual.Length);
        EqualBits(expected, actual);

        CreateReal factory = typeof(RfDemodulator)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "ExtractReal" && method.GetParameters().Length == 1)
            .CreateDelegate<CreateReal>();
        EqualBits(expected, factory(input));

        // The factory never exposes uninitialized data. Exercise its private copy
        // with poison to prove every slot is written, including short scalar tails.
        FillReal fill = typeof(ComplexRealExtractor)
            .GetMethod("Fill", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<FillReal>();
        double[] guarded = Enumerable.Repeat(Poison, input.Length + 7).ToArray();
        double[] guardedExpected = guarded.ToArray();
        expected.CopyTo(guardedExpected, 3);
        fill(input, guarded.AsSpan(3, input.Length));
        EqualBits(guardedExpected, guarded);
        EqualBytes(before, input);
    }

    private static double Poison => BitConverter.UInt64BitsToDouble(0xFFF8_1234_5678_9ABCUL);

    private static double[] ScalarReference(ReadOnlySpan<Complex> input)
    {
        var output = new double[input.Length];
        for (int index = 0; index < input.Length; index++) output[index] = input[index].Real;
        return output;
    }

    private static Complex[] BuildInput(int length, int pattern)
    {
        ulong[] special =
        [
            0x0000_0000_0000_0000UL, 0x8000_0000_0000_0000UL,
            0x0000_0000_0000_0001UL, 0x8000_0000_0000_0001UL,
            0x0010_0000_0000_0000UL, 0x8010_0000_0000_0000UL,
            0x7FEF_FFFF_FFFF_FFFFUL, 0xFFEF_FFFF_FFFF_FFFFUL,
            0x7FF0_0000_0000_0000UL, 0xFFF0_0000_0000_0000UL,
            0x7FF8_0000_0000_0123UL, 0xFFF8_0000_0000_0456UL,
            0x7FF0_0000_0000_0123UL, 0xFFF0_0000_0000_0456UL
        ];
        var output = new Complex[length];
        ulong state = 0xD1B5_4A32_D192_ED03UL;
        for (int index = 0; index < output.Length; index++)
        {
            state = unchecked(state * 2862933555777941757UL + 3037000493UL);
            double real = pattern == 0 ? ((state >> 16 & 0xFFFF) - 32_768.0)
                : BitConverter.UInt64BitsToDouble(pattern == 1 ? state : special[index % special.Length]);
            state = unchecked(state * 2862933555777941757UL + 3037000493UL);
            output[index] = new Complex(real, BitConverter.UInt64BitsToDouble(state));
        }
        return output;
    }

    private static void EqualBits(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual)
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Real extraction changed an original f64 bit, including NaN sign/payload or signed zero.");

    private static void EqualBytes(ReadOnlySpan<Complex> expected, ReadOnlySpan<Complex> actual)
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Real extraction changed its complex input or guard storage.");
}
