using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftPacketTransposeTests
{
    // Frozen from the original packet copy/scatter implementation, before tiling.
    private static readonly (int Length, string Forward, string Inverse)[] Cases =
    [
        (16_384,
            "1F45F9DF9BC5B4088D14863137AB2A799885784B50CFA41362C86887405994B8",
            "EF7D5F9A015144B5BBADD1B89F50E582FC0E7E8FA54663551A1CF4B3802C119C"),
        (32_768,
            "9511E280E2089A9CAC6888B63F2544C88E9E2FF3FBC7D601212F23BAC0397989",
            "2016B804F92A14A858CD0294ECA123D21FB39BB7FFEF8EF0642EDF6764D60E1E"),
        (65_536,
            "0D3309720542B99BC7BA3675094C1B372388E73B35150E114924444BC6E678C2",
            "6E699DB5BCE4CA33725A9A105C91F00C5D427C54BF60F2475C26CB62105BC4AE"),
        (1_048_576,
            "CE038C66DE8F7D251644637C20E401BA1C6F90F8C588E0D4427C1B2435F7A9BA",
            "BCE3538172E8DED7EE61908D34FD1ECBE216DC9223D6293E5540E6842A129840")
    ];

    [Fact(DisplayName = "Packet FFT retains frozen bits across rectangular and reused stage sizes")]
    public void RetainsFrozenBitsAcrossRectangularAndReusedStageSizes()
    {
        // 32K is a 256x128 matrix; 1M has even second-plan pass count.
        // Grow TLS first, then repeatedly exercise active matrices below its capacity.
        foreach (int caseIndex in new[] { 3, 0, 1, 0, 2, 1, 0, 3 })
        {
            AssertFrozen(Cases[caseIndex]);
        }
    }

    [Fact(DisplayName = "Parallel packet FFT retains frozen forward and inverse bits")]
    public void ParallelRetainsFrozenForwardAndInverseBits()
    {
        Parallel.For(0, 9, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            index => AssertFrozen(Cases[index % 3]));
    }

    private static void AssertFrozen((int Length, string Forward, string Inverse) testCase)
    {
        Complex[] input = BuildInput(testCase.Length);
        Complex[] owned = input.ToArray();
        string forwardHash = Hash(PocketFftComplex.ForwardDucc(input));
        string inverseHash = Hash(PocketFftComplex.InverseDucc(input));
        Assert.Equal(Hash(owned), Hash(input));
        WindowsFrozenBitOracle.Equal(testCase.Forward, forwardHash);
        WindowsFrozenBitOracle.Equal(testCase.Inverse, inverseHash);

        PocketFftComplex.ForwardDuccInPlace(owned);
        Assert.Equal(forwardHash, Hash(owned));
        input.CopyTo(owned, 0);
        PocketFftComplex.InverseDuccInPlace(owned);
        Assert.Equal(inverseHash, Hash(owned));
    }

    private static Complex[] BuildInput(int length)
    {
        var input = new Complex[length];
        ulong state = 0xD1B54A32D192ED03UL;
        for (int index = 0; index < input.Length; index++)
        {
            input[index] = new Complex(Next(), Next());
        }
        return input;

        double Next()
        {
            state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
            return (((state >> 11) / 9007199254740992.0) - 0.5) * 2048.0;
        }
    }

    private static string Hash(Complex[] values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
}
