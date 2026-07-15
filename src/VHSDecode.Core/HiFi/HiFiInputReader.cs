using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using VHSDecode.Core.Rf;

namespace VHSDecode.Core.HiFi;

internal interface IHiFiSampleReader : IDisposable
{
    long? TotalSamples { get; }

    int Read(Span<float> destination, CancellationToken cancellationToken = default);
}

internal readonly record struct HiFiToolProbeResult(
    bool Found,
    string? FirstOutputLine);

internal interface IHiFiInputProcessHost
{
    HiFiToolProbeResult Probe(
        string fileName,
        IReadOnlyList<string> arguments);

    IHiFiSampleReader Open(
        string fileName,
        IReadOnlyList<string> arguments,
        HiFiRawSampleFormat format,
        long? totalSamples);
}

internal sealed class SystemHiFiInputProcessHost : IHiFiInputProcessHost
{
    public static SystemHiFiInputProcessHost Instance { get; } = new();

    private SystemHiFiInputProcessHost()
    {
    }

    public HiFiToolProbeResult Probe(
        string fileName,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return default;
            }

            process.StandardInput.Close();
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            string firstOutputLine = standardOutput.Result
                .Split('\n', 2)[0]
                .TrimEnd('\r');
            return new HiFiToolProbeResult(true, firstOutputLine);
        }
        catch (Win32Exception)
        {
            return default;
        }
    }

    public IHiFiSampleReader Open(
        string fileName,
        IReadOnlyList<string> arguments,
        HiFiRawSampleFormat format,
        long? totalSamples)
        => HiFiInputReader.OpenProcessCore(
            fileName,
            arguments,
            format,
            totalSamples);
}

internal static class HiFiInputReader
{
    private static readonly IReadOnlyDictionary<string, HiFiRawSampleFormat> RawFormats =
        new Dictionary<string, HiFiRawSampleFormat>(StringComparer.Ordinal)
        {
            ["u8"] = HiFiRawSampleFormat.U8,
            ["u10le"] = HiFiRawSampleFormat.U10Le,
            ["u12le"] = HiFiRawSampleFormat.U12Le,
            ["u16le"] = HiFiRawSampleFormat.U16Le,
            ["s8"] = HiFiRawSampleFormat.S8,
            ["s10le"] = HiFiRawSampleFormat.S10Le,
            ["s12le"] = HiFiRawSampleFormat.S12Le,
            ["s16le"] = HiFiRawSampleFormat.S16Le,
            ["raw"] = HiFiRawSampleFormat.S16Le,
            ["f32le"] = HiFiRawSampleFormat.F32Le
        };

    public static IHiFiSampleReader Open(HiFiDecodeOptions options, TextWriter output)
        => Open(options, output, SystemHiFiInputProcessHost.Instance);

    internal static IHiFiSampleReader Open(
        HiFiDecodeOptions options,
        TextWriter output,
        IHiFiInputProcessHost processHost)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(processHost);

        string inputPath = options.InputFile;
        string extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        string extensionWithEndian = extension
            .Replace("16", "16le", StringComparison.Ordinal)
            .Replace("32", "32le", StringComparison.Ordinal);
        bool isRaw = RawFormats.TryGetValue(extensionWithEndian, out HiFiRawSampleFormat extensionFormat);
        HiFiRawSampleFormat? overrideFormat = options.InputFormatOverride is null
            ? null
            : HiFiSampleNormalizer.ParseFormat(options.InputFormatOverride);
        HiFiRawSampleFormat format = overrideFormat ?? extensionFormat;

        if (isRaw || inputPath == "-")
        {
            if (inputPath == "-" && !overrideFormat.HasValue)
            {
                throw new ArgumentException("`--raw_format <format>` is required for stdin input");
            }

            Stream stream = inputPath == "-"
                ? Console.OpenStandardInput()
                : new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new NormalizedStreamReader(
                stream,
                format,
                ownsStream: inputPath != "-");
        }

        if (extension == "lds")
        {
            foreach ((string tool, string? inputArgument) in new[]
            {
                ("ld-lds-reader", (string?)null),
                ("ld-lds-converter", "-i")
            })
            {
                if (ProbeLdTool(processHost, tool, output))
                {
                    var arguments = new List<string>();
                    if (inputArgument is not null)
                    {
                        arguments.Add(inputArgument);
                    }

                    arguments.Add(inputPath);
                    return processHost.Open(
                        tool,
                        arguments,
                        overrideFormat ?? HiFiRawSampleFormat.S16Le,
                        totalSamples: null);
                }
            }

            throw new NotSupportedException(
                "ERROR: Unable to decode LDS without ld-lds-reader or ld-lds-converter. "
                + "Please install one of them and try again.");
        }

