using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftDuccOwnershipTests
{
    [Theory(DisplayName = "In-place DUCC forward retains allocating bits and avoids output allocation")]
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
        Check(input);

        var disposable = new Complex[length];
        long start = GC.GetAllocatedBytesForCurrentThread();
        PocketFftComplex.ForwardDuccInPlace(disposable);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocatedBytes < 256, $"Warmed in-place forward allocated {allocatedBytes} bytes.");

        static void Check(Complex[] original)
        {
            Complex[] expected = PocketFftComplex.ForwardDucc(original);
            Complex[] actual = original.ToArray();
            PocketFftComplex.ForwardDuccInPlace(actual);
            Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())),
                $"Allocating and in-place DUCC forward differed for length {original.Length}.");
        }
    }
}
