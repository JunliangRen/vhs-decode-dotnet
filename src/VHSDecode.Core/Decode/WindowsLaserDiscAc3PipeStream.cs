using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Decode;

internal sealed unsafe partial class WindowsLaserDiscAc3PipeStream : Stream
{
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint Infinite = 0xffffffff;
    private const int StdErrorHandle = -12;

    private static readonly object HandleInheritanceGate = new();

    private readonly FileStream _input;
    private readonly IReadOnlyList<SafeWaitHandle> _processes;
    private bool _disposed;

    public WindowsLaserDiscAc3PipeStream(string outputFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilename);
        IReadOnlyList<LaserDiscAc3ProcessSpec> specs = LaserDiscAc3Pipe.BuildProcessSpecs(
            outputFilename);
        FileStream? log = null;
        SafeFileHandle? inheritedError = null;
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? soxToDemodulateRead = null;
        SafeFileHandle? soxToDemodulateWrite = null;
        SafeFileHandle? demodulateToDecodeRead = null;
        SafeFileHandle? demodulateToDecodeWrite = null;
        FileStream? input = null;
        SafeWaitHandle? decode = null;
        SafeWaitHandle? demodulate = null;
        SafeWaitHandle? sox = null;
        try
        {
            log = new FileStream(
                outputFilename + ".log",
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite);
            inheritedError = DuplicateStandardError();
            (inputRead, inputWrite) = CreatePipePair();
            (soxToDemodulateRead, soxToDemodulateWrite) = CreatePipePair();
            (demodulateToDecodeRead, demodulateToDecodeWrite) = CreatePipePair();

            decode = StartProcess(
                specs[0],
                demodulateToDecodeRead,
                log.SafeFileHandle,
                log.SafeFileHandle);
            demodulate = StartProcess(
                specs[1],
                soxToDemodulateRead,
                demodulateToDecodeWrite,
                inheritedError);
            sox = StartProcess(
                specs[2],
                inputRead,
                soxToDemodulateWrite,
                inheritedError);

            input = new FileStream(
                inputWrite,
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false);
            inputWrite = null;
        }
        catch
        {
            input?.Dispose();
            TerminateAndDispose(sox);
            TerminateAndDispose(demodulate);
            TerminateAndDispose(decode);
            throw;
        }
        finally
        {
            inputRead?.Dispose();
            inputWrite?.Dispose();
            soxToDemodulateRead?.Dispose();
            soxToDemodulateWrite?.Dispose();
            demodulateToDecodeRead?.Dispose();
            demodulateToDecodeWrite?.Dispose();
            inheritedError?.Dispose();
            log?.Dispose();
        }

        _input = input;
        _processes = [sox, demodulate, decode];
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

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

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
            Exception? failure = null;
            try
            {
                _input.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                failure = ex;
            }

            try
            {
                foreach (SafeWaitHandle process in _processes)
                {
                    uint waitResult = NativeMethods.WaitForSingleObject(
                        process.DangerousGetHandle(),
                        Infinite);
                    if (waitResult == uint.MaxValue && failure is null)
                    {
                        failure = new Win32Exception(
                            Marshal.GetLastPInvokeError(),
                            "Unable to wait for the LD AC3 pipeline.");
                    }
                }
            }
            finally
            {
                foreach (SafeWaitHandle process in _processes)
                {
                    process.Dispose();
                }
            }