        if (extension == "ldf")
        {
            try
            {
                foreach (string tool in new[] { "ld-ldf-reader", "ld-ldf-reader-py" })
                {
                    if (ProbeLdTool(processHost, tool, output))
                    {
                        return processHost.Open(
                            tool,
                            [inputPath],
                            overrideFormat ?? HiFiRawSampleFormat.S16Le,
                            totalSamples: null);
                    }
                }

                output.WriteLine(
                    "WARN: ld-ldf-reader/ld-ldf-reader-py not installed. "
                    + "LDF file format may not decode correctly");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.WriteLine(
                    "WARN: Unexpected error opening LDF reader tool, "
                    + $"LDF file format may not decode correctly {ex.Message}");
            }

            try
            {
                if (ProbeVersionedTool(processHost, "flac", "-version", output))
                {
                    return OpenFlac(
                        processHost,
                        inputPath,
                        overrideFormat ?? HiFiRawSampleFormat.S16Le);
                }

                if (ProbeVersionedTool(processHost, "ffmpeg", "-version", output))
                {
                    return OpenFfmpeg(
                        processHost,
                        inputPath,
                        overrideFormat ?? HiFiRawSampleFormat.S16Le,
                        useNoBuffer: true,
                        totalSamples: null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            throw new NotSupportedException(
                "Unable to decode LDF input with the available LD, FLAC, FFmpeg, or SoundFile paths.");
        }

        if (extension == "flac")
        {
            try
            {
                return OpenFfmpeg(
                    processHost,
                    inputPath,
                    overrideFormat ?? HiFiRawSampleFormat.S16Le,
                    useNoBuffer: false,
                    TryReadFlacTotalSamples(inputPath));
            }
            catch (Win32Exception ex)
            {
                throw new NotSupportedException(
                    "ffmpeg is not installed (or not in PATH), cannot decode this input format.",
                    ex);
            }
        }

        output.WriteLine("WARN: Unknown file format.");
        output.WriteLine("WARN: Attempting to decode with ffmpeg");

        try
        {
            if (ProbeVersionedTool(processHost, "ffmpeg", "-version", output))
            {
                return OpenFfmpeg(
                    processHost,
                    inputPath,
                    overrideFormat ?? HiFiRawSampleFormat.S16Le,
                    useNoBuffer: true,
                    totalSamples: null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        output.WriteLine("WARN: Attempting to decode with SoundFile");
        if (extension == "wav")
        {
            return OpenWave(inputPath, overrideFormat ?? HiFiRawSampleFormat.S16Le);
        }

        throw new NotSupportedException(
            "ffmpeg is not installed (or not in PATH), cannot decode this input format.");
    }

    internal static string NormalizeRawExtension(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension
            .Replace("16", "16le", StringComparison.Ordinal)
            .Replace("32", "32le", StringComparison.Ordinal);
    }

    internal static long? TryReadFlacTotalSamples(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[4];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual("fLaC"u8))
            {
                return null;
            }

            bool lastBlock = false;
            Span<byte> header = stackalloc byte[4];
            while (!lastBlock)
            {
                stream.ReadExactly(header);
                lastBlock = (header[0] & 0x80) != 0;
                int blockType = header[0] & 0x7f;
                int blockLength = (header[1] << 16) | (header[2] << 8) | header[3];
                if (blockType == 0 && blockLength >= 18)
                {
                    Span<byte> streamInfo = stackalloc byte[18];
                    stream.ReadExactly(streamInfo);
                    ulong packed = BinaryPrimitives.ReadUInt64BigEndian(streamInfo[10..]);
                    long totalSamples = checked((long)(packed & 0x0000000FFFFFFFFFUL));
                    return totalSamples == 0 ? null : totalSamples;
                }

                stream.Seek(blockLength, SeekOrigin.Current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static IHiFiSampleReader OpenWave(string path, HiFiRawSampleFormat format)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            WaveDataInfo info = WavePcm16SampleLoader.ReadWaveInfo(stream);
            stream.Position = info.DataOffset;
            return new NormalizedStreamReader(
                new LengthLimitedStream(stream, info.DataLengthBytes),
                format,
                ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static IHiFiSampleReader OpenFfmpeg(
        IHiFiInputProcessHost processHost,
        string path,
        HiFiRawSampleFormat format,
        bool useNoBuffer,
        long? totalSamples)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-ignore_unknown"
        };
        if (useNoBuffer)
        {
            arguments.Add("-fflags");
            arguments.Add("nobuffer");
        }

        arguments.AddRange(
        [
            "-i", path,
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "-avoid_negative_ts", "disabled",
            "-"
        ]);
        return processHost.Open("ffmpeg", arguments, format, totalSamples);
    }

    private static IHiFiSampleReader OpenFlac(
        IHiFiInputProcessHost processHost,
        string path,
        HiFiRawSampleFormat format)
        => processHost.Open(
            "flac",
            [
                "-d",
                "-c",
                "-s",
                "-F",
                "--force-raw-format",
                "--endian", "little",
                "--sign", "signed",
                path
            ],
            format,
            totalSamples: null);

    internal static IHiFiSampleReader OpenProcessCore(
        string fileName,
        IReadOnlyList<string> arguments,
        HiFiRawSampleFormat format,
        long? totalSamples)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        return new ProcessSampleReader(process, format, totalSamples);
    }

    private static bool ProbeLdTool(
        IHiFiInputProcessHost processHost,
        string fileName,
        TextWriter output)
    {
        HiFiToolProbeResult result = processHost.Probe(fileName, ["--help"]);
        if (result.Found)
        {
            output.WriteLine($"Found {fileName}");
            return true;
        }

        output.WriteLine($"WARN: {fileName} not installed (or not in PATH)");
        return false;
    }

    private static bool ProbeVersionedTool(
        IHiFiInputProcessHost processHost,
        string fileName,
        string versionArgument,
        TextWriter output)
    {
        HiFiToolProbeResult result = processHost.Probe(fileName, [versionArgument]);
        if (result.Found)
        {
            output.WriteLine($"Found {result.FirstOutputLine}");
            return true;
        }

        output.WriteLine($"WARN: {fileName} not installed (or not in PATH)");
        return false;
    }

    private sealed class ProcessSampleReader : IHiFiSampleReader
    {
        private readonly Process _process;
        private readonly NormalizedStreamReader _reader;
        private readonly Task<string> _stderrTask;
        private bool _disposed;

        public ProcessSampleReader(
            Process process,
            HiFiRawSampleFormat format,
            long? totalSamples)
        {
            _process = process;
            _reader = new NormalizedStreamReader(
                process.StandardOutput.BaseStream,
                format,
                ownsStream: false);
            _stderrTask = process.StandardError.ReadToEndAsync();
            TotalSamples = totalSamples;
        }

        public long? TotalSamples { get; }

        public int Read(Span<float> destination, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int read = _reader.Read(destination, cancellationToken);
            if (read == 0)
            {
                _process.WaitForExit();
                if (_process.ExitCode != 0)
                {
                    string detail = _stderrTask.GetAwaiter().GetResult().Trim();
                    throw new InvalidDataException(
                        $"Error decoding input rf: {(detail.Length == 0 ? $"{_process.StartInfo.FileName} exited with code {_process.ExitCode}" : detail)}");
                }
            }

            return read;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reader.Dispose();
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                _process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private sealed class NormalizedStreamReader : IHiFiSampleReader
    {
        private const int BufferLength = 1024 * 1024;

        private readonly Stream _stream;
        private readonly HiFiRawSampleFormat _format;
        private readonly bool _ownsStream;
        private readonly byte[] _buffer;
        private bool _disposed;

        public NormalizedStreamReader(
            Stream stream,
            HiFiRawSampleFormat format,
            bool ownsStream)
        {
            _stream = stream;
            _format = format;
            _ownsStream = ownsStream;
            _buffer = ArrayPool<byte>.Shared.Rent(BufferLength);
            int bytesPerSample = HiFiSampleNormalizer.BytesPerSample(format);
            TotalSamples = stream.CanSeek
                ? Math.Max(0, stream.Length - stream.Position) / bytesPerSample
                : null;
        }

        public long? TotalSamples { get; }

        public int Read(Span<float> destination, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int bytesPerSample = HiFiSampleNormalizer.BytesPerSample(_format);
            int totalSamples = 0;
            while (totalSamples < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sampleCapacity = Math.Min(
                    destination.Length - totalSamples,
                    _buffer.Length / bytesPerSample);
                int requestedBytes = checked(sampleCapacity * bytesPerSample);
                int bytesRead = ReadUpTo(_buffer.AsSpan(0, requestedBytes), cancellationToken);
                int samplesRead = bytesRead / bytesPerSample;
                if (samplesRead == 0)
                {
                    break;
                }

                int completeBytes = checked(samplesRead * bytesPerSample);
                HiFiSampleNormalizer.Normalize(
                    _buffer.AsSpan(0, completeBytes),
                    destination[totalSamples..],
                    _format);
                totalSamples += samplesRead;
                if (bytesRead < requestedBytes)
                {
                    break;
                }
            }

            return totalSamples;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }

        private int ReadUpTo(Span<byte> destination, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = _stream.Read(destination[total..]);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }
    }

    private sealed class LengthLimitedStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public LengthLimitedStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = checked((int)Math.Min(buffer.Length, _remaining));
            if (count == 0)
            {
                return 0;
            }

            int read = _inner.Read(buffer[..count]);
            _remaining -= read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
