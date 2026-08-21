using System.Diagnostics;
using System.Globalization;
using VHSDecode.Core.Formats;

namespace VHSDecode.Preview;

internal sealed class FfmpegHlsWindowEncoder
{
    private readonly string _ffmpegPath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _crf;
    private readonly bool _isPalColorSystem;
    private readonly PreviewTimeline _timeline;
    private readonly PreviewEncoderBackend _encoderBackend;

    internal FfmpegHlsWindowEncoder(
        string ffmpegPath,
        int width,
        int height,
        int crf,
        string system,
        PreviewTimeline timeline,
        PreviewEncoderBackend encoderBackend)
    {
        _ffmpegPath = PreviewServerOptions.NormalizeExecutablePath(ffmpegPath);
        _width = width;
        _height = height;
        _crf = crf;
        _isPalColorSystem = FormatCatalog.NormalizeSystem(system).StartsWith(
            "PAL",
            StringComparison.Ordinal);
        _timeline = timeline;
        _encoderBackend = encoderBackend;
    }

    internal PreviewSegmentWindow Encode(
        int windowIndex,
        Action<Stream> writeFrames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeFrames);
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
                _timeline.FrameCountInWindow(windowIndex),
                windowIndex);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                writeFrames(process.StandardInput.BaseStream);
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
                    $"FFmpeg HLS encoder failed with exit code {process.ExitCode}: {diagnostic.Trim()}");
            }

            if (!File.Exists(initPath) || !File.Exists(playlistPath))
            {
                throw new InvalidOperationException(
                    "FFmpeg completed without producing the expected fMP4 HLS window.");
            }

            string[] generatedSegmentNames = File.ReadLines(playlistPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToArray();
            int expectedSegments = _timeline.SegmentCountInWindow(windowIndex);
            if (generatedSegmentNames.Length != expectedSegments)
            {
                throw new InvalidOperationException(
                    $"FFmpeg produced {generatedSegmentNames.Length} HLS segment(s); expected {expectedSegments}.");
            }

            int firstGlobal = _timeline.FirstSegmentInWindow(windowIndex);
            var segments = new List<PreviewMediaSegment>(expectedSegments);
            for (int local = 0; local < expectedSegments; local++)
            {
                string segmentPath = Path.Combine(
                    temporaryDirectory,
                    Path.GetFileName(generatedSegmentNames[local]));
                if (!File.Exists(segmentPath))
                {
                    throw new InvalidOperationException(
                        $"FFmpeg playlist referenced a missing segment '{generatedSegmentNames[local]}'.");
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

    private Process StartProcess(
        string playlistPath,
        string initPath,
        string segmentPattern,
        int frameCount,
        int windowIndex)
    {
        int keyFrameInterval = _timeline.FramesPerSegment;
        string frameRate = _timeline.FramesPerSecond.ToString("R", CultureInfo.InvariantCulture);
        string segmentDuration = (keyFrameInterval / _timeline.FramesPerSecond)
            .ToString("R", CultureInfo.InvariantCulture);
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
        string[] arguments = BuildArguments(
            playlistPath,
            initPath,
            segmentPattern,
            frameCount,
            keyFrameInterval,
            frameRate,
            segmentDuration);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start FFmpeg.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Could not start FFmpeg at '{_ffmpegPath}'.",
                ex);
        }

        return process;
    }

    internal string[] BuildArguments(
        string playlistPath,
        string initPath,
        string segmentPattern,
        int frameCount,
        int keyFrameInterval,
        string frameRate,
        string segmentDuration)
    {
        string colorStandard = _isPalColorSystem ? "bt470bg" : "smpte170m";
        int colorCode = _isPalColorSystem ? 5 : 6;
        string x264Parameters = $"colorprim={colorStandard}"
            + ":transfer=bt709"
            + $":colormatrix={colorStandard}"
            + ":fullrange=off";
        int outputFrameCount = checked(frameCount * 2);
        int outputKeyFrameInterval = checked(keyFrameInterval * 2);
        var arguments = new List<string>();
        if (_encoderBackend == PreviewEncoderBackend.Qsv)
        {
            arguments.AddRange(
            [
                "-init_hw_device", "qsv=preview_qsv",
                "-filter_hw_device", "preview_qsv"
            ]);
        }

        arguments.AddRange(
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-f", "rawvideo",
            "-pixel_format", "yuv420p",
            "-video_size", $"{_width}x{_height}",
            "-framerate", frameRate,
            "-i", "pipe:0",
            "-an"
        ]);
        arguments.AddRange(BuildBackendArguments(x264Parameters));
        arguments.AddRange(
        [
            "-profile:v", "main",
            "-level:v", "3.1",
            "-g", outputKeyFrameInterval.ToString(CultureInfo.InvariantCulture),
            "-keyint_min", outputKeyFrameInterval.ToString(CultureInfo.InvariantCulture),
            "-color_primaries", colorStandard,
            "-color_trc", "bt709",
            "-colorspace", colorStandard,
            "-color_range", "tv",
            "-bsf:v", $"h264_metadata=video_full_range_flag=0:colour_primaries={colorCode}:transfer_characteristics=1:matrix_coefficients={colorCode}",
            "-frames:v", outputFrameCount.ToString(CultureInfo.InvariantCulture),
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
        ]);
        return [.. arguments];
    }

    private string[] BuildBackendArguments(string x264Parameters)
        => _encoderBackend switch
        {
            PreviewEncoderBackend.Nvenc =>
            [
                "-vf", "setfield=tff,format=nv12,hwupload_cuda,yadif_cuda=mode=send_field:parity=tff:deint=all",
                "-c:v", "h264_nvenc",
                "-preset", "p1",
                "-tune", "ull",
                "-rc", "constqp",
                "-qp", _crf.ToString(CultureInfo.InvariantCulture),
                "-bf", "0",
                "-pix_fmt", "cuda"
            ],
            PreviewEncoderBackend.Qsv =>
            [
                "-vf", "setfield=tff,tpad=stop_mode=clone:stop=1,format=nv12,hwupload=extra_hw_frames=64,vpp_qsv=deinterlace=advanced:rate=field",
                "-c:v", "h264_qsv",
                "-preset", "veryfast",
                "-global_quality", Math.Max(1, _crf).ToString(CultureInfo.InvariantCulture),
                "-look_ahead", "0",
                "-async_depth", "1",
                "-forced_idr", "1",
                "-bf", "0",
                "-pix_fmt", "qsv"
            ],
            PreviewEncoderBackend.Amf =>
            [
                "-vf", "setfield=tff,yadif=mode=send_field:parity=tff:deint=all",
                "-c:v", "h264_amf",
                "-usage", "ultralowlatency",
                "-quality", "speed",
                "-rc", "cqp",
                "-qp_i", _crf.ToString(CultureInfo.InvariantCulture),
                "-qp_p", _crf.ToString(CultureInfo.InvariantCulture),
                "-qp_b", _crf.ToString(CultureInfo.InvariantCulture),
                "-bf", "0",
                "-pix_fmt", "yuv420p"
            ],
            PreviewEncoderBackend.Libx264 =>
            [
                "-vf", "setfield=tff,yadif=mode=send_field:parity=tff:deint=all",
                "-c:v", "libx264",
                "-preset", "ultrafast",
                "-tune", "zerolatency",
                "-crf", _crf.ToString(CultureInfo.InvariantCulture),
                "-threads", "2",
                "-x264-params", x264Parameters,
                "-sc_threshold", "0",
                "-pix_fmt", "yuv420p"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(_encoderBackend))
        };

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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort after an encoder/decode failure.
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("vhsdecode-preview-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove an unexpected preview temporary directory: {fullPath}");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
