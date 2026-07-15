using System.ComponentModel;
using System.Diagnostics;

namespace VHSDecode.Core.Decode;

public sealed record LaserDiscAc3ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool RedirectInput,
    bool RedirectOutput,
    bool RedirectErrorToLog);

public static class LaserDiscAc3Pipe
{
    public static IReadOnlyList<LaserDiscAc3ProcessSpec> BuildProcessSpecs(string outputFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilename);
        return
        [
            new LaserDiscAc3ProcessSpec(
                "ld-ac3-decode",
                ["-", outputFilename],
                RedirectInput: true,
                RedirectOutput: true,
                RedirectErrorToLog: true),
            new LaserDiscAc3ProcessSpec(
                "ld-ac3-demodulate",
                ["-v", "3", "-", "-"],
                RedirectInput: true,
                RedirectOutput: true,
                RedirectErrorToLog: false),
            new LaserDiscAc3ProcessSpec(
                "sox",
                ["-r", "40000000", "-b", "8", "-c", "1", "-e", "signed", "-t", "raw", "-", "-b", "8", "-r", "46080000", "-e", "unsigned", "-c", "1", "-t", "raw", "-"],
                RedirectInput: true,
                RedirectOutput: true,
                RedirectErrorToLog: false)
        ];
    }

    public static Stream Open(string outputFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilename);
        try
        {
            return OperatingSystem.IsWindows()
                ? new WindowsLaserDiscAc3PipeStream(outputFilename)
                : new Ac3PipeStream(outputFilename);
        }
        catch (Win32Exception ex)
        {
            throw new NotSupportedException("sox, ld-ac3-demodulate, and ld-ac3-decode are required to write LD AC3 files.", ex);
        }
    }

    private sealed class Ac3PipeStream : Stream
    {
        private readonly FileStream _log;
        private readonly Process _sox;
        private readonly Process _demodulate;
        private readonly Process _decode;
        private readonly Task _soxPump;
        private readonly Task _demodulatePump;
        private readonly Task _decodeStdoutPump;
        private readonly Task _decodeStderrPump;
        private bool _disposed;

        public Ac3PipeStream(string outputFilename)
        {
            _log = File.Create(outputFilename + ".log");
            IReadOnlyList<LaserDiscAc3ProcessSpec> specs = BuildProcessSpecs(outputFilename);
            Process? decode = null;
            Process? demodulate = null;
            Process? sox = null;
            try
            {
                decode = StartProcess(specs[0]);
                demodulate = StartProcess(specs[1]);
                sox = StartProcess(specs[2]);
            }
            catch
            {
                StopStartedProcess(sox);
                StopStartedProcess(demodulate);
                StopStartedProcess(decode);
                _log.Dispose();
                throw;
            }

            _decode = decode;
            _demodulate = demodulate;
            _sox = sox;
            _soxPump = PumpAsync(_sox.StandardOutput.BaseStream, _demodulate.StandardInput.BaseStream);
            _demodulatePump = PumpAsync(_demodulate.StandardOutput.BaseStream, _decode.StandardInput.BaseStream);
            _decodeStdoutPump = CopyToLogAsync(_decode.StandardOutput.BaseStream);
            _decodeStderrPump = CopyToLogAsync(_decode.StandardError.BaseStream);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _sox.StandardInput.BaseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _sox.StandardInput.BaseStream.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _sox.StandardInput.BaseStream.Write(buffer);
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
                _sox.StandardInput.Close();
                WaitForPipeline();
                _log.Dispose();
            }

            base.Dispose(disposing);
        }

        private static Process StartProcess(LaserDiscAc3ProcessSpec spec)
        {
            var startInfo = new ProcessStartInfo(spec.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = spec.RedirectInput,
                RedirectStandardOutput = spec.RedirectOutput,
                RedirectStandardError = spec.RedirectErrorToLog,
                CreateNoWindow = true
            };

            foreach (string argument in spec.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start {spec.FileName}.");
            }

            return process;
        }

        private static void StopStartedProcess(Process? process)
        {
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private static async Task PumpAsync(Stream source, Stream destination)
        {
            try
            {
                await source.CopyToAsync(destination).ConfigureAwait(false);
            }
            finally
            {
                destination.Dispose();
            }
        }

        private Task CopyToLogAsync(Stream source)
        {
            return Task.Run(async () =>
            {
                using (source)
                {
                    byte[] buffer = new byte[4096];
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0)
                        {
                            return;
                        }

                        lock (_log)
                        {
                            _log.Write(buffer, 0, read);
                        }
                    }
                }
            });
        }

        private void WaitForPipeline()
        {
            try
            {
                Task.WaitAll([
                    _soxPump,
                    _demodulatePump,
                    _decodeStdoutPump,
                    _decodeStderrPump
                ]);
                _sox.WaitForExit();
                _demodulate.WaitForExit();
                _decode.WaitForExit();
            }
            finally
            {
                _sox.Dispose();
                _demodulate.Dispose();
                _decode.Dispose();
            }
        }
    }
}
