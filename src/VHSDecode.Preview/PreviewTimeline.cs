namespace VHSDecode.Preview;

public sealed class PreviewTimeline
{
    public PreviewTimeline(
        double sourceDurationSeconds,
        double framesPerSecond,
        double requestedSegmentSeconds,
        int segmentsPerWindow)
    {
        if (!double.IsFinite(sourceDurationSeconds) || sourceDurationSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceDurationSeconds),
                "Preview input must have a positive finite duration.");
        }

        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        if (!double.IsFinite(requestedSegmentSeconds) || requestedSegmentSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSegmentSeconds));
        }

        if (segmentsPerWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentsPerWindow));
        }

        FramesPerSecond = framesPerSecond;
        FramesPerSegment = Math.Max(
            1,
            checked((int)Math.Round(
                requestedSegmentSeconds * framesPerSecond,
                MidpointRounding.AwayFromZero)));
        SegmentsPerWindow = segmentsPerWindow;
        TotalFrames = Math.Max(
            1L,
            checked((long)Math.Floor(sourceDurationSeconds * framesPerSecond)));
        long segmentCount = DivideRoundUp(TotalFrames, FramesPerSegment);
        if (segmentCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceDurationSeconds),
                "Preview source contains too many HLS segments.");
        }

        SegmentCount = (int)segmentCount;
        WindowCount = checked((int)DivideRoundUp(SegmentCount, SegmentsPerWindow));
        DurationSeconds = TotalFrames / FramesPerSecond;
    }

    public double FramesPerSecond { get; }

    public int FramesPerSegment { get; }

    public int SegmentsPerWindow { get; }

    public long TotalFrames { get; }

    public int SegmentCount { get; }

    public int WindowCount { get; }

    public double DurationSeconds { get; }

    public int FirstSegmentInWindow(int windowIndex)
    {
        ValidateWindowIndex(windowIndex);
        return checked(windowIndex * SegmentsPerWindow);
    }

    public int SegmentCountInWindow(int windowIndex)
    {
        int first = FirstSegmentInWindow(windowIndex);
        return Math.Min(SegmentsPerWindow, SegmentCount - first);
    }

    public int FrameCountInSegment(int segmentIndex)
    {
        ValidateSegmentIndex(segmentIndex);
        long firstFrame = checked((long)segmentIndex * FramesPerSegment);
        return checked((int)Math.Min(FramesPerSegment, TotalFrames - firstFrame));
    }

    public double SegmentDurationSeconds(int segmentIndex)
        => FrameCountInSegment(segmentIndex) / FramesPerSecond;

    public long FirstFrameInWindow(int windowIndex)
        => checked((long)FirstSegmentInWindow(windowIndex) * FramesPerSegment);

    public int FrameCountInWindow(int windowIndex)
    {
        int first = FirstSegmentInWindow(windowIndex);
        int count = SegmentCountInWindow(windowIndex);
        int frames = 0;
        for (int i = 0; i < count; i++)
        {
            frames = checked(frames + FrameCountInSegment(first + i));
        }

        return frames;
    }

    public double WindowStartSeconds(int windowIndex)
        => FirstFrameInWindow(windowIndex) / FramesPerSecond;

    public double WindowDurationSeconds(int windowIndex)
        => FrameCountInWindow(windowIndex) / FramesPerSecond;

    public int WindowForSegment(int segmentIndex)
    {
        ValidateSegmentIndex(segmentIndex);
        return segmentIndex / SegmentsPerWindow;
    }

    public int LocalSegmentIndex(int segmentIndex)
    {
        ValidateSegmentIndex(segmentIndex);
        return segmentIndex % SegmentsPerWindow;
    }

    private void ValidateWindowIndex(int windowIndex)
    {
        if ((uint)windowIndex >= (uint)WindowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(windowIndex));
        }
    }

    private void ValidateSegmentIndex(int segmentIndex)
    {
        if ((uint)segmentIndex >= (uint)SegmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }
    }

    private static long DivideRoundUp(long value, long divisor)
        => checked((value + divisor - 1) / divisor);
}
