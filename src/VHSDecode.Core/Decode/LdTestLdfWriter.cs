using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VHSDecode.Core.Rf;

namespace VHSDecode.Core.Decode;

public readonly record struct LdTestLdfWriteResult(
    bool Success,
    string Message,
    long SamplesWritten,
    long StartSample = 0,
    long EndSample = 0,
    string? OutputPath = null,
    long? ShortReadSample = null);

public interface ILdTestLdfWriter
{
    LdTestLdfWriteResult Write(DecodeSession session, long startSample, long endSample, Stream input);
}

public sealed class LdTestLdfWriter : ILdTestLdfWriter
{
    private readonly ILdTestLdfWriter _preferred;
    private readonly ILdTestLdfWriter _fallback;

    public LdTestLdfWriter(int chunkSamples = FfmpegLdTestLdfWriter.DefaultChunkSamples)
        : this(
            new LibsndfileLdTestLdfWriter(chunkSamples),
            new FfmpegLdTestLdfWriter(chunkSamples))
    {
    }

    internal LdTestLdfWriter(ILdTestLdfWriter preferred, ILdTestLdfWriter fallback)
    {
        _preferred = preferred ?? throw new ArgumentNullException(nameof(preferred));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public LdTestLdfWriteResult Write(
        DecodeSession session,
        long startSample,
        long endSample,
        Stream input)
    {
        try
        {
            return _preferred.Write(session, startSample, endSample, input);
        }
        catch (LdTestLdfBackendUnavailableException)
        {
            return _fallback.Write(session, startSample, endSample, input);
        }
    }
}

internal sealed class LibsndfileLdTestLdfWriter : ILdTestLdfWriter
{
    internal const int SampleRate = 40_000;
    internal const double CompressionLevel = 0.6;

    private readonly FfmpegLdTestLdfWriter _writer;

    public LibsndfileLdTestLdfWriter(int chunkSamples)
    {
        _writer = new FfmpegLdTestLdfWriter(
            path => new LibsndfilePcm16FlacStream(path, SampleRate, CompressionLevel),
            chunkSamples);
    }

    public LdTestLdfWriteResult Write(
        DecodeSession session,
        long startSample,
        long endSample,
        Stream input)
        => _writer.Write(session, startSample, endSample, input);
}

internal sealed class LdTestLdfBackendUnavailableException(string message, Exception innerException)
    : Exception(message, innerException)
{
}

internal sealed unsafe partial class LibsndfilePcm16FlacStream : Stream
{
    internal const int FlacPcm16Format = 0x170002;

    private const int SetCompressionLevelCommand = 0x1301;
    private const int WriteMode = 0x20;

    private nint _file;
    private bool _disposed;

    public LibsndfilePcm16FlacStream(string path, int sampleRate, double compressionLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(compressionLevel, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compressionLevel, 1.0);

        var info = new SoundFileInfo
        {
            SampleRate = sampleRate,
            Channels = 1,
            Format = FlacPcm16Format
        };

        try
        {
            _file = NativeMethods.Open(path, WriteMode, ref info);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw BackendUnavailable(ex);
        }

        if (_file == 0)
        {
            throw new InvalidDataException(
                $"libsndfile failed to open LD test FLAC output: {ErrorText(0)}");
        }

        try
        {
            double level = compressionLevel;
            int result = NativeMethods.Command(
                _file,
                SetCompressionLevelCommand,
                &level,
                sizeof(double));
            if (result != 1)
            {
                throw new InvalidDataException(
                    $"libsndfile failed to set LD test FLAC compression: {ErrorText(_file)}");
            }
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            CloseAfterInitializationFailure();
            throw BackendUnavailable(ex);
        }
        catch
        {
            CloseAfterInitializationFailure();
            throw;
        }
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            NativeMethods.WriteSync(_file);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            throw BackendUnavailable(ex);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((buffer.Length & 1) != 0)
        {
            throw new ArgumentException(
                "PCM16 output must contain complete little-endian samples.",
                nameof(buffer));
        }

        if (buffer.IsEmpty)
        {
            return;
        }

        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "The bundled libsndfile PCM16 writer requires a little-endian process.");
        }

