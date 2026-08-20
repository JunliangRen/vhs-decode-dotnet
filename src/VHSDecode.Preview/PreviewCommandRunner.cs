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
                cancellationToken,
                output).ConfigureAwait(false);
            var fpsDisplay = new PreviewRealtimeFpsDisplay(
                output,
                provider.Timeline.FramesPerSecond);
            provider.WindowGenerationUpdated += fpsDisplay.Report;
            try
            {
                await using PreviewHttpServer server = await PreviewHttpServer.StartAsync(
                    provider,
                    options,
                    cancellationToken).ConfigureAwait(false);
                output.WriteLine($"Preview server: {server.BaseAddress}");
                output.WriteLine($"HLS playlist: {server.PlaylistAddress}");
                output.WriteLine(
                    $"Mode: {server.MediaInfo.SourceKind} {server.MediaInfo.System}, "
                    + $"{server.MediaInfo.Width}x{server.MediaInfo.Height} "
                    + $"{server.MediaInfo.FramesPerSecond:0.###} fps progressive, "
                    + $"CRF {server.MediaInfo.Crf}, "
                    + $"{server.MediaInfo.DecodeBackend}, {server.MediaInfo.AccuracyProfile}");
                output.WriteLine($"Preview encoder: {server.MediaInfo.EncodeBackend}");
                if (provider.CudaFastEnabled)
                {
                    output.WriteLine(
                        "CUDA-FAST preview: persistent 40 -> 20 MSPS GPU DSP, block-linear NV12/NVENC, FFmpeg copy-mux.");
                }
                else
                {
                    output.WriteLine(provider.IppFastEnabled
                        ? "IPP-FAST: enabled (runtime initialization succeeded)"
                        : "IPP-FAST: disabled (Exact backend active)");
                }
                output.WriteLine(
                    $"Preview RF rate: {provider.SourceSampleRateHz / 1_000_000.0:0.###}"
                    + $" -> {provider.DecodeSampleRateHz / 1_000_000.0:0.###} MSPS");
                output.WriteLine(provider.CudaFastEnabled
                    ? "Decoder threads: GPU-managed (one persistent CUDA preview context)"
                    : $"Decoder threads: {provider.DecoderThreads}");
                output.WriteLine("Press Ctrl+C to stop. Preview mode does not create TBC, JSON, SQLite, EFM, or audio outputs.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                fpsDisplay.Start();
                await server.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                provider.WindowGenerationUpdated -= fpsDisplay.Report;
                fpsDisplay.Complete();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            error.WriteLine($"Preview server failed: {ex.Message}");
            return 1;
        }
    }
}
