using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
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

        long sampleCount = endSample - startSample;
        if (sampleCount == 0)
        {
            return new LdTestLdfWriteResult(
                false,
                "No samples were available for LD test LDF output.",
                0,
                startSample,
                endSample,
                session.TestLdfOutputPath);
        }

        IRfSampleLoader loader = RfLoaderFactory.CreateNative(session.InputFile);
        IDisposable? disposableLoader = loader as IDisposable;
        try
        {
            long written = 0;
            using Stream output = _openOutput(session.TestLdfOutputPath);
            for (long sample = startSample; sample < endSample;)
            {
                int readLength = (int)Math.Min(_chunkSamples, endSample - sample);
                double[]? values = loader.Read(input, sample, readLength);
                if (values is null || values.Length != readLength)
                {
                    return new LdTestLdfWriteResult(
                        false,
                        $"Short read at sample {sample}.",
                        written,
                        startSample,
                        endSample,
                        session.TestLdfOutputPath,
                        sample);
                }

                WriteInt16Samples(output, values);
                sample += readLength;
                written += readLength;
            }

            return new LdTestLdfWriteResult(
                true,
                $"Wrote {written} input sample(s) to {session.TestLdfOutputPath}",
                written,
                startSample,
                endSample,
                session.TestLdfOutputPath);
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
