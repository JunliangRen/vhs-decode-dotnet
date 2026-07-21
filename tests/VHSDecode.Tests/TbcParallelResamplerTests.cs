using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcParallelResamplerTests
{
    [Fact(DisplayName = "TBC sinc interior and clamped edges remain bit-exact")]
    public void TbcSincInteriorAndClampedEdgesRemainBitExact()
    {
        var resampler = new TbcLineResampler(outputLineLength: 16);
        double[] output = resampler.ResampleLines(
            Enumerable.Range(0, 64).Select(value => (double)value).ToArray(),
            [-4.25, 28.5, 67.75],
            firstLine: 0,
            lineCount: 2);

        Assert.Equal(
            "399D86D060BC715962539EA12B2C4EF83E86C330EDFDDAA3F305379B207612DB",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
    }

    [Fact(DisplayName = "TBC sinc clamps sources shorter than its tap window")]
    public void TbcSincClampsSourcesShorterThanTapWindow()
    {
        var resampler = new TbcLineResampler(outputLineLength: 16);
        double[] output = resampler.ResampleLines(
            Enumerable.Range(0, 8).Select(value => (double)value).ToArray(),
            [0.0, 7.0],
            firstLine: 0,
            lineCount: 1);

        Assert.Equal(
            "357269E737450AD2B3007BD6CC78974A536A5B825B0A55AC9F4D0DC9CCFE6BE1",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
    }

    [Fact(DisplayName = "Prepared TBC resampling plans are reusable and bit-exact")]
    public async Task PreparedTbcResamplingPlansAreReusableAndBitExact()
    {
        const int outputLineLength = 1_024;
        const int lineCount = 100;
        double[] firstSource = Enumerable.Range(0, 220_000)
            .Select(index => Math.Sin(index * 0.0031) + Math.Cos(index * 0.0007))
            .ToArray();
        double[] secondSource = firstSource
            .Select((value, index) => value + (0.125 * Math.Sin(index * 0.0013)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => 1_000.25 + (line * 2_000.125) + (0.01 * line * line))
            .ToArray();
        var resampler = new TbcLineResampler(
            outputLineLength,
            TbcLineInterpolationMethod.Linear,
            wowLevelAdjustSmoothing: 1.5,
            nominalInputLineLength: 2_000.125,
            workerThreads: 5);
        double[] expectedFirst = resampler.ResampleLines(firstSource, lineLocations, 0, lineCount);
        double[] expectedSecond = resampler.ResampleLines(secondSource, lineLocations, 0, lineCount);

        TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            lineLocations,
            firstLine: 0,
            lineCount);
        Task<double[]> first = Task.Run(() => resampler.ResamplePrepared(firstSource, plan));
        Task<double[]> second = Task.Run(() => resampler.ResamplePrepared(secondSource, plan));
        await Task.WhenAll(first, second);

        Assert.Equal(expectedFirst, first.Result);
        Assert.Equal(expectedSecond, second.Result);
        plan.Dispose();
        Assert.Throws<ObjectDisposedException>(() => resampler.ResamplePrepared(firstSource, plan));
    }

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