        long sampleCount = buffer.Length / sizeof(short);
        fixed (byte* bytes = buffer)
        {
            long written;
            try
            {
                written = NativeMethods.WriteShort(_file, (short*)bytes, sampleCount);
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                throw BackendUnavailable(ex);
            }

            if (written != sampleCount)
            {
                throw new InvalidDataException(
                    $"libsndfile wrote {written} of {sampleCount} LD test PCM samples: {ErrorText(_file)}");
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        nint file = _file;
        _file = 0;
        if (disposing && file != 0)
        {
            int result;
            try
            {
                result = NativeMethods.Close(file);
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                throw BackendUnavailable(ex);
            }

            if (result != 0)
            {
                throw new InvalidDataException(
                    $"libsndfile failed while finalizing LD test FLAC output: {ErrorText(0)}");
            }
        }

        base.Dispose(disposing);
    }

    private static bool IsUnavailable(Exception exception)
        => exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static LdTestLdfBackendUnavailableException BackendUnavailable(Exception exception)
        => new("libsndfile is unavailable for LD test FLAC output.", exception);

    private static string ErrorText(nint file)
        => Marshal.PtrToStringUTF8(NativeMethods.StrError(file))
            ?? "unknown libsndfile error";

    private void CloseAfterInitializationFailure()
    {
        nint file = _file;
        _file = 0;
        if (file == 0)
        {
            return;
        }

        try
        {
            NativeMethods.Close(file);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SoundFileInfo
    {
        public long Frames;
        public int SampleRate;
        public int Channels;
        public int Format;
        public int Sections;
        public int Seekable;
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "sndfile";

        internal static nint Open(
            string path,
            int mode,
            ref SoundFileInfo info)
            => OperatingSystem.IsWindows()
                ? OpenWindows(path, mode, ref info)
                : OpenUnix(path, mode, ref info);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_wchar_open",
            StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint OpenWindows(
            string path,
            int mode,
            ref SoundFileInfo info);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_open",
            StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint OpenUnix(
            string path,
            int mode,
            ref SoundFileInfo info);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_command")]
        internal static partial int Command(
            nint file,
            int command,
            void* data,
            int dataSize);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_write_short")]
        internal static partial long WriteShort(
            nint file,
            short* samples,
            long sampleCount);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_write_sync")]
        internal static partial void WriteSync(nint file);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_close")]
        internal static partial int Close(nint file);

        [LibraryImport(
            LibraryName,
            EntryPoint = "sf_strerror")]
        internal static partial nint StrError(nint file);
    }
}

public sealed class FfmpegLdTestLdfWriter(
    Func<string, Stream> openOutput,
    int chunkSamples = FfmpegLdTestLdfWriter.DefaultChunkSamples)
    : ILdTestLdfWriter
{
    public const int DefaultChunkSamples = 16_384;
    public const int DefaultCompressionLevel = 6;

    private readonly Func<string, Stream> _openOutput = openOutput ?? throw new ArgumentNullException(nameof(openOutput));
    private readonly int _chunkSamples = chunkSamples > 0 ? chunkSamples : throw new ArgumentOutOfRangeException(nameof(chunkSamples));

    public FfmpegLdTestLdfWriter(int chunkSamples = DefaultChunkSamples)
        : this(path => OpenFfmpegInputPipe(path), chunkSamples)
    {
    }

    public LdTestLdfWriteResult Write(DecodeSession session, long startSample, long endSample, Stream input)
    {
        if (session.TestLdfOutputPath is null)
        {
            return new LdTestLdfWriteResult(false, "No LD test LDF output was requested.", 0);
        }

        if (startSample < 0 || endSample < startSample)
        {
            throw new ArgumentOutOfRangeException(nameof(startSample), "Invalid LD test LDF sample range.");
        }

        session.RuntimeReporter?.BeginTestLdfReport(
            session.TestLdfOutputPath,
            startSample,
            endSample);
        long sampleCount = endSample - startSample;
        if (sampleCount == 0)
        {
            var emptyResult = new LdTestLdfWriteResult(
                false,
                "No samples were available for LD test LDF output.",
                0,
                startSample,
                endSample,
                session.TestLdfOutputPath);
            session.RuntimeReporter?.CompleteTestLdfReport(emptyResult);
            return emptyResult;
        }

        IRfSampleLoader loader = RfLoaderFactory.CreateNative(session.InputFile);
        IDisposable? disposableLoader = loader as IDisposable;
        try
        {
            long written = 0;
            long? shortReadSample = null;
            using (Stream output = _openOutput(session.TestLdfOutputPath))
            {
                for (long sample = startSample; sample < endSample;)
                {
                    int readLength = (int)Math.Min(_chunkSamples, endSample - sample);
                    double[]? values = loader.Read(input, sample, readLength);
                    if (values is null || values.Length != readLength)
                    {
                        shortReadSample = sample;
                        session.RuntimeReporter?.WriteTestLdfShortRead(sample);
                        break;
                    }

                    WriteInt16Samples(output, values);
                    sample += readLength;
                    written += readLength;
                }

                session.RuntimeReporter?.WriteTestLdfSamplesWritten(written);
            }

            var result = new LdTestLdfWriteResult(
                !shortReadSample.HasValue,
                shortReadSample.HasValue
                    ? $"Short read at sample {shortReadSample.Value}."
                    : $"Wrote {written} input sample(s) to {session.TestLdfOutputPath}",
                written,
                startSample,
                endSample,
                session.TestLdfOutputPath,
                shortReadSample);
            session.RuntimeReporter?.CompleteTestLdfReport(result);
            return result;
        }
        finally
        {
            disposableLoader?.Dispose();
        }
    }

    public static IReadOnlyList<string> BuildFfmpegArguments(string outputFilename, int compressionLevel = DefaultCompressionLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilename);
        return
        [
            "-y",
            "-hide_banner",
            "-loglevel",
            "quiet",
            "-f",
            "s16le",
            "-ar",
            "40k",
            "-ac",
            "1",
            "-i",
            "-",
            "-acodec",
            "flac",
            "-f",
            "ogg",
            "-compression_level",
            compressionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            outputFilename
        ];
    }

    private static void WriteInt16Samples(Stream output, IReadOnlyList<double> values)
    {
        byte[] buffer = new byte[checked(values.Count * 2)];
        for (int i = 0; i < values.Count; i++)
        {
            short sample = unchecked((short)(int)Math.Truncate(values[i]));
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), sample);
        }

        output.Write(buffer);
    }

    public static Stream OpenFfmpegInputPipe(
        string outputFilename,
        bool terminateBeforeInputClose = false)
    {
        var stderr = new StringBuilder();
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in BuildFfmpegArguments(outputFilename))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
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
            throw new NotSupportedException("FFmpeg is required to write LD test .ldf files.", ex);
        }

        process.BeginErrorReadLine();
        return new FfmpegInputPipeStream(process, stderr, terminateBeforeInputClose);
    }

    private sealed class FfmpegInputPipeStream(
        Process process,
        StringBuilder stderr,
        bool terminateBeforeInputClose) : Stream
    {
        private bool _disposed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => process.StandardInput.BaseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            process.StandardInput.BaseStream.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            process.StandardInput.BaseStream.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (disposing)
            {
                if (terminateBeforeInputClose)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                    }

                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException)
                    {
                    }

                    try
                    {
                        process.WaitForExit();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    process.Dispose();
                    base.Dispose(disposing);
                    return;
                }

                process.StandardInput.Close();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string detail = stderr.Length == 0 ? "no ffmpeg error output was captured" : stderr.ToString().Trim();
                    process.Dispose();
                    throw new InvalidOperationException($"FFmpeg failed while writing LD test .ldf: {detail}");
                }

                process.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
