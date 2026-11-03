using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class DemodVideoClipperTests
{
    private delegate double[] ClipFactory(ReadOnlySpan<double> input, double sampleRateHz);
    private delegate void FillClip(ReadOnlySpan<double> input, Span<double> output, double upper);
    private static readonly ClipFactory Factory = typeof(RfDemodulator)
        .GetMethod("ClipDemodForVideo", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<ClipFactory>();
    private static readonly FillClip Fill = typeof(DemodVideoClipper)
        .GetMethod("Fill", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<FillClip>();

    [Theory(DisplayName = "Video demod clipping retains original f64 bits, offsets and scalar tails")]
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
    [InlineData(16_384)]
    [InlineData(32_767)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    [InlineData(65_536)]
    public void RetainsScalarBitsAndSourceGuards(int length)
    {
        foreach (int pattern in new[] { 0, 1, 2 })
        {
            double[] source = BuildInput(length + 5, pattern);
            double[] before = source.ToArray();
            double[] expected = ScalarReference(source.AsSpan(1, length), 40_000_000.0);
            EqualBits(expected, DemodVideoClipper.Create(source.AsSpan(1, length), 40_000_000.0));
            EqualBits(expected, Factory(source.AsSpan(1, length), 40_000_000.0));
            EqualBits(before, source);

            // The production factory uses a fresh, same-length destination. Poison
            // proves that every element is written, including non-vector tails.
            double[] guarded = Enumerable.Repeat(Poison, length + 7).ToArray();
            double[] wanted = guarded.ToArray();
            expected.CopyTo(wanted, 3);
            Fill(source.AsSpan(1, length), guarded.AsSpan(3, length), 30_000_000.0);
            EqualBits(wanted, guarded);
            EqualBits(before, source);
        }
    }

    [Fact(DisplayName = "Video demod clipping retains exact f64 endpoints and original NaN payloads")]
    public void RetainsFullPrecisionEndpointsAndSpecialValues()
    {
        const double minimum = 1_500_000.0, maximum = 30_000_000.0;
        double[] nan =
        [
            BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0123UL),
            BitConverter.UInt64BitsToDouble(0xFFF8_0000_0000_0456UL),
            BitConverter.UInt64BitsToDouble(0x7FF0_0000_0000_0123UL),
            BitConverter.UInt64BitsToDouble(0xFFF0_0000_0000_0456UL)
        ];
        double precise = minimum + Math.ScaleB(1.0, -30);
        double[] input = [nan[0], nan[1], nan[2], nan[3], -0.0, 0.0, double.Epsilon, -double.Epsilon,
            double.NegativeInfinity, double.PositiveInfinity, double.MinValue, double.MaxValue,
            Math.BitDecrement(minimum), minimum, Math.BitIncrement(minimum), precise,
            Math.BitDecrement(maximum), maximum, Math.BitIncrement(maximum)];
        double[] expected = [nan[0], nan[1], nan[2], nan[3], minimum, minimum, minimum, minimum,
            minimum, maximum, minimum, maximum, minimum, minimum, Math.BitIncrement(minimum), precise,
            Math.BitDecrement(maximum), maximum, maximum];
        EqualBits(expected, DemodVideoClipper.Create(input, 40_000_000.0));
        EqualBits(expected, Factory(input, 40_000_000.0));
        Assert.NotEqual(BitConverter.DoubleToUInt64Bits((float)precise), BitConverter.DoubleToUInt64Bits(precise));
    }

    [Fact(DisplayName = "NaN upper thresholds retain the original ordered Math.Clamp branches")]
    public void NaNUpperThresholdPreservesOrderedBranches()
    {
        ulong[] nanRates = [0x7FF8_0000_0000_0123UL, 0xFFF8_0000_0000_0456UL,
            0x7FF0_0000_0000_0123UL, 0xFFF0_0000_0000_0456UL];
        double[] input = BuildInput(33, 2);
        double[] before = input.ToArray();
        double[] expected = input.Select(value => value < 1_500_000.0 ? 1_500_000.0 : value).ToArray();
        foreach (ulong bits in nanRates)
        {
            double rate = BitConverter.UInt64BitsToDouble(bits);
            EqualBits(expected, ScalarReference(input, rate));
            EqualBits(expected, DemodVideoClipper.Create(input, rate));
            EqualBits(expected, Factory(input, rate));
        }
        EqualBits(before, input);
    }

    [Fact(DisplayName = "Video clipping preserves equal and adjacent rate-derived bounds")]
    public void RetainsRateDerivedBoundaryRounding()
    {
        double[] input = BuildInput(33, 2);
        foreach (double rate in new[] { 2_000_000.0, Math.BitIncrement(2_000_000.0),
            32_000_000.0, double.MaxValue, double.PositiveInfinity })
        {
            double[] expected = ScalarReference(input, rate);
            EqualBits(expected, DemodVideoClipper.Create(input, rate));
            EqualBits(expected, Factory(input, rate));
        }
        foreach (double invalid in new[] { Math.BitDecrement(2_000_000.0), -0.0 })
        {
            AssertSameException(() => ScalarReference(input, invalid), () => DemodVideoClipper.Create(input, invalid));
            AssertSameException(() => ScalarReference(input, invalid), () => Factory(input, invalid));
        }
    }

    [Theory(DisplayName = "Invalid video clipping rates retain original exceptions and empty no-op")]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1_000_000.0)]
    [InlineData(1_999_999.0)]
    [InlineData(double.Epsilon)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MinValue)]
    public void InvalidRatesRetainScalarExceptionsAndEmptyBehavior(double rate)
    {
        foreach (int length in new[] { 0, 1, 3, 4, 5, 17 })
        {
            double[] input = BuildInput(length, 2);
            double[] before = input.ToArray();
            AssertSameException(() => ScalarReference(input, rate), () => DemodVideoClipper.Create(input, rate));
            AssertSameException(() => ScalarReference(input, rate), () => Factory(input, rate));
            if (length == 0)
            {
                Assert.Empty(DemodVideoClipper.Create(input, rate));
                Assert.Empty(Factory(input, rate));
            }
            EqualBits(before, input);
        }
    }

    [Fact(DisplayName = "Empty video clipping accepts every threshold without an eager Clamp")]
    public void EmptyInputRemainsAValidNoOp()
    {
        foreach (double rate in new[] { double.NegativeInfinity, -0.0, 0.0,
            double.NaN, 2_000_000.0, 40_000_000.0, double.PositiveInfinity })
        {
            Assert.Empty(DemodVideoClipper.Create(default, rate));
            Assert.Empty(Factory(default, rate));
        }
    }

    [Theory(DisplayName = "Video clipping retains frozen original Core f64 output hashes")]
    [InlineData(32_768, "1F6C09C43EB99DDEB428A3F5045C4FF5E829E4DE60142A33EFADFF857D8144DD")]
    [InlineData(65_536, "129E4FC6A6C6707B97CE1D0F587B99EAEDC75C7D9E9757CC02E6AC06B69375F8")]
    public void RetainsFrozenOriginalOutputHashes(int length, string hash)
    {
        // Original v4 Core 40F29C... / MVID e53638d9..., before this helper.
        // Integer-exact finite fixtures have no platform libm dependency.
        double[] input = BuildInput(length, 0);
        double[] output = DemodVideoClipper.Create(input, 40_000_000.0);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
    }

    [Fact(DisplayName = "Video clipping outputs stay independent of later calls and input mutations")]
    public void OwnsItsOutput()
    {
        double[] input = BuildInput(17, 2);
        double[] expected = ScalarReference(input, 40_000_000.0);
        double[] first = DemodVideoClipper.Create(input, 40_000_000.0);
        double[] second = DemodVideoClipper.Create(input, 40_000_000.0);
        Assert.NotSame(input, first);
        Assert.NotSame(first, second);
        input[0] = 123.0;
        EqualBits(expected, first);
        EqualBits(expected, second);
        first[1] = 456.0;
        EqualBits(expected, second);
    }

    [Fact(DisplayName = "Parallel video clipping retains independent complete outputs")]
    public void ParallelOutputsRemainIndependent()
    {
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker =>
        {
            double[] input = BuildInput(1_023 + worker, worker % 3);
            double[] expected = ScalarReference(input, 40_000_000.0);
            double[] retained = DemodVideoClipper.Create(input, 40_000_000.0);
            _ = DemodVideoClipper.Create(BuildInput(32_769, 2), 32_000_000.0);
            EqualBits(expected, retained);
        });
    }

    private static void AssertSameException(Func<double[]> expectedAction, Func<double[]> actualAction)
    {
        Exception? expected = Record.Exception(() => { _ = expectedAction(); });
        Exception? actual = Record.Exception(() => { _ = actualAction(); });
        Assert.Equal(expected?.GetType(), actual?.GetType());
        Assert.Equal(expected?.Message, actual?.Message);
        Assert.Equal((expected as ArgumentException)?.ParamName, (actual as ArgumentException)?.ParamName);
    }

    // Original private ClipDemodForVideo, including upper calculation and allocation order.
    private static double[] ScalarReference(ReadOnlySpan<double> demod, double sampleRateHz)
    {
        double upper = sampleRateHz * 0.75;
        var output = new double[demod.Length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = Math.Clamp(demod[i], 1_500_000.0, upper);
        }

        return output;
    }

    private static double[] BuildInput(int length, int pattern)
    {
        double[] edges = [1_500_000.0, Math.BitDecrement(1_500_000.0), Math.BitIncrement(1_500_000.0),
            30_000_000.0, Math.BitDecrement(30_000_000.0), Math.BitIncrement(30_000_000.0),
            -0.0, 0.0, double.Epsilon, -double.Epsilon, double.MaxValue, -double.MaxValue,
            double.PositiveInfinity, double.NegativeInfinity,
            BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0123UL),
            BitConverter.UInt64BitsToDouble(0xFFF8_0000_0000_0456UL),
            BitConverter.UInt64BitsToDouble(0x7FF0_0000_0000_0123UL),
            BitConverter.UInt64BitsToDouble(0xFFF0_0000_0000_0456UL)];
        var input = new double[length];
        ulong state = 0xD1B5_4A32_D192_ED03UL;
        for (int index = 0; index < input.Length; index++)
        {
            state = unchecked(state * 2862933555777941757UL + 3037000493UL);
            input[index] = pattern switch
            {
                0 => 8_000_000.0 + ((state >> 16 & 0xFFFF) - 32_768.0) * 17.0,
                1 => BitConverter.UInt64BitsToDouble(state),
                _ => edges[index % edges.Length]
            };
        }
        return input;
    }

    private static double Poison => BitConverter.UInt64BitsToDouble(0xFFF8_1234_5678_9ABCUL);
    private static void EqualBits(ReadOnlySpan<double> expected, ReadOnlySpan<double> actual)
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "Video clipping changed a raw f64 bit, NaN payload, scalar tail or guard value.");
}
