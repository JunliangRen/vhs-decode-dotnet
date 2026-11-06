using System.Reflection;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscIppParallelLineTests
{
    private const int LineCount = 273;
    private const int BurstLines = 266;
    private const int LineSamples = 2542;
    private const int FirstLine = 1000;
    private const double FscMHz = 3.5795454545454546;

    [Fact]
    public void HSyncLocationsAndFillBitsMatchOnStockGeometry()
    {
        using TbcFieldDecodePipeline pipeline = CreatePipeline(out VideoOutputConverter converter);
        (RfDecodedSpan span, LineLocationResult locations) = CreateField(phase: 0.25);
        long[] input = Bits(span.VideoLowPass!);
        long[] initial = Bits(locations.Locations);
        bool[] initialFilled = locations.Filled.ToArray();
        var expected = HSync(pipeline, span, locations, converter, parallel: false);
        var actual = HSync(pipeline, span, locations, converter, parallel: true);
        EqualLines(expected, actual);
        Assert.NotEqual(initial[10], BitConverter.DoubleToInt64Bits(actual.Locations[10]));
        Assert.True(actual.Filled[68]);
        Assert.Equal(initial, Bits(locations.Locations));
        Assert.Equal(initialFilled, locations.Filled);
        Assert.Equal(input, Bits(span.VideoLowPass!));
    }

    [Fact]
    public void BurstSearchMatchesEachScalarResultAndOrderedStateAcrossFields()
    {
        using TbcFieldDecodePipeline serial = CreatePipeline(out VideoOutputConverter converter);
        using TbcFieldDecodePipeline parallel = CreatePipeline(out _);
        foreach ((double phase, bool first) in new[] { (0.25, true), (1.5, false), (3.0, true) })
        {
            (RfDecodedSpan span, LineLocationResult raw) = CreateField(phase);
            var locations = HSync(serial, span, raw, converter, parallel: false);
            long[] inputLocations = Bits(locations.Locations);
            long[] inputBurst = Bits(span.VideoBurst!);
            double previous = serial.CaptureState().LaserDiscNtscPhaseAdjustMedian;
            int fieldBoundary = first ? 263 : 262;
            var results = ((bool Found, bool Rising, double PhaseAdjustment)[])Invoke(
                parallel, "SearchLaserDiscBurstLinesInParallel",
                span.Video, span.VideoBurst!, locations.Locations, BurstLines, previous, fieldBoundary,
                FscMHz, converter.HzIre)!;
            Assert.Contains(results, item => item.Found);
            Assert.Contains(results, item => !item.Found);
            for (int line = 0; line < BurstLines; line++)
            {
                bool found = serial.TryComputeLaserDiscLineBurst(
                    span.Video, span.VideoBurst!, locations.Locations, line, previous,
                    fieldBoundary, FscMHz, converter.HzIre, out bool rising, out double adjustment);
                Assert.Equal(found, results[line].Found);
                Assert.Equal(rising, results[line].Rising);
                Assert.Equal(BitConverter.DoubleToInt64Bits(adjustment),
                    BitConverter.DoubleToInt64Bits(results[line].PhaseAdjustment));
            }

            var expected = Burst(serial, span, locations, first, converter, useParallel: false);
            var actual = Burst(parallel, span, locations, first, converter, useParallel: true);
            EqualLines(expected.LineLocations, actual.LineLocations);
            Assert.Equal(expected.FieldPhaseId, actual.FieldPhaseId);
            Assert.Equal(BitConverter.DoubleToInt64Bits(serial.CaptureState().LaserDiscNtscPhaseAdjustMedian),
                BitConverter.DoubleToInt64Bits(parallel.CaptureState().LaserDiscNtscPhaseAdjustMedian));
            Assert.Equal(inputLocations, Bits(locations.Locations));
            Assert.Equal(inputBurst, Bits(span.VideoBurst!));
        }
    }

    [Fact]
    public void NonstandardShapesRetainOriginalFallback()
    {
        using TbcFieldDecodePipeline serial = CreatePipeline(out VideoOutputConverter converter);
        using TbcFieldDecodePipeline parallel = CreatePipeline(out _);
        (RfDecodedSpan span, LineLocationResult locations) = CreateField(phase: 0.5);
        var shortLines = new LineLocationResult(locations.Locations[..12], locations.Filled[..12]);
        EqualLines(HSync(serial, span, shortLines, converter, parallel: false),
            HSync(parallel, span, shortLines, converter, parallel: true));
        var oldBurst = Burst(serial, span, shortLines, true, converter, useParallel: false);
        var newBurst = Burst(parallel, span, shortLines, true, converter, useParallel: true);
        EqualLines(oldBurst.LineLocations, newBurst.LineLocations);
        Assert.Equal(oldBurst.FieldPhaseId, newBurst.FieldPhaseId);

        var withoutReference = span with { VideoLowPass = null, VideoBurst = null };
        Assert.Same(locations, HSync(parallel, withoutReference, locations, converter, parallel: true));
        Assert.Same(locations, Burst(parallel, withoutReference, locations, true, converter, useParallel: true).LineLocations);
    }

    [Fact]
    public void AllRangesFinishBeforeTheFirstLineOrderFailureIsPropagated()
    {
        var first = new InvalidOperationException("range zero");
        var later = new ArgumentException("range two");
        var visits = new int[4];
        Action<int, int> process = (start, end) =>
        {
            Assert.Equal(10, end - start);
            Interlocked.Increment(ref visits[start / 10]);
            if (start == 0)
            {
                throw first;
            }

            if (start == 20)
            {
                throw later;
            }
        };
        var error = Assert.Throws<TargetInvocationException>(() => { RunRanges(process); });
        Assert.Same(first, error.InnerException);
        Assert.Equal(new[] { 1, 1, 1, 1 }, visits);
    }

    [Fact]
    public void InterruptedCallerStillDrainsAQueuedRange()
    {
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var callerFinished = new ManualResetEventSlim();
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            try
            {
                RunRanges((start, _) =>
                {
                    if (start == 10)
                    {
                        workerEntered.Set();
                        if (!releaseWorker.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException("Range was not released");
                        }
                    }
                });
            }
            catch (TargetInvocationException error)
            {
                failure = error.InnerException;
            }
            finally
            {
                callerFinished.Set();
            }
        }) { IsBackground = true };
        caller.Start();
        try
        {
            Assert.True(workerEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => (caller.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            caller.Interrupt();
            Assert.False(callerFinished.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            releaseWorker.Set();
            Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.IsType<ThreadInterruptedException>(failure);
    }

    private static TbcFieldDecodePipeline CreatePipeline(out VideoOutputConverter converter)
    {
        var analyzer = new SyncAnalyzer(40_000_000.0, 63.5555556, 4.7, 2.3, 27.1);
        converter = new VideoOutputConverter(0.0, 1.0, 256, -40.0, 10.0);
        var spec = new TbcFrameSpec(
            "NTSC", OutputLineLength: 4, OutputLineCount: 263, OutputSampleRateHz: 14_318_180.0,
            ColourBurstStart: null, ColourBurstEnd: null, ActiveVideoStart: null, ActiveVideoEnd: null);
        return new TbcFieldDecodePipeline(
            analyzer, new TbcFieldRenderer(spec, converter), converter, "NTSC",
            TbcDropoutDetectionOptions.Disabled, laserDiscNtscBurstRefineOptions: new(FscMHz),
            decodeType: "ld", workerThreads: 4);
    }

    private static (RfDecodedSpan, LineLocationResult) CreateField(double phase)
    {
        var video = new double[FirstLine + (LineCount * LineSamples)];
        var lowPass = new double[video.Length];
        var burst = new double[video.Length];
        var positions = new double[LineCount];
        var filled = new bool[LineCount];
        for (int line = 0; line < LineCount; line++)
        {
            int start = FirstLine + (line * LineSamples);
            positions[line] = start;
            lowPass.AsSpan(start, 190).Fill(-40.0);
            for (int sample = 210; sample < 350; sample++)
            {
                burst[start + sample] = 10.0 * Math.Sin((sample * Math.Tau * FscMHz / 40.0) + phase + ((line & 1) * Math.PI));
            }
        }

        positions[5] = -0.0;
        positions[68] = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8_0000_0000_0123));
        positions[136] = -100.0;
        positions[204] = double.PositiveInfinity;
        positions[^1] = video.Length - 3.0;
        filled[67] = true;
        lowPass[FirstLine + (135 * LineSamples) + 10] = double.NegativeInfinity;
        burst[FirstLine + (198 * LineSamples) + 260] = double.NaN;
        video[FirstLine + (200 * LineSamples) + 260] = 1000.0;
        return (new RfDecodedSpan(0, [], video, video, VideoLowPass: lowPass, VideoBurst: burst),
            new LineLocationResult(positions, filled));
    }

    private static LineLocationResult HSync(TbcFieldDecodePipeline pipeline, RfDecodedSpan span,
        LineLocationResult locations, VideoOutputConverter converter, bool parallel)
        => (LineLocationResult)Invoke(pipeline, parallel
            ? "RefineLaserDiscLineLocationsFromHSyncParallel" : "RefineLaserDiscLineLocationsFromHSync",
            span, locations, converter)!;

    private static (LineLocationResult LineLocations, int? FieldPhaseId) Burst(TbcFieldDecodePipeline pipeline,
        RfDecodedSpan span, LineLocationResult locations, bool first, VideoOutputConverter converter, bool useParallel)
        => ((LineLocationResult, int?))Invoke(pipeline, "RefineLaserDiscNtscLineLocationsFromBurst",
            span, locations, first, converter, useParallel)!;

    private static void EqualLines(LineLocationResult expected, LineLocationResult actual)
    {
        Assert.Equal(Bits(expected.Locations), Bits(actual.Locations));
        Assert.Equal(expected.Filled, actual.Filled);
    }

    private static long[] Bits(double[] values) => values.Select(BitConverter.DoubleToInt64Bits).ToArray();

    private static object? Invoke(TbcFieldDecodePipeline pipeline, string name, params object?[] arguments)
        => typeof(TbcFieldDecodePipeline).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(pipeline, arguments);

    private static void RunRanges(Action<int, int> process)
        => typeof(TbcFieldDecodePipeline)
            .GetMethod("RunFourLaserDiscLineRanges", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [40, process]);
}
