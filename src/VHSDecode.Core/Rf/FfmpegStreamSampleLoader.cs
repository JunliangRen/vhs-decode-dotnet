using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace VHSDecode.Core.Rf;

public sealed class FfmpegStreamSampleLoader : IRfSampleLoader, IDisposable
{
    public const int DefaultRewindSize = 16 * 1024 * 1024;

    private readonly Func<Stream, Stream> _openOutput;
    private readonly Func<int?>? _exitCodeAfterOutputEnd;
    private readonly Func<string>? _stderrProvider;
    private readonly StringBuilder _stderr = new();
    private Stream? _output;
    private Process? _process;
    private Task? _inputPump;
    private long _position;
    private byte[] _rewindBuffer = [];
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
        int readLengthBytes = checked(readLength * 2);
        byte[] buffered = [];

        if (sampleBytes < _position)
        {
            long rewindStart = _position - _rewindBuffer.Length;
            if (sampleBytes < rewindStart)
            {
                throw new IOException("Seeking too far backwards with ffmpeg");
            }

            int start = checked((int)(sampleBytes - rewindStart));
            int available = Math.Min(readLengthBytes, _rewindBuffer.Length - start);
            buffered = new byte[available];
            Array.Copy(_rewindBuffer, start, buffered, 0, available);
            sampleBytes += available;
            readLengthBytes -= available;
        }

        while (sampleBytes > _position)
        {
            int discardCount = checked((int)Math.Min(sampleBytes - _position, RewindSize));
            if (ReadData(discardCount).Length == 0)
            {
                return null;
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

        _position += total;
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
            _rewindBuffer = [];
        }
    }
}
