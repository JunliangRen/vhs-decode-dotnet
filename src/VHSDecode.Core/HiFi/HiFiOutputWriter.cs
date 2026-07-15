using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VHSDecode.Core.HiFi;

internal sealed class HiFiOutputWriter : IDisposable
{
    private const string DefaultChannelSuffix = "channel";
    private const string NormalizeFileSuffix = "tmp_normalize.raw";

    private readonly HiFiDecodeOptions _options;
    private readonly bool _dualMono;
    private readonly string _channel1Path;
    private readonly string _channel2Path;
    private IHiFiFloatWriter? _stereo;
    private IHiFiFloatWriter? _channel1;
    private IHiFiFloatWriter? _channel2;
    private float _leftPeak;
    private float _rightPeak;
    private bool _completed;
    private bool _disposed;

    public HiFiOutputWriter(HiFiDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _dualMono = options.AudioMode is HiFiConstants.AudioModeDualMono
            or HiFiConstants.AudioModeDualMonoMidSide;
        _channel1Path = GetDualMonoFilename(options.OutputFile, DefaultChannelSuffix + "_1");
        _channel2Path = GetDualMonoFilename(options.OutputFile, DefaultChannelSuffix + "_2");

        if (_dualMono)
        {
            _channel1 = CreateWriter(_channel1Path, 1, options.AudioRateHz, options.Normalize);
            try
            {
                _channel2 = CreateWriter(_channel2Path, 1, options.AudioRateHz, options.Normalize);
            }
            catch
            {
                _channel1.Dispose();
                throw;
            }
        }
        else
        {
            _stereo = CreateWriter(options.OutputFile, 2, options.AudioRateHz, options.Normalize);
        }
    }

    public bool IsDualMono => _dualMono;

    public void WriteInitialPadding(int finalAudioOverlap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int roundedHalfOverlap = checked((int)Math.Round(
            finalAudioOverlap / 2.0,
            MidpointRounding.ToEven));
        int floatCount = checked(roundedHalfOverlap * 2);
        if (floatCount == 0)
        {
            return;
        }

        float[] padding = new float[floatCount];
        if (_dualMono)
        {
            _channel1!.Write(padding);
            _channel2!.Write(padding);
        }
        else
        {
            _stereo!.Write(padding);
        }
    }

    public void Write(HiFiPostProcessedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _leftPeak = Math.Max(_leftPeak, block.LeftPeak);
        _rightPeak = Math.Max(_rightPeak, block.RightPeak);
        if (_dualMono)
        {
            _channel1!.Write(block.Left);
            _channel2!.Write(block.Right);
        }
        else
        {
            _stereo!.Write(block.Stereo);
        }
    }

