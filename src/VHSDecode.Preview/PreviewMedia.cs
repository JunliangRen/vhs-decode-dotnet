namespace VHSDecode.Preview;

public sealed record PreviewMediaInfo(
    string SourceKind,
    string System,
    double FramesPerSecond,
    double DurationSeconds,
    int Width,
    int Height,
    int Crf,
    bool Interlaced,
    string DecodeBackend,
    string AccuracyProfile);

public sealed record PreviewMediaSegment(
    int GlobalIndex,
    int LocalIndex,
    double DurationSeconds,
    byte[] Data);

public sealed record PreviewSegmentWindow(
    int WindowIndex,
    byte[] InitializationSegment,
    IReadOnlyList<PreviewMediaSegment> Segments);

public interface IPreviewSegmentProvider : IAsyncDisposable
{
    PreviewMediaInfo MediaInfo { get; }

    PreviewTimeline Timeline { get; }

    Task<PreviewSegmentWindow> GenerateWindowAsync(
        int windowIndex,
        CancellationToken cancellationToken);
}
