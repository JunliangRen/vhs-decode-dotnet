using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VHSDecode.Core.HiFi;

internal interface IHiFiPreviewSink : IDisposable
{
    void Write(
        ReadOnlySpan<float> stereoSamples,
        CancellationToken cancellationToken = default);
}

internal static class HiFiPreviewSinkFactory
{
    public static IHiFiPreviewSink? TryCreate(int sampleRateHz)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return new WinMmHiFiPreviewSink(sampleRateHz);
        }
        catch (Exception ex) when (ex is IOException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return null;
        }
    }
}

internal sealed unsafe partial class WinMmHiFiPreviewSink : IHiFiPreviewSink
{
    private const uint WaveMapper = uint.MaxValue;
    private const uint WaveFormatPcm = 1;
    private const uint HeaderDone = 0x00000001;
    private const uint StillPlaying = 33;

    private readonly object _gate = new();
    private nint _device;
    private bool _disposed;

    public WinMmHiFiPreviewSink(int sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        var format = new WaveFormat
        {
            FormatTag = checked((ushort)WaveFormatPcm),
            Channels = 2,
            SamplesPerSecond = checked((uint)sampleRateHz),
            AverageBytesPerSecond = checked((uint)(sampleRateHz * 4)),
            BlockAlign = 4,
            BitsPerSample = 16,
            ExtraSize = 0
        };
        uint result = NativeMethods.waveOutOpen(
            out _device,
            WaveMapper,
            in format,
            0,
            0,
            0);
        if (result != 0)
        {
            throw CreateException(result, "open");
        }
    }

    public void Write(
        ReadOnlySpan<float> stereoSamples,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        short[] samples = ConvertToPcm16(stereoSamples);
        if (samples.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            WritePcm(samples, cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_device != 0)
            {
                _ = NativeMethods.waveOutReset(_device);
                uint result = NativeMethods.waveOutClose(_device);
                _device = 0;
                if (result != 0)
                {
                    throw CreateException(result, "close");
                }
            }
        }
    }

    internal static short[] ConvertToPcm16(ReadOnlySpan<float> stereoSamples)
    {
        int sampleCount = stereoSamples.Length - (stereoSamples.Length % 2);
        var output = new short[sampleCount];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = QuantizeSample(stereoSamples[i]);
        }

        return output;
    }

    internal static short QuantizeSample(float sample)
    {
        if (!float.IsFinite(sample))
        {
            return 0;
        }

        double truncated = Math.Truncate(sample * 32_768.0);
        if (truncated < long.MinValue || truncated > long.MaxValue)
        {
            return 0;
        }

        return unchecked((short)(long)truncated);
    }

    private void WritePcm(short[] samples, CancellationToken cancellationToken)
    {
        int headerSize = Marshal.SizeOf<WaveHeader>();
        GCHandle samplesHandle = default;
        nint headerPointer = 0;
        bool prepared = false;
        try
        {
            samplesHandle = GCHandle.Alloc(samples, GCHandleType.Pinned);
            headerPointer = Marshal.AllocHGlobal(headerSize);
            var header = new WaveHeader
            {
                Data = samplesHandle.AddrOfPinnedObject(),
                BufferLength = checked((uint)(samples.Length * sizeof(short)))
            };
            Marshal.StructureToPtr(header, headerPointer, fDeleteOld: false);
            ThrowIfError(
                NativeMethods.waveOutPrepareHeader(
                    _device,
                    headerPointer,
                    checked((uint)headerSize)),
                "prepare preview buffer");
            prepared = true;
            ThrowIfError(
                NativeMethods.waveOutWrite(
                    _device,
                    headerPointer,
                    checked((uint)headerSize)),
                "write preview buffer");

            while ((Marshal.PtrToStructure<WaveHeader>(headerPointer).Flags & HeaderDone) == 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ThrowIfError(NativeMethods.waveOutReset(_device), "cancel preview playback");
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Thread.Sleep(1);
            }
        }
        finally
        {
            if (prepared)
            {
                UnprepareHeader(headerPointer, headerSize);
            }

            if (headerPointer != 0)
            {
                Marshal.FreeHGlobal(headerPointer);
            }

            if (samplesHandle.IsAllocated)
            {
                samplesHandle.Free();
            }
        }
    }

    private void UnprepareHeader(nint headerPointer, int headerSize)
    {
        while (true)
        {
            uint result = NativeMethods.waveOutUnprepareHeader(
                _device,
                headerPointer,
                checked((uint)headerSize));
            if (result == 0)
            {
                return;
            }

            if (result != StillPlaying)
            {
                throw CreateException(result, "unprepare preview buffer");
            }

            Thread.Sleep(1);
        }
    }

    private static void ThrowIfError(uint result, string operation)
    {
        if (result != 0)
        {
            throw CreateException(result, operation);
        }
    }

    private static IOException CreateException(uint result, string operation)
    {
        Span<char> message = stackalloc char[256];
        uint formatResult;
        fixed (char* messagePointer = message)
        {
            formatResult = NativeMethods.waveOutGetErrorText(
                result,
                messagePointer,
                checked((uint)message.Length));
        }

        int terminator = message.IndexOf('\0');
        string detail = formatResult == 0
            ? new string(terminator < 0 ? message : message[..terminator])
            : new Win32Exception(checked((int)result)).Message;
        return new IOException(
            $"Unable to {operation} HiFi preview audio: {detail} (MMRESULT {result}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint UserData;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nuint Reserved;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("winmm.dll")]
        public static partial uint waveOutOpen(
            out nint device,
            uint deviceId,
            in WaveFormat format,
            nuint callback,
            nuint instance,
            uint flags);

        [LibraryImport("winmm.dll")]
        public static partial uint waveOutPrepareHeader(
            nint device,
            nint header,
            uint headerSize);

        [LibraryImport("winmm.dll")]
        public static partial uint waveOutWrite(
            nint device,
            nint header,
            uint headerSize);

        [LibraryImport("winmm.dll")]
        public static partial uint waveOutUnprepareHeader(
            nint device,
            nint header,
            uint headerSize);

        [LibraryImport("winmm.dll")]
        public static partial uint waveOutReset(nint device);

        [LibraryImport("winmm.dll")]
        public static partial uint waveOutClose(nint device);

        [LibraryImport("winmm.dll", EntryPoint = "waveOutGetErrorTextW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint waveOutGetErrorText(
            uint error,
            char* message,
            uint messageLength);
    }
}
