namespace VHSDecode.Preview;

public sealed record PreviewServerOptions
{
    public const int DefaultPort = 8080;

    public const int DefaultPortFallbackCount = 100;

    public int Port { get; init; } = DefaultPort;

    public int PortFallbackCount { get; init; } = DefaultPortFallbackCount;

    public double SegmentSeconds { get; init; } = 2.0;

    public int SegmentsPerWindow { get; init; } = 1;

    public int CacheWindowCount { get; init; } = 3;

    public int MaximumConcurrentWindowBuilds { get; init; } = 2;

    public int Crf { get; init; } = 31;

    public string FfmpegPath { get; init; } =
        Environment.GetEnvironmentVariable("VHSDECODE_FFMPEG") ?? "ffmpeg";

    public string FfprobePath { get; init; } =
        Environment.GetEnvironmentVariable("VHSDECODE_FFPROBE") ?? "ffprobe";

    public void Validate()
    {
        if (Port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "Preview port must be between 0 and 65535.");
        }

        if (PortFallbackCount is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PortFallbackCount),
                "Preview port fallback count must be between 0 and 1000.");
        }

        if (!double.IsFinite(SegmentSeconds) || SegmentSeconds is < 0.5 or > 10.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SegmentSeconds),
                "Preview segment length must be between 0.5 and 10 seconds.");
        }

        if (SegmentsPerWindow is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SegmentsPerWindow),
                "Preview windows must contain between 1 and 16 segments.");
        }

        if (CacheWindowCount is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CacheWindowCount),
                "Preview cache must retain between 1 and 32 windows.");
        }

        if (MaximumConcurrentWindowBuilds is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConcurrentWindowBuilds),
                "Preview window concurrency must be between 1 and 8.");
        }

        if (Crf is < 0 or > 51)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Crf),
                "Preview CRF must be between 0 and 51.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(FfprobePath);
    }
}