            if (failure is not null)
            {
                throw failure;
            }
        }

        base.Dispose(disposing);
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipePair()
    {
        var attributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            SecurityDescriptor = 0,
            InheritHandle = 0
        };
        if (NativeMethods.CreatePipe(
            out nint readHandle,
            out nint writeHandle,
            ref attributes,
            0) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Unable to create an LD AC3 pipeline pipe.");
        }

        return (
            new SafeFileHandle(readHandle, ownsHandle: true),
            new SafeFileHandle(writeHandle, ownsHandle: true));
    }

    private static SafeFileHandle DuplicateStandardError()
    {
        nint standardError = NativeMethods.GetStdHandle(StdErrorHandle);
        if (standardError == 0 || standardError == -1)
        {
            return File.OpenHandle(
                "NUL",
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
        }

        nint currentProcess = NativeMethods.GetCurrentProcess();
        if (NativeMethods.DuplicateHandle(
            currentProcess,
            standardError,
            currentProcess,
            out nint duplicate,
            0,
            0,
            DuplicateSameAccess) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Unable to duplicate stderr for the LD AC3 pipeline.");
        }

        return new SafeFileHandle(duplicate, ownsHandle: true);
    }

    private static SafeWaitHandle StartProcess(
        LaserDiscAc3ProcessSpec spec,
        SafeFileHandle standardInput,
        SafeFileHandle standardOutput,
        SafeFileHandle standardError)
    {
        string commandLine = BuildCommandLine(spec.FileName, spec.Arguments);
        var startupInfo = new StartupInfo
        {
            Size = checked((uint)Marshal.SizeOf<StartupInfo>()),
            Flags = StartfUseStdHandles,
            StandardInput = standardInput.DangerousGetHandle(),
            StandardOutput = standardOutput.DangerousGetHandle(),
            StandardError = standardError.DangerousGetHandle()
        };
        ProcessInformation processInformation;
        lock (HandleInheritanceGate)
        {
            SafeFileHandle[] inheritedHandles = DistinctHandles(
                standardInput,
                standardOutput,
                standardError);
            foreach (SafeFileHandle handle in inheritedHandles)
            {
                SetInheritable(handle, true);
            }

            try
            {
                char[] mutableCommandLine = (commandLine + '\0').ToCharArray();
                fixed (char* commandLinePointer = mutableCommandLine)
                {
                    if (NativeMethods.CreateProcessW(
                        0,
                        commandLinePointer,
                        0,
                        0,
                        1,
                        CreateNoWindow,
                        0,
                        0,
                        ref startupInfo,
                        out processInformation) == 0)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastPInvokeError(),
                            $"Unable to start {spec.FileName} for the LD AC3 pipeline.");
                    }
                }
            }
            finally
            {
                foreach (SafeFileHandle handle in inheritedHandles)
                {
                    SetInheritable(handle, false);
                }
            }
        }

        NativeMethods.CloseHandle(processInformation.Thread);
        return new SafeWaitHandle(processInformation.Process, ownsHandle: true);
    }

    private static SafeFileHandle[] DistinctHandles(params SafeFileHandle[] handles)
        => handles
            .DistinctBy(handle => handle.DangerousGetHandle())
            .ToArray();

    private static void SetInheritable(SafeFileHandle handle, bool inheritable)
    {
        if (NativeMethods.SetHandleInformation(
            handle.DangerousGetHandle(),
            HandleFlagInherit,
            inheritable ? HandleFlagInherit : 0) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Unable to configure LD AC3 pipeline handle inheritance.");
        }
    }

    private static string BuildCommandLine(
        string fileName,
        IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteArgument(fileName));
        foreach (string argument in arguments)
        {
            commandLine.Append(' ');
            commandLine.Append(QuoteArgument(argument));
        }

        return commandLine.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0
            && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2);
        quoted.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
                quoted.Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes);
            backslashes = 0;
            quoted.Append(character);
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    private static void TerminateAndDispose(SafeWaitHandle? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            NativeMethods.TerminateProcess(process.DangerousGetHandle(), 1);
            NativeMethods.WaitForSingleObject(process.DangerousGetHandle(), Infinite);
        }
        finally
        {
            process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public nint ReservedBytes;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int CreatePipe(
            out nint readPipe,
            out nint writePipe,
            ref SecurityAttributes pipeAttributes,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int SetHandleInformation(
            nint handle,
            uint mask,
            uint flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int DuplicateHandle(
            nint sourceProcess,
            nint sourceHandle,
            nint targetProcess,
            out nint targetHandle,
            uint desiredAccess,
            int inheritHandle,
            uint options);

        [LibraryImport("kernel32.dll")]
        public static partial nint GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint GetStdHandle(int standardHandle);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
        public static partial int CreateProcessW(
            nint applicationName,
            char* commandLine,
            nint processAttributes,
            nint threadAttributes,
            int inheritHandles,
            uint creationFlags,
            nint environment,
            nint currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int TerminateProcess(nint process, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int CloseHandle(nint handle);
    }
}
