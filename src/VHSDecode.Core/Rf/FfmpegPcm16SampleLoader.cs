using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VHSDecode.Core.Rf;

public sealed class FfmpegPcm16SampleLoader : IRfSampleLoader, IDisposable
{
    public const int ContainerAudioSampleRateHz = 40_000;
    public const int DefaultRewindSize = 2 * 1024 * 1024;
    public const int DefaultSeekThreshold = 40 * 1024 * 1024;

    private readonly string _filename;
    private readonly Func<string, long, int, byte[]?>? _readSegment;
    private readonly Func<string, long, Stream>? _openOutput;
    private readonly Func<int?>? _exitCodeAfterOutputEnd;
    private readonly Func<string>? _stderrProvider;
    private readonly StringBuilder _stderr = new();
    private ContainerAudioInfo? _containerAudioInfo;
    private Stream? _output;
    private Process? _process;
    private long _positionBytes;
    private byte[] _rewindBuffer = [];
    private bool _disposed;

    public FfmpegPcm16SampleLoader(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException("Input filename must not be empty.", nameof(filename));
        }

        _filename = filename;
        _openOutput = OpenFfmpegOutput;
        RewindSize = DefaultRewindSize;
        SeekThreshold = DefaultSeekThreshold;
    }

    public FfmpegPcm16SampleLoader(string filename, Func<string, long, int, byte[]?> readSegment)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException("Input filename must not be empty.", nameof(filename));
        }

        _filename = filename;
        _readSegment = readSegment ?? throw new ArgumentNullException(nameof(readSegment));
        RewindSize = DefaultRewindSize;
        SeekThreshold = DefaultSeekThreshold;
    }

    public FfmpegPcm16SampleLoader(
        string filename,
        Func<string, long, Stream> openOutput,
        int rewindSize = DefaultRewindSize,
        int seekThreshold = DefaultSeekThreshold,
        Func<int?>? exitCodeAfterOutputEnd = null,
        Func<string>? stderrProvider = null)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException("Input filename must not be empty.", nameof(filename));
        }

        _filename = filename;
        _openOutput = openOutput ?? throw new ArgumentNullException(nameof(openOutput));
        _exitCodeAfterOutputEnd = exitCodeAfterOutputEnd;
        _stderrProvider = stderrProvider;
        RewindSize = rewindSize > 0 ? rewindSize : throw new ArgumentOutOfRangeException(nameof(rewindSize));
        SeekThreshold = seekThreshold > 0 ? seekThreshold : throw new ArgumentOutOfRangeException(nameof(seekThreshold));
    }

    ~FfmpegPcm16SampleLoader()
    {
        Dispose();
    }

    public int RewindSize { get; }

    public int SeekThreshold { get; }

    public double[]? Read(Stream stream, long sample, int readLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Sample offset must be non-negative.");
        }

        if (readLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readLength), "Read length must be non-negative.");
        }

        if (readLength == 0)
        {
            return [];
        }

        if (_readSegment is not null)
        {
            return ReadSegment(sample, readLength);
        }

        return ReadStreaming(sample, readLength);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseProcess();
        GC.SuppressFinalize(this);
    }

    public static string FormatSeekSeconds(
        long sample,
        int containerAudioSampleRateHz = ContainerAudioSampleRateHz)
    {
        if (sample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), "Sample offset must be non-negative.");
        }

        if (containerAudioSampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(containerAudioSampleRateHz),
                "Container audio sample rate must be positive.");
        }

        return ((double)sample / containerAudioSampleRateHz).ToString("0.#########", CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> BuildFfmpegArguments(
        string filename,
        long sample,
        int containerAudioSampleRateHz = ContainerAudioSampleRateHz)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
            "-i",
            filename,
            "-ss",
            FormatSeekSeconds(sample, containerAudioSampleRateHz),
            "-map",
            "0:a:0",
            "-f",
            "s16le",
            "-acodec",
            "pcm_s16le",
            "-ac",
            "1",
            "-"
        ];
    }

    private double[]? ReadSegment(long sample, int readLength)
    {
        Func<string, long, int, byte[]?> readSegment =
            _readSegment ?? throw new InvalidOperationException("Segment reader is not configured.");
        byte[]? buffer = readSegment(_filename, sample, readLength);
        if (buffer is null || buffer.Length != checked(readLength * 2))
        {
            return null;
        }

        return DecodePcm16(buffer, readLength);
    }

    private double[]? ReadStreaming(long sample, int readLength)
    {
        long sampleBytes = checked(sample * 2);
        int readLengthBytes = checked(readLength * 2);
        EnsureStarted(sample);
        byte[] buffered = [];

        if (sampleBytes < _positionBytes)
        {
            long rewindStart = _positionBytes - _rewindBuffer.Length;
            if (sampleBytes < rewindStart)
            {
                RestartAt(sample);
            }
            else
            {
                int start = checked((int)(sampleBytes - rewindStart));
                int available = Math.Min(readLengthBytes, _rewindBuffer.Length - start);
                buffered = new byte[available];
                Array.Copy(_rewindBuffer, start, buffered, 0, available);
                sampleBytes += available;
                readLengthBytes -= available;
            }
        }

        if (sampleBytes > _positionBytes)
        {
            long gap = sampleBytes - _positionBytes;
            if (gap > SeekThreshold)
            {
                RestartAt(sample);
            }
            else
            {
                while (sampleBytes > _positionBytes)
                {
                    int discardCount = checked((int)Math.Min(sampleBytes - _positionBytes, RewindSize));
                    if (ReadData(discardCount).Length == 0)
                    {
                        return null;
                    }
                }
            }
        }

        byte[] fresh = readLengthBytes > 0 ? ReadData(readLengthBytes) : [];
        if (fresh.Length < readLengthBytes)
        {
            return null;
        }

        byte[] data = new byte[buffered.Length + fresh.Length];
        Array.Copy(buffered, data, buffered.Length);
        Array.Copy(fresh, 0, data, buffered.Length, fresh.Length);
        if (data.Length != checked(readLength * 2))
        {
            return null;
        }

        return DecodePcm16(data, readLength);
    }

    private static double[] DecodePcm16(byte[] buffer, int sampleCount)
    {
        var output = new double[sampleCount];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2));
        }

        return output;
    }

    private void EnsureStarted(long sample)
    {
        if (_output is not null)
        {
            return;
        }

        OpenAt(sample);
    }

    private void RestartAt(long sample)
    {
        OpenAt(sample);
    }

    private void OpenAt(long sample)
    {
        CloseProcess();
        Func<string, long, Stream> openOutput =
            _openOutput ?? throw new InvalidOperationException("Streaming ffmpeg output is not configured.");
        _output = openOutput(_filename, sample);
        _positionBytes = checked(sample * 2);
        _rewindBuffer = [];
    }

    private Stream OpenFfmpegOutput(string filename, long sample)
    {
        _stderr.Clear();
        ContainerAudioInfo audioInfo = ResolveContainerAudioInfo(filename);
        long sourceSample = sample;
        int initialSkipSamples = 0;
        int paddedFrameSamples = 0;
        if (audioInfo.RequiresPyAvPlanePadding)
        {
            paddedFrameSamples = PyAvAudioPlanePaddingStream.CalculatePaddedFrameSamples(
                audioInfo.FrameSamples);
            long frameIndex = Math.DivRem(sample, paddedFrameSamples, out long frameOffset);
            sourceSample = checked(frameIndex * audioInfo.FrameSamples);
            initialSkipSamples = checked((int)frameOffset);
        }

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in BuildFfmpegArguments(filename, sourceSample, audioInfo.SampleRateHz))
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { Length: > 0 })
            {
                _stderr.AppendLine(args.Data);
            }
        };

        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException("Failed to start ffmpeg.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new NotSupportedException("FFmpeg is required to decode .ldf/.flac/.vhs/raw.oga RF inputs.", ex);
        }

        _process.BeginErrorReadLine();
        Stream output = _process.StandardOutput.BaseStream;
        return audioInfo.RequiresPyAvPlanePadding
            ? new PyAvAudioPlanePaddingStream(
                output,
                audioInfo.FrameSamples,
                paddedFrameSamples,
                initialSkipSamples)
            : output;
    }

    private ContainerAudioInfo ResolveContainerAudioInfo(string filename)
    {
        if (_containerAudioInfo is not null)
        {
            return _containerAudioInfo;
        }

        _containerAudioInfo = ProbeContainerAudioInfo(filename)
            ?? ContainerAudioInfo.Default;
        return _containerAudioInfo;
    }

    private static ContainerAudioInfo? ProbeContainerAudioInfo(string filename)
    {
        var startInfo = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-v", "error",
            "-select_streams", "a:0",
            "-read_intervals", "%+#1",
            "-show_entries", "stream=sample_rate,channels,sample_fmt:frame=nb_samples",
            "-of", "json",
            filename
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            return process.ExitCode == 0
                ? ParseContainerAudioInfo(standardOutput.Result)
                : null;
        }
        catch (Exception ex) when (ex is Win32Exception
            or InvalidOperationException
            or JsonException
            or FormatException
            or OverflowException)
        {
            return null;
        }
    }

    private static ContainerAudioInfo? ParseContainerAudioInfo(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("streams", out JsonElement streams)
            || streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement stream = streams[0];
        string? sampleRateText = stream.TryGetProperty("sample_rate", out JsonElement sampleRateValue)
            ? sampleRateValue.GetString()
            : null;
        if (!int.TryParse(
                sampleRateText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int sampleRate)
            || sampleRate <= 0)
        {
            return null;
        }

        int channels = stream.TryGetProperty("channels", out JsonElement channelValue)
            ? channelValue.GetInt32()
            : 1;
        string sampleFormat = stream.TryGetProperty("sample_fmt", out JsonElement sampleFormatValue)
            ? sampleFormatValue.GetString() ?? string.Empty
            : string.Empty;
        int frameSamples = 0;
        if (root.TryGetProperty("frames", out JsonElement frames)
            && frames.ValueKind == JsonValueKind.Array
            && frames.GetArrayLength() > 0
            && frames[0].TryGetProperty("nb_samples", out JsonElement frameSamplesValue))
        {
            frameSamples = frameSamplesValue.GetInt32();
        }

        bool requiresPadding = frameSamples > 0
            && channels > 0
            && sampleFormat.Length > 0
            && (channels != 1 || !string.Equals(sampleFormat, "s16", StringComparison.Ordinal));
        return new ContainerAudioInfo(
            sampleRate,
            frameSamples,
            requiresPadding);
    }

    private byte[] ReadData(int count)
    {
        if (count == 0)
        {
            return [];
        }

        Stream output = _output ?? throw new InvalidOperationException("FFmpeg output stream was not opened.");
        byte[] buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = output.Read(buffer.AsSpan(total, count - total));
            if (read == 0)
            {
                ThrowIfProcessFailed();
                break;
            }

            total += read;
        }

        if (total != buffer.Length)
        {
            Array.Resize(ref buffer, total);
        }

        _positionBytes += total;
        AppendRewind(buffer);
        return buffer;
    }

    private void AppendRewind(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        int combinedLength = Math.Min(RewindSize, _rewindBuffer.Length + data.Length);
        byte[] combined = new byte[combinedLength];
        int dataBytes = Math.Min(data.Length, combinedLength);
        int oldBytes = combinedLength - dataBytes;
        if (oldBytes > 0)
        {
            Array.Copy(_rewindBuffer, _rewindBuffer.Length - oldBytes, combined, 0, oldBytes);
        }

        Array.Copy(data, data.Length - dataBytes, combined, oldBytes, dataBytes);
        _rewindBuffer = combined;
    }

    private void ThrowIfProcessFailed()
    {
        int? exitCode = ProcessExitCodeAfterOutputEnd();
        if (exitCode is not null and not 0)
        {
            string detail = ErrorOutput();
            throw new InvalidOperationException($"FFmpeg failed while streaming '{_filename}': {detail}");
        }
    }

    private int? ProcessExitCodeAfterOutputEnd()
    {
        if (_exitCodeAfterOutputEnd is not null)
        {
            return _exitCodeAfterOutputEnd();
        }

        if (_process is null)
        {
            return null;
        }

        if (!_process.HasExited)
        {
            _process.WaitForExit(1000);
        }

        return _process.HasExited ? _process.ExitCode : null;
    }

    private string ErrorOutput()
    {
        string? external = _stderrProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(external))
        {
            return external.Trim();
        }

        return _stderr.Length == 0 ? "no ffmpeg error output was captured" : _stderr.ToString().Trim();
    }

    private void CloseProcess()
    {
        try
        {
            _output?.Dispose();
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }

            _process?.WaitForExit();
            _process?.Dispose();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process = null;
            _output = null;
            _positionBytes = 0;
            _rewindBuffer = [];
        }
    }

    private static byte[]? ReadSegmentWithFfmpeg(string filename, long sample, int readLength)
    {
        int byteCount = checked(readLength * 2);
        byte[] buffer = new byte[byteCount];
        var stderr = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (string argument in BuildFfmpegArguments(filename, sample))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { Length: > 0 })
            {
                stderr.AppendLine(args.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ffmpeg.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new NotSupportedException("FFmpeg is required to decode .ldf/.flac/.vhs/raw.oga RF inputs.", ex);
        }

        process.BeginErrorReadLine();
        int read = process.StandardOutput.BaseStream.ReadAtLeast(buffer, byteCount, throwOnEndOfStream: false);
        if (read == byteCount)
        {
            StopProcess(process);
            return buffer;
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string detail = stderr.Length == 0 ? "no ffmpeg error output was captured" : stderr.ToString().Trim();
            throw new InvalidOperationException($"FFmpeg failed while reading '{filename}': {detail}");
        }

        return null;
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record ContainerAudioInfo(
        int SampleRateHz,
        int FrameSamples,
        bool RequiresPyAvPlanePadding)
    {
        public static ContainerAudioInfo Default { get; } = new(
            ContainerAudioSampleRateHz,
            0,
            false);
    }
}
