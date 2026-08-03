using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcParallelResamplerTests
{
    [Fact(DisplayName = "Batched linear TBC positions preserve upstream scaled coordinates")]
    public void BatchedLinearTbcPositionsPreserveUpstreamScaledCoordinates()
    {
        var random = new Random(0x51C0_2026);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            int outputLineLength = random.Next(1, 258);
            int locationCount = random.Next(4, 40);
            var locations = new double[locationCount];
            locations[0] = (random.NextDouble() - 0.5) * 1_000_000.0;
            for (int i = 1; i < locations.Length; i++)
            {
                locations[i] = locations[i - 1] + 0.001 + (random.NextDouble() * 10_000.0);
            }

            int firstLine = random.Next(0, locationCount - 1);
            int lineCount = random.Next(0, locationCount - firstLine);
            var resampler = new TbcLineResampler(
                outputLineLength,
                TbcLineInterpolationMethod.Linear,
                nominalInputLineLength: 2_000.125,
                workerThreads: 5);

            using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
                locations,
                firstLine,
                lineCount);
            for (int i = 0; i < plan.DestinationLength; i++)
            {
                int sampleIndex = checked((firstLine * outputLineLength) + i);
                int left = sampleIndex / outputLineLength;
                double outputScale = 2_000.125 / outputLineLength;
                double scaledPosition = sampleIndex * outputScale;
                double fraction = Math.Clamp(
                    (scaledPosition - (left * 2_000.125)) / 2_000.125,
                    0.0,
                    1.0);
                double expected = locations[left]
                    + ((locations[left + 1] - locations[left]) * fraction);

                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(expected),
                    BitConverter.DoubleToInt64Bits(plan.SourcePositions[i]));
            }
        }
    }

    [Fact(DisplayName = "Linear TBC accepts non-monotonic measured line locations like SciPy")]
    public void LinearTbcAcceptsNonMonotonicMeasuredLineLocations()
    {
        double[] locations = [20.0, 30.0, 25.0, 40.0];
        var resampler = new TbcLineResampler(
            outputLineLength: 4,
            nominalInputLineLength: 10.0);

        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            locations,
            firstLine: 1,
            lineCount: 1);

        Assert.Equal(
            [30.0, 28.75, 27.5, 26.25],
            plan.SourcePositions.AsSpan(0, plan.DestinationLength).ToArray());
    }

    [Fact(DisplayName = "TBC sinc interior and bounded edges remain bit-exact")]
    public void TbcSincInteriorAndBoundedEdgesRemainBitExact()
    {
        var resampler = new TbcLineResampler(outputLineLength: 16);
        double[] output = resampler.ResampleLines(
            Enumerable.Range(0, 64).Select(value => (double)value).ToArray(),
            [-4.25, 28.5, 67.75],
            firstLine: 0,
            lineCount: 2);

        Assert.Equal(
            "7E3B3F5DCF0E5C38FB7C46E6E5CF362D596F9DECD7416445247F0A4C4878F33C",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
    }

    [Fact(DisplayName = "TBC sinc negative coordinates match NumPy wrapped indexing")]
    public void TbcSincNegativeCoordinatesMatchNumpyWrappedIndexing()
    {
        var resampler = new TbcLineResampler(outputLineLength: 16);
        double[] output = resampler.ResampleLines(
            Enumerable.Range(0, 64).Select(value => (double)value).ToArray(),
            [-4.25, 28.5],
            firstLine: 0,
            lineCount: 1);

        Assert.Equal(
            "426959AEB3440862EF1B3148CF09D4AF143D5F4F21944E04C6AAA62214F4C0BC",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
    }

    [Fact(DisplayName = "TBC sinc bounds sources shorter than its tap window")]
    public void TbcSincBoundsSourcesShorterThanTapWindow()
    {
        var resampler = new TbcLineResampler(outputLineLength: 16);
        double[] output = resampler.ResampleLines(
            Enumerable.Range(0, 8).Select(value => (double)value).ToArray(),
            [0.0, 7.0],
            firstLine: 0,
            lineCount: 1);

        Assert.Equal(
            "5E5BED2929B57864FE4E2514C4D3754CF7168A6200A7C57FF45BDE2F97B1BD1F",
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

    [Theory(DisplayName = "Prepared TBC resampling writes caller-owned buffers bit-exactly")]
    [InlineData(1)]
    [InlineData(5)]
    public void PreparedTbcResamplingWritesCallerOwnedBuffersBitExactly(int workerThreads)
    {
        const int outputLineLength = 1_024;
        const int lineCount = 100;
        double[] source = Enumerable.Range(0, 220_000)
            .Select(index => Math.Sin(index * 0.0031) + Math.Cos(index * 0.0007))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => 1_000.25 + (line * 2_000.125) + (0.01 * line * line))
            .ToArray();
        var resampler = new TbcLineResampler(
            outputLineLength,
            TbcLineInterpolationMethod.Linear,
            wowLevelAdjustSmoothing: 1.5,
            nominalInputLineLength: 2_000.125,
            workerThreads);

        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            lineLocations,
            firstLine: 0,
            lineCount);
        double[] expected = resampler.ResamplePrepared(source, plan);
        var destination = new double[plan.DestinationLength];
        Array.Fill(destination, double.NaN);

        resampler.ResamplePrepared(source, plan, destination);

        Assert.Equal(expected, destination);
        Assert.Throws<ArgumentException>(
            () => resampler.ResamplePrepared(source, plan, new double[destination.Length - 1]));
    }

    [Theory(DisplayName = "Prepared TBC line-prefix resampling matches full output bit-exactly")]
    [InlineData(TbcLineInterpolationMethod.Linear, 1)]
    [InlineData(TbcLineInterpolationMethod.Linear, 8)]
    [InlineData(TbcLineInterpolationMethod.Quadratic, 8)]
    [InlineData(TbcLineInterpolationMethod.Cubic, 8)]
    public void PreparedTbcLinePrefixResamplingMatchesFullOutputBitExactly(
        TbcLineInterpolationMethod interpolationMethod,
        int workerThreads)
    {
        const int OutputLineLength = 1_024;
        const int SamplesPerLine = 320;
        const int LineCount = 256;
        const int FirstLine = 1;
        double[] source = Enumerable.Range(0, 560_000)
            .Select(index => Math.Sin(index * 0.0031) + Math.Cos(index * 0.0007))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, FirstLine + LineCount + 1)
            .Select(line => 1_000.25 + (line * 2_000.125) + (0.01 * line * line))
            .ToArray();
        var resampler = new TbcLineResampler(
            OutputLineLength,
            interpolationMethod,
            wowLevelAdjustSmoothing: 1.5,
            nominalInputLineLength: 2_000.125,
            workerThreads);

        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            lineLocations,
            FirstLine,
            LineCount);
        double[] expected = resampler.ResamplePrepared(source, plan);
        var actual = new double[plan.DestinationLength];
        Array.Fill(actual, double.NaN);

        resampler.ResampleLinePrefixes(
            source,
            lineLocations,
            FirstLine,
            LineCount,
            SamplesPerLine,
            actual);

        for (int line = 0; line < LineCount; line++)
        {
            int lineStart = line * OutputLineLength;
            Assert.Equal(
                expected.AsSpan(lineStart, SamplesPerLine),
                actual.AsSpan(lineStart, SamplesPerLine));
            Assert.All(
                actual.AsSpan(
                        lineStart + SamplesPerLine,
                        OutputLineLength - SamplesPerLine)
                    .ToArray(),
                static value => Assert.True(double.IsNaN(value)));
        }
    }

    [Fact(DisplayName = "Prepared TBC resampling reuses caller-owned output without field allocation")]
    public void PreparedTbcResamplingReusesCallerOwnedOutputWithoutFieldAllocation()
    {
        const int outputLineLength = 512;
        const int lineCount = 64;
        double[] source = Enumerable.Range(0, (lineCount + 1) * 1_024 + 16)
            .Select(index => Math.Sin(index * 0.007) + (0.2 * Math.Cos(index * 0.011)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => line * 1_024.0)
            .ToArray();
        var resampler = new TbcLineResampler(
            outputLineLength,
            nominalInputLineLength: 1_024.0,
            workerThreads: 1);
        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            lineLocations,
            firstLine: 0,
            lineCount);
        var destination = new double[plan.DestinationLength];
        resampler.ResamplePrepared(source, plan, destination);

        long before = GC.GetAllocatedBytesForCurrentThread();
        resampler.ResamplePrepared(source, plan, destination);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(destination);
        Assert.True(
            allocated < 16_384,
            $"Warm prepared TBC resampling allocated {allocated:N0} bytes.");
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
