using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

[Collection(DspWorkingBufferCollection.Name)]
public sealed class TbcDirectConversionTests
{
    [Theory(DisplayName = "Prepared TBC direct conversion is bit-exact")]
    [InlineData(1)]
    [InlineData(5)]
    public void PreparedTbcDirectConversionIsBitExact(int workerThreads)
    {
        const int outputLineLength = 1_024;
        const int lineCount = 100;
        const double nominalInputLineLength = 2_000.125;
        double[] source = Enumerable.Range(0, 220_000)
            .Select(index => 4_000_000.0
                + (1_500_000.0 * Math.Sin(index * 0.0031))
                + (250_000.0 * Math.Cos(index * 0.0007)))
            .ToArray();
        double[] lineLocations = Enumerable.Range(0, lineCount + 1)
            .Select(line => 1_000.25 + (line * nominalInputLineLength) + (0.01 * line * line))
            .ToArray();
        lineLocations[50] += 50.0;
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
            nominalInputLineLength,
            workerThreads);

        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            lineLocations,
            firstLine: 0,
            lineCount);
        double[] expectedLevelAdjusts = BuildLegacyLinearLevelAdjusts(
            lineLocations,
            outputLineLength,
            lineCount,
            nominalInputLineLength,
            wowLevelAdjustSmoothing: 1.5);
        ushort[] expected = converter.ConvertHz(resampler.ResamplePrepared(source, plan));
        ushort[] actual = resampler.ResamplePreparedToUInt16(source, plan, converter);
        var callerOwned = new ushort[plan.DestinationLength];
        resampler.ResamplePreparedToUInt16(source, plan, converter, callerOwned);

        Assert.True(
            MemoryMarshal.AsBytes(expectedLevelAdjusts.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(plan.LevelAdjusts.AsSpan(0, expectedLevelAdjusts.Length))),
            "Prepared linear TBC level adjustment differs from the allocation-based reference.");
        Assert.Equal(expected, actual);
        Assert.Equal(expected, callerOwned);

        if (workerThreads == 1)
        {
            for (int i = 0; i < 4; i++)
            {
                using TbcLineResampler.ResamplingPlan warmup =
                    resampler.PrepareLineResampling(lineLocations, firstLine: 0, lineCount);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                using TbcLineResampler.ResamplingPlan measured =
                    resampler.PrepareLineResampling(lineLocations, firstLine: 0, lineCount);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(
                allocated < 64_000,
                $"Preparing 200 linear TBC plans allocated {allocated:N0} bytes.");
        }
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
        var directDestination = new ushort[frameSpec.FieldSampleCount];
        var fallbackDestination = new ushort[frameSpec.FieldSampleCount];
        TbcRenderedField directIntoDestination = directRenderer.RenderPreparedFieldPayload(
            source,
            directPlan,
            outputDestination: directDestination);
        TbcRenderedField fallbackIntoDestination = fallbackRenderer.RenderPreparedFieldPayload(
            source,
            fallbackPlan,
            outputDestination: fallbackDestination);

        Assert.Equal(fallback.Samples, direct.Samples);
        Assert.Equal(direct.Samples, directIntoDestination.Samples);
        Assert.Equal(fallback.Samples, fallbackIntoDestination.Samples);
        Assert.Same(directDestination, directIntoDestination.Samples);
        Assert.Same(fallbackDestination, fallbackIntoDestination.Samples);
        Assert.Null(direct.OutputPayload);
        Assert.NotNull(fallback.OutputPayload);
        Assert.NotNull(fallbackIntoDestination.OutputPayload);
        Assert.Equal(fallback.OutputPayload.SampleFormat, fallbackIntoDestination.OutputPayload.SampleFormat);
        Assert.Equal(fallback.OutputPayload.Bytes, fallbackIntoDestination.OutputPayload.Bytes);
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
        var destination = new ushort[destinationLength];
        _ = renderer.RenderPreparedFieldPayload(
            source,
            plan,
            outputDestination: destination);

        long before = GC.GetAllocatedBytesForCurrentThread();
        TbcRenderedField rendered = renderer.RenderPreparedFieldPayload(source, plan);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        TbcRenderedField callerOwned = renderer.RenderPreparedFieldPayload(
            source,
            plan,
            outputDestination: destination);
        long callerOwnedAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(destinationLength, rendered.Samples.Length);
        Assert.Same(destination, callerOwned.Samples);
        Assert.Equal(rendered.Samples, callerOwned.Samples);
        Assert.True(
            allocated < destinationLength * 3L,
            $"Direct prepared TBC rendering allocated {allocated:N0} bytes.");
        Assert.True(
            callerOwnedAllocated < 16_384,
            $"Caller-owned prepared TBC rendering allocated {callerOwnedAllocated:N0} bytes.");
        Assert.True(
            allocated >= destinationLength * sizeof(ushort),
            $"Allocating prepared TBC rendering allocated only {allocated:N0} bytes.");
    }

    [Fact(DisplayName = "Prepared TBC fallback renderer reuses a caller workspace")]
    public void PreparedTbcFallbackRendererReusesCallerWorkspace()
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
            ire0Adjust: new Ire0AdjustOptions(
                BackPorch: false,
                HSync: false,
                BackPorchStart: 0,
                BackPorchEnd: 0),
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
        var workspace = new double[destinationLength];

        using TbcLineResampler.ResamplingPlan plan =
            renderer.PrepareFieldResampling(lineLocations);
        TbcRenderedField expected =
            renderer.RenderPreparedFieldPayload(source, plan);
        _ = renderer.RenderPreparedFieldPayload(
            source,
            plan,
            resamplingWorkspace: workspace);
        Array.Fill(workspace, double.NaN);

        long before = GC.GetAllocatedBytesForCurrentThread();
        TbcRenderedField actual = renderer.RenderPreparedFieldPayload(
            source,
            plan,
            resamplingWorkspace: workspace);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected.Samples, actual.Samples);
        Assert.True(
            allocated < destinationLength * 3L,
            $"Workspace-backed prepared TBC rendering allocated {allocated:N0} bytes.");
    }

    private static double[] BuildLegacyLinearLevelAdjusts(
        IReadOnlyList<double> lineLocations,
        int outputLineLength,
        int lineCount,
        double nominalInputLineLength,
        double wowLevelAdjustSmoothing)
    {
        double inputScale = 1.0 / nominalInputLineLength;
        var lineFactors = new double[lineCount];
        for (int line = 0; line < lineCount; line++)
        {
            lineFactors[line] = (lineLocations[line + 1] * inputScale)
                - (lineLocations[line] * inputScale);
        }

        double median = NumpyReduction.MedianFloat64(lineFactors);
        var deviations = new double[lineCount];
        for (int line = 0; line < lineCount; line++)
        {
            deviations[line] = Math.Abs(lineFactors[line] - median);
        }

        double mad = NumpyReduction.MedianFloat64(deviations);
        double threshold = mad > 0.0 ? 15.0 * mad : 0.001;
        var levelAdjusts = new double[checked(outputLineLength * lineCount)];
        for (int line = 0; line < lineCount; line++)
        {
            double factor = lineFactors[line];
            Array.Fill(
                levelAdjusts,
                Math.Abs(factor - median) > threshold ? median : factor,
                line * outputLineLength,
                outputLineLength);
        }

        if (wowLevelAdjustSmoothing > 0.0)
        {
            double alpha = 1.0 / (wowLevelAdjustSmoothing * outputLineLength);
            for (int i = 1; i < levelAdjusts.Length; i++)
            {
                double previous = levelAdjusts[i - 1];
                levelAdjusts[i] = Math.FusedMultiplyAdd(
                    levelAdjusts[i] - previous,
                    alpha,
                    previous);
            }
        }

        return levelAdjusts;
    }
}
