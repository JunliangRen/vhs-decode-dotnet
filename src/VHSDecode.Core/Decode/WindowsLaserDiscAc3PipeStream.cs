using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using VHSDecode.Core.Processes;

namespace VHSDecode.Core.Decode;

internal sealed class WindowsLaserDiscAc3PipeStream : Stream
{
    private readonly FileStream _input;
    private readonly IReadOnlyList<Process> _processes;
    private bool _disposed;

    public WindowsLaserDiscAc3PipeStream(string outputFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilename);
        IReadOnlyList<LaserDiscAc3ProcessSpec> specs = LaserDiscAc3Pipe.BuildProcessSpecs(outputFilename);
        using var log = new FileStream(outputFilename + ".log", FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? soxRead = null;
        SafeFileHandle? soxWrite = null;
        SafeFileHandle? demodulateRead = null;
        SafeFileHandle? demodulateWrite = null;
        Process? decode = null;
        Process? demodulate = null;
        Process? sox = null;
        try
        {
            SafeFileHandle.CreateAnonymousPipe(out inputRead, out inputWrite);
            SafeFileHandle.CreateAnonymousPipe(out soxRead, out soxWrite);
            SafeFileHandle.CreateAnonymousPipe(out demodulateRead, out demodulateWrite);
            decode = StartProcess(specs[0], demodulateRead, log.SafeFileHandle, log.SafeFileHandle);
            demodulate = StartProcess(specs[1], soxRead, demodulateWrite);
            sox = StartProcess(specs[2], inputRead, soxWrite);
            _input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 4096, isAsync: false);
            inputWrite = null; // The FileStream now owns this end of the input pipe.
            _processes = [sox, demodulate, decode];
        }
        catch
        {
            StopAndDispose(sox);
            StopAndDispose(demodulate);
            StopAndDispose(decode);
            throw;
        }
        finally
        {
            inputRead?.Dispose();
            inputWrite?.Dispose();
            soxRead?.Dispose();
            soxWrite?.Dispose();
            demodulateRead?.Dispose();
            demodulateWrite?.Dispose();
        }
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

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _input.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _input.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _input.Write(buffer);
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
            try
            {
                _input.Dispose();
                foreach (Process process in _processes)
                {
                    process.WaitForExitStatus();
                }
            }
            finally
            {
                foreach (Process process in _processes)
                {
                    StopAndDispose(process);
                }
            }
        }

        base.Dispose(disposing);
    }

    private static Process StartProcess(
        LaserDiscAc3ProcessSpec spec,
        SafeFileHandle input,
        SafeFileHandle output,
        SafeFileHandle? error = null)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputHandle = input,
            StandardOutputHandle = output,
            StandardErrorHandle = error,
            // Process.Start adds the standard handles to this whitelist.
            InheritedHandles = []
        };
        ExternalToolProcess.Configure(startInfo);
        foreach (string argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {spec.FileName} for the LD AC3 pipeline.");
    }

    private static void StopAndDispose(Process? process)
    {
        if (process is not null)
        {
            ExternalToolProcess.Stop(process);
            process.Dispose();
        }
    }
}
