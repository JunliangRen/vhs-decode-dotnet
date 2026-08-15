namespace VHSDecode.Preview;

internal enum PreviewEncoderBackend
{
    Nvenc,
    Qsv,
    Amf,
    Libx264
}

internal static class PreviewEncoderSelector
{
    private static readonly IReadOnlyList<PreviewEncoderBackend> CandidateOrderValue =
        Array.AsReadOnly(
        new[]
        {
            PreviewEncoderBackend.Nvenc,
            PreviewEncoderBackend.Qsv,
            PreviewEncoderBackend.Amf,
            PreviewEncoderBackend.Libx264
        });

    internal static IReadOnlyList<PreviewEncoderBackend> CandidateOrder => CandidateOrderValue;

    internal static async Task<PreviewEncoderBackend> SelectAsync(
        string ffmpegPath,
        int width,
        int height,
        int crf,
        string system,
        double framesPerSecond,
        CancellationToken cancellationToken)
        => await SelectAsync(
            (candidate, token) => Task.Run(
                () => Probe(
                    ffmpegPath,
                    width,
                    height,
                    crf,
                    system,
                    framesPerSecond,
                    candidate,
                    token),
                token),
            cancellationToken).ConfigureAwait(false);

    internal static async Task<PreviewEncoderBackend> SelectAsync(
        Func<PreviewEncoderBackend, CancellationToken, Task> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var diagnostics = new List<string>();
        foreach (PreviewEncoderBackend candidate in CandidateOrderValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await probe(candidate, cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                diagnostics.Add($"{DisplayName(candidate)}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "FFmpeg has no usable 2x progressive H.264 preview pipeline. "
            + string.Join(" | ", diagnostics));
    }

    internal static string DisplayName(PreviewEncoderBackend backend)
        => backend switch
        {
            PreviewEncoderBackend.Nvenc => "NVENC + CUDA YADIF x2",
            PreviewEncoderBackend.Qsv => "QSV + VPP advanced x2",
            PreviewEncoderBackend.Amf => "AMF + CPU YADIF x2",
            PreviewEncoderBackend.Libx264 => "libx264 + CPU YADIF x2",
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };

    private static void Probe(
        string ffmpegPath,
        int width,
        int height,
        int crf,
        string system,
        double framesPerSecond,
        PreviewEncoderBackend backend,
        CancellationToken cancellationToken)
    {
        const int probeFrameCount = 4;
        double probeDuration = (probeFrameCount + 0.25) / framesPerSecond;
        var timeline = new PreviewTimeline(
            probeDuration,
            framesPerSecond,
            probeDuration,
            segmentsPerWindow: 1);
        var encoder = new FfmpegHlsWindowEncoder(
            ffmpegPath,
            width,
            height,
            crf,
            system,
            timeline,
            backend);
        int lumaBytes = checked(width * height);
        byte[] frame = new byte[checked(lumaBytes * 3 / 2)];
        Array.Fill(frame, (byte)16, 0, lumaBytes);
        Array.Fill(frame, (byte)128, lumaBytes, frame.Length - lumaBytes);
        _ = encoder.Encode(
            windowIndex: 0,
            stream =>
            {
                for (int index = 0; index < timeline.FrameCountInWindow(0); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Write(frame);
                }
            },
            cancellationToken);
    }
}
