using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscPilotNumericsCompatibilityTests
{
    [Theory(DisplayName = "PAL LD pilot circular mean matches NumPy complex128 bits")]
    [InlineData(1, 0x3FBFFCB923A29C78UL)]
    [InlineData(2, 0x3FBFFDEF8487B99DUL)]
    [InlineData(3, 0x3FBFFF25E56CD6C3UL)]
    [InlineData(4, 0x3FBFFEB4A66543B5UL)]
    [InlineData(63, 0x3FC000039859A2A4UL)]
    [InlineData(64, 0x3FBFFFFC115DF5B8UL)]
    [InlineData(65, 0x3FBFFFFAD678B9E8UL)]
    [InlineData(127, 0x3FBFFFF52ADF1AC4UL)]
    [InlineData(128, 0x3FBFFFF5A5332A76UL)]
    [InlineData(129, 0x3FBFFFFAED858234UL)]
    [InlineData(323, 0x3FBFFFFE71162D57UL)]
    public void PalLdPilotCircularMeanMatchesNumpyComplex128Bits(
        int length,
        ulong expectedBits)
    {
        double[] phases = Enumerable.Range(0, length)
            .Select(index => 0.125 + ((((index * 37) % 101) - 50) * 1e-6))
            .ToArray();

        double average = TbcFieldDecodePipeline.CircularAverageUnitPhase(phases);

        Assert.Equal(expectedBits, BitConverter.DoubleToUInt64Bits(average));
    }
}
