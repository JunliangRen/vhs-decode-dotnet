using VHSDecode.Core.CommandLine;

namespace VHSDecode.Preview;

public sealed class PreviewCommandRunner
{
    public static bool IsRequested(ParsedCommand command)
        => command.Spec.Name is "vhs" or "ld"
            && command.Values.TryGetValue("preview_server", out object? value)
            && value is true;

    public async Task<int> RunAsync(
        ParsedCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            var options = new PreviewServerOptions
            {
                Port = command.Get<int>("preview_port"),
                Crf = command.Get<int>("preview_crf")
            };
            DecodePreviewSegmentProvider provider = await DecodePreviewSegmentProvider.CreateAsync(
                command,
                options,
                cancellationToken).ConfigureAwait(false);
            await using PreviewHttpServer server = await PreviewHttpServer.StartAsync(
                provider,
                options,
                cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Preview server: {server.BaseAddress}");
            output.WriteLine($"HLS playlist: {server.PlaylistAddress}");
            output.WriteLine(
                $"Mode: {server.MediaInfo.SourceKind} {server.MediaInfo.System}, "
                + $"{server.MediaInfo.Width}x{server.MediaInfo.Height} "
                + $"{server.MediaInfo.FramesPerSecond:0.###} fps interlaced, "
                + $"CRF {server.MediaInfo.Crf}, "
                + $"{server.MediaInfo.DecodeBackend}, {server.MediaInfo.AccuracyProfile}");
            output.WriteLine("Press Ctrl+C to stop. Preview mode does not create TBC, JSON, SQLite, EFM, or audio outputs.");
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await server.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            error.WriteLine($"Preview server failed: {ex.Message}");
            return 1;
        }
    }
}
