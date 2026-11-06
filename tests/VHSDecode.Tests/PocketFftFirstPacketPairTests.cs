using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftFirstPacketPairTests
{
    private const int Length = 32_768;

    private static bool CanEnter => Avx.IsSupported && OperatingSystem.IsWindows()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [Fact]
    public void PreservesFiniteSpectrumBitsAndStockReuse()
    {
        AssertCandidate(CreateInput(Length, 1));
        AssertCandidate(CreateHilbertInput());
        AssertCandidate(Enumerable.Repeat(new Complex(-0.0, 0.0), Length).ToArray());
        AssertCandidate(Enumerable.Range(0, Length).Select(index => new Complex(
            index % 4 == 0 ? -double.Epsilon : double.Epsilon,
            index % 3 == 0 ? -0.0 : double.Epsilon)).ToArray());
        AssertCandidate(CreateInput(Length, 2).Select(value => value * 1e270).ToArray());
        Complex[] large = CreateInput(65_536, 3);
        PocketFftComplex.InverseDuccInPlace(large);
        AssertCandidate(CreateInput(Length, 4));
    }

    [Fact]
    public void DeclineDoesNotWriteCallerInput()
    {
        Assert.Throws<ArgumentNullException>(() => PocketFftComplex.TryInverseDuccFirstPacketPairs32K(null!));
        AssertDeclines(CreateInput(16_384, 5));
        AssertDeclines(CreateInput(65_536, 6));

        // Reject both early and late first-packet pairs. A late failure follows
        // real writes to private stage and must still preserve the caller.
        foreach (int index in new[] { 0, 1, 127, Length - 1 })
        {
            foreach (double special in new[]
            {
                double.PositiveInfinity, double.NegativeInfinity, double.MaxValue,
                BitConverter.UInt64BitsToDouble(0x7FF8000000000789UL),
                BitConverter.UInt64BitsToDouble(0xFFF0000000000ABCUL)
            })
            {
                Complex[] input = CreateInput(Length, 7);
                input[index] = new Complex(input[index].Real, special);
                AssertDeclines(input);
            }
        }
    }

    [Fact]
    public void KeepsIndependentWorkerScratchAndRetainedOutputs()
    {
        Complex[][] input = Enumerable.Range(0, 8).Select(index => CreateInput(Length, 8 + index)).ToArray();
        Complex[][] output = new Complex[input.Length][];
        Parallel.For(0, input.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            index => output[index] = AssertCandidate(input[index]));
        Parallel.For(0, input.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            index => Assert.Equal(Hash(PocketFftComplex.InverseDucc(input[index])), Hash(output[index])));
    }

    private static Complex[] AssertCandidate(Complex[] input)
    {
        string immutable = Hash(input);
        Complex[] expected = (Complex[])input.Clone();
        PocketFftComplex.InverseDuccInPlace(expected);
        Assert.Equal(Hash(expected), Hash(PocketFftComplex.InverseDucc(input)));
        Complex[] candidate = (Complex[])input.Clone();
        Assert.Equal(CanEnter, PocketFftComplex.TryInverseDuccFirstPacketPairs32K(candidate));
        if (!CanEnter)
        {
            Assert.Equal(immutable, Hash(candidate));
            PocketFftComplex.InverseDuccInPlace(candidate);
        }

        Assert.Equal(Hash(expected), Hash(candidate));
        Assert.Equal(immutable, Hash(input));
        return candidate;
    }

    private static void AssertDeclines(Complex[] input)
    {
        string immutable = Hash(input);
        Assert.False(PocketFftComplex.TryInverseDuccFirstPacketPairs32K(input));
        Assert.Equal(immutable, Hash(input));
    }

    private static Complex[] CreateHilbertInput()
    {
        Complex[] input = CreateInput(Length, 13);
        for (int index = (Length / 2) + 1; index < Length; index++)
        {
            Complex value = input[index];
            input[index] = new Complex(Math.CopySign(0.0, value.Real), Math.CopySign(0.0, value.Imaginary));
        }

        return input;
    }

    private static Complex[] CreateInput(int length, int seed)
    {
        Complex[] values = new Complex[length];
        var random = new Random(seed);
        for (int index = 0; index < length; index++)
        {
            values[index] = new Complex((random.NextDouble() - 0.5) * 8192.0, (random.NextDouble() - 0.5) * 8192.0);
        }

        return values;
    }

    private static string Hash(Complex[] values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
}
