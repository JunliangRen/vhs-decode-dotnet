using System.Runtime.InteropServices;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class TbcApproxResamplingKernelTests
{
    private const int ParallelOutputLineLength = 2_048;
    private const int ParallelFirstLine = 3;
    private const int ParallelLineCount = 33;
    private const int ParallelSourceGuardSamples = 31;

    [Fact(DisplayName = "Explicit Catmull-Rom4 renderer matches the independent prepared kernel")]
    public void ExplicitCatmullRom4RendererMatchesIndependentPreparedKernel()
    {
        TbcFrameSpec frameSpec = CreateFrameSpec();
        VideoOutputConverter converter = CreateConverter();
        var renderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            nominalInputLineLength: 44.625,
            sampleResamplingKernel: TbcSampleResamplingKernel.CatmullRom4Float32);
        double[] source = CreateSource();
        double[] lineLocations = CreateLineLocations();

        using TbcLineResampler.ResamplingPlan plan =
            renderer.PrepareFieldResampling(lineLocations);
        var expected = new double[plan.DestinationLength];
        ApproxCatmullRom4Resampler.Resample(
            source,
            plan.SourcePositions.AsSpan(0, plan.DestinationLength),
            plan.LevelAdjusts.AsSpan(plan.PrefixSamples, plan.DestinationLength),
            expected);
        double[] actual = renderer.ResamplePreparedField(source, plan);
        var callerOwned = new double[plan.DestinationLength];
        renderer.ResamplePreparedField(source, plan, callerOwned);
        double[] unprepared = renderer.ResampleField(source, lineLocations);

        Assert.Equal(
            TbcSampleResamplingKernel.CatmullRom4Float32,
            renderer.SampleResamplingKernel);
        AssertBitExact(expected, actual);
        AssertBitExact(expected, callerOwned);
        AssertBitExact(expected, unprepared);
    }

    [Fact(DisplayName = "Explicit Catmull-Rom4 shifted renderer matches the independent shifted kernel")]
    public void ExplicitCatmullRom4ShiftedRendererMatchesIndependentShiftedKernel()
    {
        const double SourcePositionShift = 0.375;
        TbcFrameSpec frameSpec = CreateFrameSpec();
        var renderer = new TbcFieldRenderer(
            frameSpec,
            CreateConverter(),
            nominalInputLineLength: 44.625,
            sampleResamplingKernel: TbcSampleResamplingKernel.CatmullRom4Float32);
        double[] source = CreateSource();
        double[] lineLocations = CreateLineLocations();

        using TbcLineResampler.ResamplingPlan plan =
            renderer.PrepareFieldResampling(lineLocations);
        var expected = new double[plan.DestinationLength];
        ApproxCatmullRom4Resampler.Resample(
            source,
            plan.SourcePositions.AsSpan(0, plan.DestinationLength),
            plan.LevelAdjusts.AsSpan(plan.PrefixSamples, plan.DestinationLength),
            SourcePositionShift,
            expected);
        double[] actual = renderer.ResamplePreparedField(
            source,
            plan,
            SourcePositionShift);
        var callerOwned = new double[plan.DestinationLength];
        renderer.ResamplePreparedField(
            source,
            plan,
            callerOwned,
            SourcePositionShift);
        var fieldDestination = new double[frameSpec.FieldSampleCount];
        renderer.ResampleFieldInto(
            source,
            lineLocations,
            firstLine: 0,
            fieldDestination,
            SourcePositionShift);

        AssertBitExact(expected, actual);
        AssertBitExact(expected, callerOwned);
        AssertBitExact(expected, fieldDestination);
    }

    [Fact(DisplayName = "Explicit Catmull-Rom4 direct UInt16 rendering matches the independent kernel")]
    public void ExplicitCatmullRom4DirectUInt16RenderingMatchesIndependentKernel()
    {
        TbcFrameSpec frameSpec = CreateFrameSpec();
        VideoOutputConverter converter = CreateConverter();
        var renderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            nominalInputLineLength: 44.625,
            sampleResamplingKernel: TbcSampleResamplingKernel.CatmullRom4Float32);
        double[] source = CreateSource();

        using TbcLineResampler.ResamplingPlan plan =
            renderer.PrepareFieldResampling(CreateLineLocations());
        var expected = new ushort[plan.DestinationLength];
        ApproxCatmullRom4Resampler.ResampleToUInt16(
            source,
            plan.SourcePositions.AsSpan(0, plan.DestinationLength),
            plan.LevelAdjusts.AsSpan(plan.PrefixSamples, plan.DestinationLength),
            converter,
            expected);
        TbcRenderedField actual = renderer.RenderPreparedFieldPayload(source, plan);
        var callerOwned = new ushort[plan.DestinationLength];
        TbcRenderedField intoCallerOwned = renderer.RenderPreparedFieldPayload(
            source,
            plan,
            outputDestination: callerOwned);

        Assert.Equal(expected, actual.Samples);
        Assert.Equal(expected, intoCallerOwned.Samples);
        Assert.Same(callerOwned, intoCallerOwned.Samples);
    }

    [Fact(DisplayName = "Float32 Catmull-Rom4 prepared paths match exact-widened input bit-for-bit")]
    public void Float32CatmullRom4PreparedPathsMatchExactWidenedInputBitForBit()
    {
        var resampler = new TbcLineResampler(
            outputLineLength: 24,
            nominalInputLineLength: 44.625,
            workerThreads: 1);
        float[] source = CreateFloatSource();
        double[] widenedSource = Array.ConvertAll(source, static value => (double)value);
        VideoOutputConverter converter = CreateConverter();

        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            CreateLineLocations(),
            firstLine: 0,
            lineCount: 3);
        double[] expected = resampler.ResamplePrepared(
            widenedSource,
            plan,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        double[] actual = resampler.ResamplePrepared(
            source,
            plan,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        var callerOwned = new double[plan.DestinationLength];
        resampler.ResamplePrepared(
            source,
            plan,
            callerOwned,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        ushort[] expectedUInt16 = resampler.ResamplePreparedToUInt16(
            widenedSource,
            plan,
            converter,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        ushort[] actualUInt16 = resampler.ResamplePreparedToUInt16(
            source,
            plan,
            converter,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        var callerOwnedUInt16 = new ushort[plan.DestinationLength];
        resampler.ResamplePreparedToUInt16(
            source,
            plan,
            converter,
            callerOwnedUInt16,
            TbcSampleResamplingKernel.CatmullRom4Float32);

        AssertBitExact(expected, actual);
        AssertBitExact(expected, callerOwned);
        AssertBitExact(expectedUInt16, actualUInt16);
        AssertBitExact(expectedUInt16, callerOwnedUInt16);
    }

    [Fact(DisplayName = "Float32 Catmull-Rom4 renderer covers direct and workspace-backed prepared paths")]
    public void Float32CatmullRom4RendererCoversDirectAndWorkspaceBackedPreparedPaths()
    {
        TbcFrameSpec frameSpec = CreateFrameSpec();
        VideoOutputConverter converter = CreateConverter();
        var directRenderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            nominalInputLineLength: 44.625,
            sampleResamplingKernel: TbcSampleResamplingKernel.CatmullRom4Float32);
        var fallbackRenderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            exportRawTbc: true,
            nominalInputLineLength: 44.625,
            sampleResamplingKernel: TbcSampleResamplingKernel.CatmullRom4Float32);
        float[] source = CreateFloatSource();
        double[] widenedSource = Array.ConvertAll(source, static value => (double)value);
        double[] lineLocations = CreateLineLocations();

        using TbcLineResampler.ResamplingPlan directPlan =
            directRenderer.PrepareFieldResampling(lineLocations);
        TbcRenderedField expectedDirect = directRenderer.RenderPreparedFieldPayload(
            widenedSource,
            directPlan);
        var directDestination = new ushort[directPlan.DestinationLength];
        TbcRenderedField actualDirect = directRenderer.RenderPreparedFieldPayload(
            source,
            directPlan,
            outputDestination: directDestination);

        using TbcLineResampler.ResamplingPlan fallbackPlan =
            fallbackRenderer.PrepareFieldResampling(lineLocations);
        TbcRenderedField expectedFallback = fallbackRenderer.RenderPreparedFieldPayload(
            widenedSource,
            fallbackPlan);
        var fallbackWorkspace = new double[fallbackPlan.DestinationLength];
        var fallbackDestination = new ushort[fallbackPlan.DestinationLength];
        TbcRenderedField actualFallback =
            fallbackRenderer.RenderPreparedFieldPayloadWithDiagnosticLogger(
                source,
                fallbackPlan,
                fieldNumber: 0,
                converterOverride: null,
                trackPhaseOverride: null,
                resamplingWorkspace: fallbackWorkspace,
                outputDestination: fallbackDestination,
                diagnosticLogger: static (_, _) => { });

        AssertBitExact(expectedDirect.Samples, actualDirect.Samples);
        Assert.Same(directDestination, actualDirect.Samples);
        AssertBitExact(expectedFallback.Samples, actualFallback.Samples);
        Assert.Same(fallbackDestination, actualFallback.Samples);
        Assert.NotNull(expectedFallback.OutputPayload);
        Assert.NotNull(actualFallback.OutputPayload);
        Assert.Equal(
            expectedFallback.OutputPayload.SampleFormat,
            actualFallback.OutputPayload.SampleFormat);
        Assert.Equal(expectedFallback.OutputPayload.Bytes, actualFallback.OutputPayload.Bytes);
    }

    [Fact(DisplayName = "Float32 prepared resampling fails closed outside the Catmull-Rom4 Approx contract")]
    public void Float32PreparedResamplingFailsClosedOutsideCatmullRom4ApproxContract()
    {
        TbcFrameSpec frameSpec = CreateFrameSpec();
        VideoOutputConverter converter = CreateConverter();
        var resampler = new TbcLineResampler(
            outputLineLength: frameSpec.OutputLineLength,
            nominalInputLineLength: 44.625);
        var renderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            nominalInputLineLength: 44.625);
        float[] source = CreateFloatSource();

        using TbcLineResampler.ResamplingPlan resamplerPlan =
            resampler.PrepareLineResampling(
                CreateLineLocations(),
                firstLine: 0,
                lineCount: frameSpec.OutputLineCount);
        using TbcLineResampler.ResamplingPlan rendererPlan =
            renderer.PrepareFieldResampling(CreateLineLocations());
        var doubleDestination = new double[resamplerPlan.DestinationLength];
        var ushortDestination = new ushort[resamplerPlan.DestinationLength];

        Assert.Throws<NotSupportedException>(
            () => resampler.ResamplePrepared(
                source,
                resamplerPlan,
                doubleDestination,
                TbcSampleResamplingKernel.KaiserSinc16));
        Assert.Throws<NotSupportedException>(
            () => resampler.ResamplePreparedToUInt16(
                source,
                resamplerPlan,
                converter,
                ushortDestination,
                TbcSampleResamplingKernel.KaiserSinc16));
        Assert.Throws<NotSupportedException>(
            () => renderer.RenderPreparedFieldPayload(source, rendererPlan));
    }

    [Theory(DisplayName = "Renderer line-prefix analysis defaults to sinc and honors explicit Catmull-Rom4")]
    [InlineData(TbcLineInterpolationMethod.Linear)]
    [InlineData(TbcLineInterpolationMethod.Quadratic)]
    [InlineData(TbcLineInterpolationMethod.Cubic)]
    public void RendererLinePrefixAnalysisDefaultsToSincAndHonorsExplicitCatmullRom4(
        TbcLineInterpolationMethod interpolationMethod)
    {
        const int SamplesPerLine = 9;
        TbcFrameSpec frameSpec = CreateFrameSpec();
        VideoOutputConverter converter = CreateConverter();
        var approxRenderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            interpolationMethod: interpolationMethod,
            nominalInputLineLength: 44.625,
            sampleResamplingKernel: TbcSampleResamplingKernel.CatmullRom4Float32);
        var defaultRenderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            interpolationMethod: interpolationMethod,
            nominalInputLineLength: 44.625);
        var explicitSincRenderer = new TbcFieldRenderer(
            frameSpec,
            converter,
            interpolationMethod: interpolationMethod,
            nominalInputLineLength: 44.625,
            sampleResamplingKernel: TbcSampleResamplingKernel.KaiserSinc16);
        double[] source = CreateSource();
        double[] lineLocations = CreateLineLocations();
        var prefixes = new double[frameSpec.FieldSampleCount];
        Array.Fill(prefixes, double.NaN);
        var catmullPrefixes = new double[frameSpec.FieldSampleCount];
        Array.Fill(catmullPrefixes, double.NaN);

        approxRenderer.ResampleFieldLinePrefixesInto(
            source,
            lineLocations,
            firstLine: 0,
            SamplesPerLine,
            prefixes);
        approxRenderer.ResampleFieldLinePrefixesInto(
            source,
            lineLocations,
            firstLine: 0,
            SamplesPerLine,
            catmullPrefixes,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        double[] defaultSinc = defaultRenderer.ResampleField(source, lineLocations);
        double[] explicitSinc = explicitSincRenderer.ResampleField(source, lineLocations);
        double[] catmull = approxRenderer.ResampleField(source, lineLocations);

        Assert.Equal(
            TbcSampleResamplingKernel.KaiserSinc16,
            defaultRenderer.SampleResamplingKernel);
        AssertBitExact(defaultSinc, explicitSinc);
        for (int line = 0; line < frameSpec.OutputLineCount; line++)
        {
            int lineStart = line * frameSpec.OutputLineLength;
            AssertBitExact(
                defaultSinc.AsSpan(lineStart, SamplesPerLine),
                prefixes.AsSpan(lineStart, SamplesPerLine));
            AssertBitExact(
                catmull.AsSpan(lineStart, SamplesPerLine),
                catmullPrefixes.AsSpan(lineStart, SamplesPerLine));
            Assert.All(
                prefixes.AsSpan(
                        lineStart + SamplesPerLine,
                        frameSpec.OutputLineLength - SamplesPerLine)
                    .ToArray(),
                static value => Assert.True(double.IsNaN(value)));
            Assert.All(
                catmullPrefixes.AsSpan(
                        lineStart + SamplesPerLine,
                        frameSpec.OutputLineLength - SamplesPerLine)
                    .ToArray(),
                static value => Assert.True(double.IsNaN(value)));
        }

        Assert.Contains(
            Enumerable.Range(0, frameSpec.FieldSampleCount),
            index => BitConverter.DoubleToInt64Bits(defaultSinc[index])
                != BitConverter.DoubleToInt64Bits(catmull[index]));
    }

    [Theory(DisplayName = "Catmull-Rom4 prefix resampling covers compact, prepared, serial, and parallel paths")]
    [InlineData(TbcLineInterpolationMethod.Linear, 1)]
    [InlineData(TbcLineInterpolationMethod.Linear, 20)]
    [InlineData(TbcLineInterpolationMethod.Cubic, 1)]
    [InlineData(TbcLineInterpolationMethod.Cubic, 20)]
    public void CatmullRom4PrefixResamplingMatchesFullFieldAndPreservesTail(
        TbcLineInterpolationMethod interpolationMethod,
        int workerThreads)
    {
        const int OutputLineLength = 512;
        const int LineCount = 160;
        const int SamplesPerLine = 416;
        const double NominalInputLineLength = 640.125;
        double[] lineLocations = Enumerable.Range(0, LineCount + 1)
            .Select(index =>
                50.25
                + (index * NominalInputLineLength)
                + (0.002 * index * index))
            .ToArray();
        int sourceLength = checked((int)Math.Ceiling(lineLocations[^1]) + 16);
        double[] source = Enumerable.Range(0, sourceLength)
            .Select(index =>
                Math.Sin(index * 0.017)
                + (0.25 * Math.Cos(index * 0.0031))
                + ((index % 11) * 0.001))
            .ToArray();
        var resampler = new TbcLineResampler(
            OutputLineLength,
            interpolationMethod,
            wowLevelAdjustSmoothing: 1.5,
            nominalInputLineLength: NominalInputLineLength,
            workerThreads);
        using TbcLineResampler.ResamplingPlan plan = resampler.PrepareLineResampling(
            lineLocations,
            firstLine: 0,
            LineCount);
        double[] full = resampler.ResamplePrepared(
            source,
            plan,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        var prefixes = new double[plan.DestinationLength];
        Array.Fill(prefixes, double.NaN);

        Assert.True(checked(LineCount * SamplesPerLine) >= 64 * 1_024);
        resampler.ResampleLinePrefixes(
            source,
            lineLocations,
            firstLine: 0,
            LineCount,
            SamplesPerLine,
            prefixes,
            TbcSampleResamplingKernel.CatmullRom4Float32);

        for (int line = 0; line < LineCount; line++)
        {
            int lineStart = line * OutputLineLength;
            AssertBitExact(
                full.AsSpan(lineStart, SamplesPerLine),
                prefixes.AsSpan(lineStart, SamplesPerLine));
            Assert.All(
                prefixes.AsSpan(
                        lineStart + SamplesPerLine,
                        OutputLineLength - SamplesPerLine)
                    .ToArray(),
                static value => Assert.True(double.IsNaN(value)));
        }
    }

    [Fact(DisplayName = "Parallel Catmull-Rom4 double and shifted output is bit-exact and deterministic")]
    public void ParallelCatmullRom4DoubleAndShiftedOutputIsBitExactAndDeterministic()
    {
        const double SourcePositionShift = 0.4375;
        var serial = CreateParallelResampler(workerThreads: 1);
        var parallel = CreateParallelResampler(workerThreads: 20);
        double[] lineLocations = CreateParallelLineLocations();
        double[] guardedSource = CreateGuardedParallelSource();
        ReadOnlySpan<double> source = guardedSource.AsSpan(
            ParallelSourceGuardSamples,
            guardedSource.Length - (2 * ParallelSourceGuardSamples));

        using TbcLineResampler.ResamplingPlan serialPlan =
            serial.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);
        using TbcLineResampler.ResamplingPlan parallelPlan =
            parallel.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);

        Assert.True(serialPlan.DestinationLength > 64 * 1_024);
        Assert.Equal(serialPlan.DestinationLength, parallelPlan.DestinationLength);
        AssertBitExact(
            serialPlan.SourcePositions.AsSpan(0, serialPlan.DestinationLength),
            parallelPlan.SourcePositions.AsSpan(0, parallelPlan.DestinationLength));
        AssertBitExact(
            serialPlan.LevelAdjusts.AsSpan(
                serialPlan.PrefixSamples,
                serialPlan.DestinationLength),
            parallelPlan.LevelAdjusts.AsSpan(
                parallelPlan.PrefixSamples,
                parallelPlan.DestinationLength));

        double[] expected = serial.ResamplePrepared(
            source,
            serialPlan,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        double[] expectedShifted = serial.ResamplePreparedShifted(
            source,
            serialPlan,
            SourcePositionShift,
            TbcSampleResamplingKernel.CatmullRom4Float32);

        for (int iteration = 0; iteration < 4; iteration++)
        {
            var actual = new double[parallelPlan.DestinationLength];
            Array.Fill(actual, double.NaN);
            parallel.ResamplePrepared(
                source,
                parallelPlan,
                actual,
                TbcSampleResamplingKernel.CatmullRom4Float32);

            var actualShifted = new double[parallelPlan.DestinationLength];
            Array.Fill(actualShifted, double.NaN);
            parallel.ResamplePreparedShifted(
                source,
                parallelPlan,
                SourcePositionShift,
                actualShifted,
                TbcSampleResamplingKernel.CatmullRom4Float32);

            AssertBitExact(expected, actual);
            AssertBitExact(expectedShifted, actualShifted);
        }
    }

    [Fact(DisplayName = "Parallel Catmull-Rom4 direct UInt16 output is bit-exact and deterministic")]
    public void ParallelCatmullRom4DirectUInt16OutputIsBitExactAndDeterministic()
    {
        var serial = CreateParallelResampler(workerThreads: 1);
        var parallel = CreateParallelResampler(workerThreads: 20);
        double[] lineLocations = CreateParallelLineLocations();
        double[] guardedSource = CreateGuardedParallelSource();
        ReadOnlySpan<double> source = guardedSource.AsSpan(
            ParallelSourceGuardSamples,
            guardedSource.Length - (2 * ParallelSourceGuardSamples));
        VideoOutputConverter converter = CreateConverter();

        using TbcLineResampler.ResamplingPlan serialPlan =
            serial.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);
        using TbcLineResampler.ResamplingPlan parallelPlan =
            parallel.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);

        Assert.True(serialPlan.DestinationLength > 64 * 1_024);
        ushort[] expected = serial.ResamplePreparedToUInt16(
            source,
            serialPlan,
            converter,
            TbcSampleResamplingKernel.CatmullRom4Float32);

        for (int iteration = 0; iteration < 4; iteration++)
        {
            var actual = new ushort[parallelPlan.DestinationLength];
            Array.Fill(actual, ushort.MaxValue);
            parallel.ResamplePreparedToUInt16(
                source,
                parallelPlan,
                converter,
                actual,
                TbcSampleResamplingKernel.CatmullRom4Float32);

            AssertBitExact(expected, actual);
        }
    }

    [Fact(DisplayName = "Parallel float32 Catmull-Rom4 output is bit-exact and deterministic")]
    public void ParallelFloat32CatmullRom4OutputIsBitExactAndDeterministic()
    {
        var serial = CreateParallelResampler(workerThreads: 1);
        var parallel = CreateParallelResampler(workerThreads: 20);
        double[] lineLocations = CreateParallelLineLocations();
        float[] guardedSource = Array.ConvertAll(
            CreateGuardedParallelSource(),
            static value => (float)value);
        ReadOnlySpan<float> source = guardedSource.AsSpan(
            ParallelSourceGuardSamples,
            guardedSource.Length - (2 * ParallelSourceGuardSamples));
        VideoOutputConverter converter = CreateConverter();

        using TbcLineResampler.ResamplingPlan serialPlan =
            serial.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);
        using TbcLineResampler.ResamplingPlan parallelPlan =
            parallel.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);

        Assert.True(serialPlan.DestinationLength > 64 * 1_024);
        double[] expected = serial.ResamplePrepared(
            source,
            serialPlan,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        ushort[] expectedUInt16 = serial.ResamplePreparedToUInt16(
            source,
            serialPlan,
            converter,
            TbcSampleResamplingKernel.CatmullRom4Float32);

        for (int iteration = 0; iteration < 4; iteration++)
        {
            var actual = new double[parallelPlan.DestinationLength];
            Array.Fill(actual, double.NaN);
            parallel.ResamplePrepared(
                source,
                parallelPlan,
                actual,
                TbcSampleResamplingKernel.CatmullRom4Float32);

            var actualUInt16 = new ushort[parallelPlan.DestinationLength];
            Array.Fill(actualUInt16, ushort.MaxValue);
            parallel.ResamplePreparedToUInt16(
                source,
                parallelPlan,
                converter,
                actualUInt16,
                TbcSampleResamplingKernel.CatmullRom4Float32);

            AssertBitExact(expected, actual);
            AssertBitExact(expectedUInt16, actualUInt16);
        }
    }

    [Fact(DisplayName = "Parallel Catmull-Rom4 joins failed workers and leaves its plan reusable")]
    public void ParallelCatmullRom4JoinsFailedWorkersAndLeavesItsPlanReusable()
    {
        var serial = CreateParallelResampler(workerThreads: 1);
        var parallel = CreateParallelResampler(workerThreads: 20);
        double[] lineLocations = CreateParallelLineLocations();
        double[] validSource = CreateGuardedParallelSource();
        var tooShortSource = new double[ApproxCatmullRom4Resampler.TapCount - 1];

        using TbcLineResampler.ResamplingPlan serialPlan =
            serial.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);
        using TbcLineResampler.ResamplingPlan parallelPlan =
            parallel.PrepareLineResampling(
                lineLocations,
                ParallelFirstLine,
                ParallelLineCount);

        var failedDestination = new double[parallelPlan.DestinationLength];
        Array.Fill(failedDestination, double.NaN);
        Exception? failure = Record.Exception(
            () => parallel.ResamplePrepared(
                tooShortSource,
                parallelPlan,
                failedDestination,
                TbcSampleResamplingKernel.CatmullRom4Float32));

        Assert.NotNull(failure);
        IReadOnlyCollection<Exception> workerFailures = failure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [failure];
        Assert.Contains(
            workerFailures,
            static exception => exception is ArgumentException argument
                && argument.ParamName == "source");
        Assert.All(failedDestination, static value => Assert.True(double.IsNaN(value)));

        ReadOnlySpan<double> source = validSource.AsSpan(
            ParallelSourceGuardSamples,
            validSource.Length - (2 * ParallelSourceGuardSamples));
        double[] expected = serial.ResamplePrepared(
            source,
            serialPlan,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        double[] actual = parallel.ResamplePrepared(
            source,
            parallelPlan,
            TbcSampleResamplingKernel.CatmullRom4Float32);
        AssertBitExact(expected, actual);
    }

    private static TbcFrameSpec CreateFrameSpec()
        => new(
            "PAL",
            OutputLineLength: 24,
            OutputLineCount: 3,
            OutputSampleRateHz: 4_000_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);

    private static VideoOutputConverter CreateConverter()
        => new(
            ire0: 4_000_000.25,
            hzIre: 100_000.125,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 512.25);

    private static double[] CreateLineLocations()
        => [9.25, 53.75, 98.5, 144.0];

    private static double[] CreateSource()
        => Enumerable.Range(0, 180)
            .Select(index =>
                4_000_000.0
                + (600_000.0 * Math.Sin(index * 0.19))
                + (120_000.0 * Math.Cos(index * 0.037))
                + ((index % 5) * 731.25))
            .ToArray();

    private static float[] CreateFloatSource()
        => Array.ConvertAll(CreateSource(), static value => (float)value);

    private static TbcLineResampler CreateParallelResampler(int workerThreads)
        => new(
            ParallelOutputLineLength,
            TbcLineInterpolationMethod.Linear,
            wowLevelAdjustSmoothing: 0.0,
            nominalInputLineLength: ParallelOutputLineLength,
            workerThreads);

    private static double[] CreateParallelLineLocations()
    {
        int locationCount = ParallelFirstLine + ParallelLineCount + 1;
        var locations = new double[locationCount];
        double position = 13.375;
        for (int index = 0; index < locations.Length; index++)
        {
            locations[index] = position;
            position += ParallelOutputLineLength
                + (((index % 7) - 3) * 0.3125)
                + (0.1875 * Math.Sin(index * 0.29));
        }

        return locations;
    }

    private static double[] CreateGuardedParallelSource()
    {
        const int SourceLength = 80_000;
        var source = new double[SourceLength + (2 * ParallelSourceGuardSamples)];
        Array.Fill(source, -91_000_000.0);
        for (int index = 0; index < SourceLength; index++)
        {
            source[ParallelSourceGuardSamples + index] =
                4_000_000.0
                + (600_000.0 * Math.Sin(index * 0.013))
                + (140_000.0 * Math.Cos(index * 0.0037))
                + ((index % 17) * 379.125);
        }

        return source;
    }

    private static void AssertBitExact(
        ReadOnlySpan<double> expected,
        ReadOnlySpan<double> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "The double payload differed at the bit level.");
    }

    private static void AssertBitExact(
        ReadOnlySpan<ushort> expected,
        ReadOnlySpan<ushort> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            "The UInt16 payload differed at the bit level.");
    }
}
