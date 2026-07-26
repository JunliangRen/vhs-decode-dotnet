using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcDirectConversionTests
{
    [Theory(DisplayName = "Prepared TBC direct conversion is bit-exact")]
    [InlineData(1)]
    [InlineData(5)]
    public void PreparedTbcDirectConversionIsBitExact(int workerThreads)
    {
        const int outputLineLength = 1_024;
        const int lineCount = 100;
        double[] source = Enumerable.Range(0, 220_000)
            .Select(index => 4_000_000.0
                + (1_500_000.0 * Math.Sin(index * 0.0031))
                + (250_000.0 * Math.Cos(index * 0.0007)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => 1_000.25 + (line * 2_000.125) + (0.01 * line * line))
            .ToArray();
        var converter = new VideoOutputConverter(
            ire0: 4_000_000.25,
            hzIre: 100_000.125,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 512.25);
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
        ushort[] expected = converter.ConvertHz(resampler.ResamplePrepared(source, plan));
        ushort[] actual = resampler.ResamplePreparedToUInt16(source, plan, converter);

        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "Prepared TBC renderer direct path matches raw fallback samples")]
    public void PreparedTbcRendererDirectPathMatchesRawFallbackSamples()
    {
        const int outputLineLength = 64;
        const int lineCount = 8;
        var frameSpec = new TbcFrameSpec(
            "PAL",
            outputLineLength,
            lineCount,
            OutputSampleRateHz: 4_000_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 4_000_000.25,
            hzIre: 100_000.125,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 512.25);
        var directRenderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            nominalInputLineLength: 128.125);
        var fallbackRenderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            exportRawTbc: true,
            nominalInputLineLength: 128.125);
        double[] source = Enumerable.Range(0, 1_200)
            .Select(index => 4_000_000.0 + (750_000.0 * Math.Sin(index * 0.013)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => 50.25 + (line * 128.125))
            .ToArray();

        using TbcLineResampler.ResamplingPlan directPlan =
            directRenderer.PrepareFieldResampling(lineLocations);
        using TbcLineResampler.ResamplingPlan fallbackPlan =
            fallbackRenderer.PrepareFieldResampling(lineLocations);
        TbcRenderedField direct = directRenderer.RenderPreparedFieldPayload(source, directPlan);
        TbcRenderedField fallback = fallbackRenderer.RenderPreparedFieldPayload(source, fallbackPlan);

        Assert.Equal(fallback.Samples, direct.Samples);
        Assert.Null(direct.OutputPayload);
        Assert.NotNull(fallback.OutputPayload);
        Assert.Same(converter, direct.OutputConverter);
    }

    [Fact(DisplayName = "Prepared TBC renderer direct path avoids a double field allocation")]
    public void PreparedTbcRendererDirectPathAvoidsDoubleFieldAllocation()
    {
        const int outputLineLength = 1_024;
        const int lineCount = 100;
        const int destinationLength = outputLineLength * lineCount;
        var frameSpec = new TbcFrameSpec(
            "PAL",
            outputLineLength,
            lineCount,
            OutputSampleRateHz: 4_000_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        var converter = new VideoOutputConverter(
            ire0: 4_000_000.25,
            hzIre: 100_000.125,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 512.25);
        var renderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            nominalInputLineLength: 2_000.125,
            workerThreads: 1);
        double[] source = Enumerable.Range(0, 220_000)
            .Select(index => 4_000_000.0
                + (1_500_000.0 * Math.Sin(index * 0.0031))
                + (250_000.0 * Math.Cos(index * 0.0007)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => 1_000.25 + (line * 2_000.125) + (0.01 * line * line))
            .ToArray();

        using TbcLineResampler.ResamplingPlan plan =
            renderer.PrepareFieldResampling(lineLocations);
        _ = renderer.RenderPreparedFieldPayload(source, plan);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TbcRenderedField rendered = renderer.RenderPreparedFieldPayload(source, plan);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(destinationLength, rendered.Samples.Length);
        Assert.True(
            allocated < destinationLength * 3L,
            $"Direct prepared TBC rendering allocated {allocated:N0} bytes.");
    }
}
