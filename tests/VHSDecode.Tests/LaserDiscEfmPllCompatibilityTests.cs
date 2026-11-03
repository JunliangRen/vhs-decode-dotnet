using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscEfmPllCompatibilityTests
{
    [Theory(DisplayName = "LD EFM PLL preserves frozen output across arbitrary chunk boundaries")]
    [InlineData(0, "B0FB7D424EB22270B3D2C3A6C34B952D731C7E048F7CF37D65F9632FD9922D35")]
    [InlineData(1, "BF46C310C8245AD765F302DAE0D1AB05E88FCB39891FE591A77C264E2BFF83C1")]
    [InlineData(2, "7E9F365C8E17A49CAB83573EE7C2155CC1422AF93D41263B63000FEB9B36FA98")]
    public void PreservesFrozenOutputAcrossChunkBoundaries(int pattern, string expectedHash)
    {
        // Freeze the scalar implementation for carrier, noise, and signed-limit/zero transitions.
        var input = new short[131_072];
        var random = new Random(0x531D + pattern);
        for (int i = 0; i < input.Length; i++)
        {
            int phase = i % 112;
            input[i] = pattern switch
            {
                0 => (short)(((phase < 56 ? phase - 28 : 84 - phase) * 1_000)
                    + ((i * 7_919 % 523) - 261)),
                1 => (short)random.Next(short.MinValue, short.MaxValue + 1),
                _ => (i % 67) switch
                {
                    0 => short.MinValue,
                    1 => 0,
                    2 => short.MaxValue,
                    3 => -1,
                    4 => 1,
                    _ => (short)(i % 2 == 0 ? 0 : -1)
                }
            };
        }

        byte[] whole = new LaserDiscEfmPll().Process(input);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(whole)));

        var chunked = new LaserDiscEfmPll();
        var chunkRandom = new Random(123);
        var actual = new List<byte>();
        for (int offset = 0; offset < input.Length;)
        {
            Assert.Empty(chunked.Process([]));
            int count = Math.Min(chunkRandom.Next(1, 1_300), input.Length - offset);
            actual.AddRange(chunked.Process(input.AsSpan(offset, count)));
            offset += count;
        }

        Assert.Equal(whole, actual.ToArray());
    }
}