    public void Complete(float leftPeak, float rightPeak, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        CompleteWriters();
        _completed = true;

        if (_dualMono)
        {
            output.Write($"{Environment.NewLine}Channel 1: Peak gain is {(leftPeak * 100.0f):F2}%.");
            if (_options.Normalize)
            {
                Normalize(
                    GetNormalizeFilename(_channel1Path, _options.AudioRateHz),
                    _channel1Path,
                    leftPeak,
                    1,
                    _options.AudioRateHz,
                    output);
            }

            output.Write($"{Environment.NewLine}Channel 2: Peak gain is {(rightPeak * 100.0f):F2}%.");
            if (_options.Normalize)
            {
                Normalize(
                    GetNormalizeFilename(_channel2Path, _options.AudioRateHz),
                    _channel2Path,
                    rightPeak,
                    1,
                    _options.AudioRateHz,
                    output);
            }
        }
        else
        {
            float peak = Math.Max(leftPeak, rightPeak);
            output.Write($"{Environment.NewLine}Peak gain is {(peak * 100.0f):F2}%.");
            if (_options.Normalize)
            {
                Normalize(
                    GetNormalizeFilename(_options.OutputFile, _options.AudioRateHz),
                    _options.OutputFile,
                    peak,
                    2,
                    _options.AudioRateHz,
                    output);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_completed)
        {
            try
            {
                Complete(_leftPeak, _rightPeak, TextWriter.Null);
            }
            catch
            {
                DisposeWriters();
            }
        }

        _disposed = true;
    }

    internal static string GetNormalizeFilename(string path, int sampleRate)
        => $"{path}_{sampleRate}_f32_{NormalizeFileSuffix}";

    internal static string GetDualMonoFilename(string path, string channelSuffix)
    {
        string extension = Path.GetExtension(path);
        string root = extension.Length == 0 ? path : path[..^extension.Length];
        return $"{root}_{channelSuffix}{extension}";
    }

    internal static int QuantizeFlacPcm24(float sample)
    {
        if (!float.IsFinite(sample))
        {
            throw new InvalidDataException("HiFi FLAC output cannot encode NaN or infinity.");
        }

        if (sample <= -1.0f)
        {
            return -8_388_608;
        }

        if (sample >= 1.0f)
        {
            return 8_388_607;
        }

        int rounded = checked((int)MathF.Round(
            sample * 8_388_608.0f,
            MidpointRounding.ToEven));
        return Math.Clamp(rounded, -8_388_608, 8_388_607);
    }

    private static IHiFiFloatWriter CreateWriter(
        string path,
        int channels,
        int sampleRate,
        bool normalize)
    {
        if (normalize)
        {
            return new RawFloatWriter(GetNormalizeFilename(path, sampleRate));
        }

        return path.Contains(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WavePcm16Writer(path, channels, sampleRate)
            : new FfmpegFlacWriter(path, channels, sampleRate);
    }

    private static void Normalize(
        string inputPath,
        string outputPath,
        float peak,
        int channels,
        int sampleRate,
        TextWriter output)
    {
        try
        {
            float gain = (float)((1.0 / peak) - (1.0 / 1024.0));
            output.Write(string.Create(
                CultureInfo.InvariantCulture,
                $" Adjusting to {(gain * 100.0f):F2}%, please wait..."));

            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using IHiFiFloatWriter writer = CreateWriter(
                outputPath,
                channels,
                sampleRate,
                normalize: false);
            byte[] bytes = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                int carry = 0;
                while (true)
                {
                    int read = input.Read(bytes, carry, bytes.Length - carry);
                    int available = carry + read;
                    int completeBytes = available - (available % sizeof(float));
                    Span<float> samples = MemoryMarshal.Cast<byte, float>(
                        bytes.AsSpan(0, completeBytes));
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] *= gain;
                    }

                    writer.Write(samples);
                    carry = available - completeBytes;
                    if (carry > 0)
                    {
                        bytes.AsSpan(completeBytes, carry).CopyTo(bytes);
                    }

                    if (read == 0)
                    {
                        break;
                    }
                }

                writer.Complete();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            output.WriteLine();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private void CompleteWriters()
    {
        if (_dualMono)
        {
            try
            {
                _channel1!.Complete();
                _channel2!.Complete();
            }
            finally
            {
                _channel1!.Dispose();
                _channel2!.Dispose();
            }
        }
        else
        {
            try
            {
                _stereo!.Complete();
            }
            finally
            {
                _stereo!.Dispose();
            }
        }
    }

    private void DisposeWriters()
    {
        _stereo?.Dispose();
        _channel1?.Dispose();
        _channel2?.Dispose();
    }

    private interface IHiFiFloatWriter : IDisposable
    {
        void Write(ReadOnlySpan<float> samples);

        void Complete();
    }

    private sealed class RawFloatWriter : IHiFiFloatWriter
    {
        private readonly FileStream _stream;
        private bool _completed;

        public RawFloatWriter(string path)
        {
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        public void Write(ReadOnlySpan<float> samples)
            => _stream.Write(MemoryMarshal.AsBytes(samples));

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _stream.Flush(flushToDisk: false);
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class WavePcm16Writer : IHiFiFloatWriter
    {
        private const int HeaderLength = 44;

        private readonly FileStream _stream;
        private readonly int _channels;
        private readonly int _sampleRate;
        private long _sampleCount;
        private bool _completed;

        public WavePcm16Writer(string path, int channels, int sampleRate)
        {
            _channels = channels;
            _sampleRate = sampleRate;
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _stream.Write(new byte[HeaderLength]);
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Audio writer is already complete.");
            }

            byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Min(
                checked(samples.Length * sizeof(short)),
                1024 * 1024));
            try
            {
                int position = 0;
                while (position < samples.Length)
                {
                    int count = Math.Min(samples.Length - position, bytes.Length / sizeof(short));
                    Span<byte> destination = bytes.AsSpan(0, count * sizeof(short));
                    for (int i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(
                            destination.Slice(i * sizeof(short), sizeof(short)),
                            QuantizePcm16(samples[position + i]));
                    }

                    _stream.Write(destination);
                    position += count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            _sampleCount = checked(_sampleCount + samples.Length);
        }

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            long dataLength = checked(_sampleCount * sizeof(short));
            if (dataLength > uint.MaxValue - 36L)
            {
                throw new IOException("HiFi PCM WAVE output exceeds the RIFF size limit.");
            }

            Span<byte> header = stackalloc byte[HeaderLength];
            WriteAscii(header[0..4], "RIFF");
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], checked((uint)(36 + dataLength)));
            WriteAscii(header[8..12], "WAVE");
            WriteAscii(header[12..16], "fmt ");
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[22..24], checked((ushort)_channels));
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], checked((uint)_sampleRate));
            uint byteRate = checked((uint)(_sampleRate * _channels * sizeof(short)));
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], byteRate);
            BinaryPrimitives.WriteUInt16LittleEndian(
                header[32..34],
                checked((ushort)(_channels * sizeof(short))));
            BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], 16);
            WriteAscii(header[36..40], "data");
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], checked((uint)dataLength));
            _stream.Position = 0;
            _stream.Write(header);
            _stream.Flush(flushToDisk: false);
        }

        public void Dispose()
        {
            try
            {
                if (!_completed)
                {
                    Complete();
                }
            }
            finally
            {
                _stream.Dispose();
            }
        }

        internal static short QuantizePcm16(float sample)
        {
            if (float.IsNaN(sample) || float.IsNegativeInfinity(sample) || sample <= -1.0f)
            {
                return short.MinValue;
            }

            if (float.IsPositiveInfinity(sample) || sample >= 1.0f)
            {
                return short.MaxValue;
            }

            return checked((short)MathF.Floor(sample * 32768.0f));
        }

        private static void WriteAscii(Span<byte> destination, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                destination[i] = (byte)value[i];
            }
        }
    }

    private sealed class FfmpegFlacWriter : IHiFiFloatWriter
    {
        private readonly Process _process;
        private readonly Stream _input;
        private readonly StringBuilder _stderr = new();
        private bool _completed;
        private bool _disposed;

        public FfmpegFlacWriter(string path, int channels, int sampleRate)
        {
            var startInfo = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "s32le", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", channels.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
                "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24",
                "-compression_level", "12", "-f", "flac", path
            })
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
                _process.Dispose();
                throw new NotSupportedException(
                    "ffmpeg is required to write HiFi FLAC output.",
                    ex);
            }

            _process.BeginErrorReadLine();
            _input = _process.StandardInput.BaseStream;
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException("Audio writer is already complete.");
            }

            byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Min(
                checked(samples.Length * sizeof(int)),
                1024 * 1024));
            try
            {
                int position = 0;
                while (position < samples.Length)
                {
                    int count = Math.Min(samples.Length - position, bytes.Length / sizeof(int));
                    Span<byte> destination = bytes.AsSpan(0, count * sizeof(int));
                    for (int i = 0; i < count; i++)
                    {
                        int pcm24 = QuantizeFlacPcm24(samples[position + i]);
                        BinaryPrimitives.WriteInt32LittleEndian(
                            destination.Slice(i * sizeof(int), sizeof(int)),
                            pcm24 << 8);
                    }

                    _input.Write(destination);
                    position += count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                return;
            }

            _completed = true;
            _input.Dispose();
            _process.WaitForExit();
            if (_process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"ffmpeg failed while writing HiFi FLAC output: {_stderr.ToString().Trim()}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (!_completed)
                {
                    _input.Dispose();
                    if (!_process.WaitForExit(1000))
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
