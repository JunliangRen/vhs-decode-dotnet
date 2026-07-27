using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
    private readonly FfmpegDiagnosticTailBuffer _stderr = new();
    private ContainerAudioInfo? _containerAudioInfo;
    private Stream? _output;
    private Process? _process;
    private long _positionBytes;
    private byte[]? _rewindBuffer;
    private byte[]? _discardBuffer;
    private int _rewindStart;
    private int _rewindCount;
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

    internal static IReadOnlyList<string> BuildPyAvFramedFfmpegArguments(
        string filename,
        long targetSample,
        int sampleRateHz)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (targetSample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSample));
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "level+info",
            "-nostdin",
            "-copyts"
        };
        long seekTimestamp = Math.Max(0, (targetSample / 1000) - sampleRateHz);
        if (seekTimestamp > 0)
        {
            arguments.Add("-ss");
            arguments.Add(FormatSeekSeconds(seekTimestamp, 1_000_000));
            arguments.Add("-noaccurate_seek");
            arguments.Add("-seek_timestamp");
            arguments.Add("1");
            arguments.Add("-seek2any");
            arguments.Add("1");
        }

        arguments.AddRange(
        [
            "-i", filename,
            "-map", "0:a:0",
            "-af", "aformat=sample_fmts=s16:channel_layouts=mono,ashowinfo",
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "-ac", "1",
            "-"
        ]);
        return arguments;
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
        int totalBytes = checked(readLength * 2);
        int remainingBytes = totalBytes;
        EnsureStarted(sample);
        int rewindOffset = 0;
        int bufferedBytes = 0;

        if (sampleBytes < _positionBytes)
        {
            long rewindStart = _positionBytes - _rewindCount;
            if (sampleBytes < rewindStart)
            {
                RestartAt(sample);
            }
            else
            {
                rewindOffset = checked((int)(sampleBytes - rewindStart));
                bufferedBytes = Math.Min(remainingBytes, _rewindCount - rewindOffset);
                sampleBytes += bufferedBytes;
                remainingBytes -= bufferedBytes;
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
                    if (DiscardData(discardCount) == 0)
                    {
                        return null;
                    }
                }
            }
        }

        byte[] data = GC.AllocateUninitializedArray<byte>(totalBytes);
        CopyRewind(rewindOffset, data.AsSpan(0, bufferedBytes));
        int freshBytes = remainingBytes > 0
            ? ReadData(data.AsSpan(bufferedBytes, remainingBytes))
            : 0;
        if (freshBytes < remainingBytes)
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
        _rewindStart = 0;
        _rewindCount = 0;
    }

    private Stream OpenFfmpegOutput(string filename, long sample)
    {
        _stderr.Clear();
        if (ImaWavPcm16Stream.TryOpen(filename, out ImaWavPcm16Stream? imaWav)
            && imaWav is not null)
        {
            return new PyAvAudioPlanePaddingStream(
                imaWav,
                imaWav.ReadNextFrameGeometry,
                sample);
        }

        ContainerAudioInfo audioInfo = ResolveContainerAudioInfo(filename);
        // LoadLDF applies the first decoded frame PTS even when mono s16 needs no plane padding.
        Channel<PyAvAudioFrameGeometry> frameGeometry =
            Channel.CreateUnbounded<PyAvAudioFrameGeometry>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        IReadOnlyList<string> arguments = BuildPyAvFramedFfmpegArguments(
            filename,
            sample,
            audioInfo.SampleRateHz);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                frameGeometry.Writer.TryComplete();
            }
            else if (TryParsePyAvAudioFrameGeometry(
                args.Data,
                out PyAvAudioFrameGeometry geometry))
            {
                frameGeometry.Writer.TryWrite(geometry);
            }
            else
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
            frameGeometry.Writer.TryComplete(ex);
            throw new NotSupportedException("FFmpeg is required to decode .ldf/.flac/.vhs/raw.oga RF inputs.", ex);
        }

        _process.BeginErrorReadLine();
        Stream output = _process.StandardOutput.BaseStream;
        return new PyAvAudioPlanePaddingStream(
            output,
            () => ReadNextFrameGeometry(frameGeometry.Reader),
            sample,
            audioInfo.RequiresPyAvPlanePadding);
    }

    internal static bool TryParsePyAvAudioFrameGeometry(
        string line,
        out PyAvAudioFrameGeometry geometry)
    {
        geometry = default;
        if (!line.Contains("Parsed_ashowinfo_", StringComparison.Ordinal)
            || !TryParseInt64Field(line, "nb_samples:", out long logicalSamples)
            || logicalSamples <= 0
            || logicalSamples > int.MaxValue)
        {
            return false;
        }

        long? presentationRfSample = null;
        if (TryParseInt64Field(line, "pts:", out long presentationSample)
            && presentationSample is >= long.MinValue / 1000 and <= long.MaxValue / 1000)
        {
            presentationRfSample = presentationSample * 1000;
        }

        geometry = new PyAvAudioFrameGeometry(
            checked((int)logicalSamples),
            presentationRfSample);
        return true;
    }

    private static bool TryParseInt64Field(string line, string field, out long value)
    {
        int start = line.IndexOf(field, StringComparison.Ordinal);
        if (start < 0)
        {
            value = 0;
            return false;
        }

        start += field.Length;
        int end = line.IndexOf(' ', start);
        ReadOnlySpan<char> token = end < 0
            ? line.AsSpan(start)
            : line.AsSpan(start, end - start);
        return long.TryParse(
            token,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static PyAvAudioFrameGeometry? ReadNextFrameGeometry(
        ChannelReader<PyAvAudioFrameGeometry> reader)
    {
        if (reader.TryRead(out PyAvAudioFrameGeometry geometry))
        {
            return geometry;
        }

        try
        {
            return reader.ReadAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (ChannelClosedException)
        {
            return null;
        }
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
            "-show_entries", "stream=sample_rate,channels,sample_fmt",
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
        bool requiresPadding = channels > 0
            && sampleFormat.Length > 0
            && (channels != 1 || !string.Equals(sampleFormat, "s16", StringComparison.Ordinal));
        return new ContainerAudioInfo(
            sampleRate,
            requiresPadding);
    }

    private int DiscardData(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_discardBuffer is null || _discardBuffer.Length < count)
        {
            _discardBuffer = GC.AllocateUninitializedArray<byte>(count);
        }

        return ReadData(_discardBuffer.AsSpan(0, count));
    }

    private int ReadData(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        Stream output = _output ?? throw new InvalidOperationException("FFmpeg output stream was not opened.");
        int total = 0;
        while (total < destination.Length)
        {
            int read = output.Read(destination[total..]);
            if (read == 0)
            {
                ThrowIfProcessFailed();
                break;
            }

            total += read;
        }

        _positionBytes += total;
        AppendRewind(destination[..total]);
        return total;
    }

    private void AppendRewind(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _rewindBuffer ??= GC.AllocateUninitializedArray<byte>(RewindSize);
        if (data.Length >= _rewindBuffer.Length)
        {
            data[^_rewindBuffer.Length..].CopyTo(_rewindBuffer);
            _rewindStart = 0;
            _rewindCount = _rewindBuffer.Length;
            return;
        }

        int writeStart = (_rewindStart + _rewindCount) % _rewindBuffer.Length;
        CopyToCircularBuffer(data, _rewindBuffer, writeStart);
        int combinedCount = _rewindCount + data.Length;
        if (combinedCount > _rewindBuffer.Length)
        {
            int overwritten = combinedCount - _rewindBuffer.Length;
            _rewindStart = (_rewindStart + overwritten) % _rewindBuffer.Length;
            _rewindCount = _rewindBuffer.Length;
        }
        else
        {
            _rewindCount = combinedCount;
        }
    }

    private void CopyRewind(int offset, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        byte[] buffer = _rewindBuffer
            ?? throw new InvalidOperationException("FFmpeg rewind buffer was not initialized.");
        int sourceStart = (_rewindStart + offset) % buffer.Length;
        int firstLength = Math.Min(destination.Length, buffer.Length - sourceStart);
        buffer.AsSpan(sourceStart, firstLength).CopyTo(destination);
        if (firstLength < destination.Length)
        {
            buffer.AsSpan(0, destination.Length - firstLength).CopyTo(destination[firstLength..]);
        }
    }

    private static void CopyToCircularBuffer(ReadOnlySpan<byte> source, byte[] destination, int start)
    {
        int firstLength = Math.Min(source.Length, destination.Length - start);
        source[..firstLength].CopyTo(destination.AsSpan(start));
        if (firstLength < source.Length)
        {
            source[firstLength..].CopyTo(destination);
        }
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

        string captured = _stderr.GetText();
        return captured.Length == 0 ? "no ffmpeg error output was captured" : captured;
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
            _rewindStart = 0;
            _rewindCount = 0;
            _rewindBuffer = null;
            _discardBuffer = null;
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
        bool RequiresPyAvPlanePadding)
    {
        public static ContainerAudioInfo Default { get; } = new(
            ContainerAudioSampleRateHz,
            false);
    }
}

internal sealed class FfmpegDiagnosticTailBuffer
{
    internal const int DefaultMaximumLines = 64;
    internal const int DefaultMaximumCharacters = 32 * 1024;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly int _maximumLines;
    private readonly int _maximumCharacters;
    private int _storedCharacters;

    public FfmpegDiagnosticTailBuffer(
        int maximumLines = DefaultMaximumLines,
        int maximumCharacters = DefaultMaximumCharacters)
    {
        _maximumLines = maximumLines > 0
            ? maximumLines
            : throw new ArgumentOutOfRangeException(nameof(maximumLines));
        _maximumCharacters = maximumCharacters > Environment.NewLine.Length
            ? maximumCharacters
            : throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _storedCharacters = 0;
        }
    }

    public void AppendLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        int maximumLineCharacters = _maximumCharacters - Environment.NewLine.Length;
        string retained = line.Length <= maximumLineCharacters
            ? line
            : line[^maximumLineCharacters..];
        int retainedCharacters = retained.Length + Environment.NewLine.Length;

        lock (_gate)
        {
            _lines.Enqueue(retained);
            _storedCharacters += retainedCharacters;
            while (_lines.Count > _maximumLines || _storedCharacters > _maximumCharacters)
            {
                string removed = _lines.Dequeue();
                _storedCharacters -= removed.Length + Environment.NewLine.Length;
            }
        }
    }

    public string GetText()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _lines);
        }
    }
}
