using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace VHSDecode.Core.Rf;

public sealed class FfmpegStreamSampleLoader : IRfSampleLoader, IDisposable
{
    public const int DefaultRewindSize = 16 * 1024 * 1024;
    private const int DiscardBufferSize = 64 * 1024;

    private readonly Func<Stream, Stream> _openOutput;
    private readonly Func<int?>? _exitCodeAfterOutputEnd;
    private readonly Func<string>? _stderrProvider;
    private readonly StringBuilder _stderr = new();
    private Stream? _output;
    private Process? _process;
    private Task? _inputPump;
    private long _position;
    private byte[]? _rewindBuffer;
    private byte[]? _discardBuffer;
    private int _rewindStart;
    private int _rewindCount;
    private bool _disposed;

    public FfmpegStreamSampleLoader(
        IReadOnlyList<string> inputArguments,
        IReadOnlyList<string> outputArguments,
        int rewindSize = DefaultRewindSize)
    {
        InputArguments = inputArguments.ToArray();
        OutputArguments = outputArguments.ToArray();
        RewindSize = rewindSize > 0 ? rewindSize : throw new ArgumentOutOfRangeException(nameof(rewindSize));
        _openOutput = OpenFfmpegOutput;
    }

    public FfmpegStreamSampleLoader(
        IReadOnlyList<string> inputArguments,
        IReadOnlyList<string> outputArguments,
        Func<Stream, Stream> openOutput,
        int rewindSize = DefaultRewindSize,
        Func<int?>? exitCodeAfterOutputEnd = null,
        Func<string>? stderrProvider = null)
        : this(inputArguments, outputArguments, rewindSize)
    {
        _openOutput = openOutput ?? throw new ArgumentNullException(nameof(openOutput));
        _exitCodeAfterOutputEnd = exitCodeAfterOutputEnd;
        _stderrProvider = stderrProvider;
    }

    ~FfmpegStreamSampleLoader()
    {
        Dispose();
    }

    public IReadOnlyList<string> InputArguments { get; }

    public IReadOnlyList<string> OutputArguments { get; }

    public int RewindSize { get; }

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

        EnsureStarted(stream);
        long sampleBytes = checked(sample * 2);
        int totalBytes = checked(readLength * 2);
        int remainingBytes = totalBytes;
        int rewindOffset = 0;
        int bufferedBytes = 0;

        if (sampleBytes < _position)
        {
            long rewindStart = _position - _rewindCount;
            if (sampleBytes < rewindStart)
            {
                throw new IOException("Seeking too far backwards with ffmpeg");
            }

            rewindOffset = checked((int)(sampleBytes - rewindStart));
            bufferedBytes = Math.Min(remainingBytes, _rewindCount - rewindOffset);
            sampleBytes += bufferedBytes;
            remainingBytes -= bufferedBytes;
        }

        while (sampleBytes > _position)
        {
            int discardCount = checked((int)Math.Min(sampleBytes - _position, RewindSize));
            if (DiscardData(discardCount) == 0)
            {
                return null;
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

        var output = new double[readLength];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2, 2));
        }

        return output;
    }

    public static IReadOnlyList<string> BuildFfmpegArguments(
        IReadOnlyList<string> inputArguments,
        IReadOnlyList<string> outputArguments)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "quiet"
        };
        arguments.AddRange(inputArguments);
        arguments.Add("-i");
        arguments.Add("-");
        arguments.AddRange(outputArguments);
        arguments.Add("-c:a");
        arguments.Add("pcm_s16le");
        arguments.Add("-f");
        arguments.Add("s16le");
        arguments.Add("-");
        return arguments;
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

    private void EnsureStarted(Stream input)
    {
        if (_output is not null)
        {
            return;
        }

        if (input.CanSeek)
        {
            input.Seek(0, SeekOrigin.Begin);
        }

        _output = _openOutput(input);
    }

    private Stream OpenFfmpegOutput(Stream input)
    {
        CloseProcess();
        _stderr.Clear();
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in BuildFfmpegArguments(InputArguments, OutputArguments))
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
            throw new NotSupportedException("FFmpeg is required for RF input resampling.", ex);
        }

        _process.BeginErrorReadLine();
        _inputPump = Task.Run(() =>
        {
            try
            {
                input.CopyTo(_process.StandardInput.BaseStream);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch (InvalidOperationException)
                {
                }
            }
        });

        return _process.StandardOutput.BaseStream;
    }

    private int DiscardData(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        _discardBuffer ??= GC.AllocateUninitializedArray<byte>(Math.Min(RewindSize, DiscardBufferSize));
        int total = 0;
        while (total < count)
        {
            int requested = Math.Min(count - total, _discardBuffer.Length);
            int read = ReadData(_discardBuffer.AsSpan(0, requested));
            total += read;
            if (read < requested)
            {
                break;
            }
        }

        return total;
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

        _position += total;
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
            throw new InvalidOperationException($"FFmpeg failed while resampling RF input: {detail}");
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
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }

            _process?.WaitForExit();
            _inputPump?.Wait(TimeSpan.FromSeconds(1));
            _process?.Dispose();
        }
        catch (InvalidOperationException)
        {
        }
        catch (AggregateException)
        {
        }
        finally
        {
            _process = null;
            _output = null;
            _inputPump = null;
            _position = 0;
            _rewindStart = 0;
            _rewindCount = 0;
            _rewindBuffer = null;
            _discardBuffer = null;
        }
    }
}
