using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcParallelResamplerTests
{
    [Theory(DisplayName = "Parallel TBC sinc resampling remains bit-exact")]
    [InlineData(TbcLineInterpolationMethod.Linear)]
    [InlineData(TbcLineInterpolationMethod.Quadratic)]
    [InlineData(TbcLineInterpolationMethod.Cubic)]
    public void ParallelTbcSincResamplingRemainsBitExact(TbcLineInterpolationMethod method)
    {
        const int outputLineLength = 1_024;
        const int lineCount = 100;
        double[] source = Enumerable.Range(0, 220_000)
            .Select(index => Math.Sin(index * 0.0031) + Math.Cos(index * 0.0007))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => 1_000.25 + (line * 2_000.125) + (0.01 * line * line))
            .ToArray();
        var serial = new TbcLineResampler(
            outputLineLength,
            method,
            wowLevelAdjustSmoothing: 1.5,
            nominalInputLineLength: 2_000.125,
            workerThreads: 1);
        var parallel = new TbcLineResampler(
            outputLineLength,
            method,
            wowLevelAdjustSmoothing: 1.5,
            nominalInputLineLength: 2_000.125,
            workerThreads: 5);

        double[] expected = serial.ResampleLines(source, lineLocations, firstLine: 0, lineCount);
        double[] actual = parallel.ResampleLines(source, lineLocations, firstLine: 0, lineCount);

        Assert.Equal(expected, actual);
    }
}
