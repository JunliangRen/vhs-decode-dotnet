using System.Diagnostics;
using System.Globalization;

namespace VHSDecode.Preview;

internal sealed class FfmpegH264HlsWindowMuxer
{
    private readonly string _ffmpegPath;
    private readonly PreviewTimeline _timeline;

    internal FfmpegH264HlsWindowMuxer(
        string ffmpegPath,
        PreviewTimeline timeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        _ffmpegPath = PreviewServerOptions.NormalizeExecutablePath(ffmpegPath);
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
    }

    internal PreviewSegmentWindow Mux(
        int windowIndex,
        Action<Stream> writeH264,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeH264);
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string playlistPath = Path.Combine(temporaryDirectory, "index.m3u8");
            string initPath = Path.Combine(temporaryDirectory, "init.mp4");
            string segmentPattern = Path.Combine(temporaryDirectory, "segment-%03d.m4s");
            using Process process = StartProcess(
                playlistPath,
                initPath,
                segmentPattern,
                windowIndex);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                writeH264(process.StandardInput.BaseStream);
                process.StandardInput.Close();
                process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch
            {
                TryKill(process);
                throw;
            }

            string diagnostic = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg H.264 copy-mux failed with exit code {process.ExitCode}: {diagnostic.Trim()}");
            }
            if (!File.Exists(initPath) || !File.Exists(playlistPath))
            {
                throw new InvalidOperationException(
                    "FFmpeg copy-mux completed without the expected fMP4 HLS window.");
            }

            string[] segmentNames = File.ReadLines(playlistPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToArray();
            int expectedSegments = _timeline.SegmentCountInWindow(windowIndex);
            if (segmentNames.Length != expectedSegments)
            {
                throw new InvalidOperationException(
                    $"FFmpeg copy-mux produced {segmentNames.Length} HLS segment(s); expected {expectedSegments}.");
            }

            int firstGlobal = _timeline.FirstSegmentInWindow(windowIndex);
            var segments = new List<PreviewMediaSegment>(expectedSegments);
            for (int local = 0; local < expectedSegments; local++)
            {
                string segmentPath = Path.Combine(
                    temporaryDirectory,
                    Path.GetFileName(segmentNames[local]));
                if (!File.Exists(segmentPath))
                {
                    throw new InvalidOperationException(
                        $"FFmpeg copy-mux referenced missing segment '{segmentNames[local]}'.");
                }

                int global = firstGlobal + local;
                byte[] data = File.ReadAllBytes(segmentPath);
                Fmp4TimelineRebaser.RebaseInPlace(
                    data,
                    _timeline.WindowStartSeconds(windowIndex));
                segments.Add(new PreviewMediaSegment(
                    global,
                    local,
                    _timeline.SegmentDurationSeconds(global),
                    data));
            }

            return new PreviewSegmentWindow(
                windowIndex,
                File.ReadAllBytes(initPath),
                segments);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    internal string[] BuildArguments(
        string playlistPath,
        string initPath,
        string segmentPattern,
        int windowIndex)
    {
        double outputFramesPerSecond = _timeline.FramesPerSecond * 2.0;
        string frameRate = outputFramesPerSecond.ToString(
            "R",
            CultureInfo.InvariantCulture);
        string segmentDuration = (_timeline.FramesPerSegment / _timeline.FramesPerSecond)
            .ToString("R", CultureInfo.InvariantCulture);
        int outputFrames = checked(_timeline.FrameCountInWindow(windowIndex) * 2);
        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "+genpts",
            "-r", frameRate,
            "-f", "h264",
            "-i", "pipe:0",
            "-an",
            "-c:v", "copy",
            "-frames:v", outputFrames.ToString(CultureInfo.InvariantCulture),
            "-f", "hls",
            "-hls_time", segmentDuration,
            "-hls_list_size", "0",
            "-hls_playlist_type", "vod",
            "-hls_segment_type", "fmp4",
            "-hls_flags", "independent_segments",
            // FFmpeg versions disagree on whether this is relative to the
            // playlist or process directory. StartProcess pins both together.
            "-hls_fmp4_init_filename", Path.GetFileName(initPath),
            "-hls_segment_filename", segmentPattern,
            playlistPath
        ];
    }

    private Process StartProcess(
        string playlistPath,
        string initPath,
        string segmentPattern,
        int windowIndex)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            WorkingDirectory = Path.GetDirectoryName(playlistPath)
                ?? throw new InvalidOperationException("Playlist path has no parent directory."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (string argument in BuildArguments(
            playlistPath,
            initPath,
            segmentPattern,
            windowIndex))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start FFmpeg copy-mux.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Could not start FFmpeg at '{_ffmpegPath}'.",
                ex);
        }

        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Best effort after decode/mux failure.
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith(
                "vhsdecode-preview-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove an unexpected preview directory: {fullPath}");
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
