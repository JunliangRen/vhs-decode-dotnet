using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftDuccOwnershipTests
{
    [Theory(DisplayName = "In-place DUCC forward retains allocating defined bits and avoids output allocation")]
    [InlineData(2)]
    [InlineData(1_024)]
    [InlineData(16_384)]
    [InlineData(32_768)]
    [InlineData(65_536)]
    public void InPlaceDuccForwardRetainsAllocatingBits(int length)
    {
        var input = new Complex[length];
        uint state = 0x51DE_F00D;
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            double real = unchecked((int)state) / 65_536.0;
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            double imaginary = unchecked((int)state) / 65_536.0;
            input[i] = new Complex(real, imaginary);
        }
        input[0] = new Complex(0.0, -0.0);
        input[length / 2] = new Complex(-0.0, 0.0);
        Check(input);
        Check(new Complex[length]);
        input[0] = new Complex(BitConverter.UInt64BitsToDouble(0x7ff8_0000_0000_0123), double.PositiveInfinity);
        input[^1] = new Complex(double.NegativeInfinity, BitConverter.UInt64BitsToDouble(0xfff8_0000_0000_0456));
        Check(input, allowComputedNaNVariation: true);

        var disposable = new Complex[length];
        for (int warmup = 0; warmup < 8; warmup++)
        {
            PocketFftComplex.ForwardDuccInPlace(disposable);
        }
        // Observe steady-state allocation after warming the measured input path;
        // one-off runner/tiering bookkeeping must not be charged to FFT scratch.
        long minimumAllocatedBytes = long.MaxValue;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            PocketFftComplex.ForwardDuccInPlace(disposable);
            minimumAllocatedBytes = Math.Min(minimumAllocatedBytes,
                GC.GetAllocatedBytesForCurrentThread() - start);
        }
        Assert.True(minimumAllocatedBytes < 256,
            $"Warmed in-place forward allocated at least {minimumAllocatedBytes} bytes per call.");

        static void Check(Complex[] original, bool allowComputedNaNVariation = false)
        {
            Complex[] expected = PocketFftComplex.ForwardDucc(original);
            Complex[] actual = original.ToArray();
            PocketFftComplex.ForwardDuccInPlace(actual);

            ReadOnlySpan<ulong> expectedBits = MemoryMarshal.Cast<Complex, ulong>(expected);
            ReadOnlySpan<ulong> actualBits = MemoryMarshal.Cast<Complex, ulong>(actual);
            if (!allowComputedNaNVariation)
            {
                Assert.True(expectedBits.SequenceEqual(actualBits),
                    $"Allocating and in-place DUCC forward differed for length {original.Length}.");
                return;
            }

            // Computed NaN sign and payload can vary across JIT tiers.
            const ulong MagnitudeMask = 0x7fff_ffff_ffff_ffffUL;
            const ulong InfinityBits = 0x7ff0_0000_0000_0000UL;
            const ulong QuietNaNBit = 0x0008_0000_0000_0000UL;
            Assert.Equal(expectedBits.Length, actualBits.Length);
            for (int index = 0; index < expectedBits.Length; index++)
            {
                ulong left = expectedBits[index];
                ulong right = actualBits[index];
                if (left == right)
                    continue;

                Assert.True(
                    (left & MagnitudeMask) > InfinityBits && (right & MagnitudeMask) > InfinityBits
                        && ((left ^ right) & QuietNaNBit) == 0,
                    $"DUCC forward differed at component {index} for length {original.Length}: {left:X16} != {right:X16}.");
            }
        }
    }
}
