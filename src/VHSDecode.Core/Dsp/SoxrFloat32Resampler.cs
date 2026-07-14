using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Dsp;

public enum SoxrQuality : uint
{
    Quick = 0,
    Low = 1,
    Medium = 2,
    High = 4,
    VeryHigh = 6
}

public sealed unsafe class SoxrFloat32Resampler : IDisposable
{
    private const int InitialBufferBytes = 1024;

    private readonly SoxrSafeHandle _handle;
    private readonly double _outputInputRatio;
    private readonly int _divideLength;
    private float[] _outputBuffer = [];
    private bool _ended;

    public SoxrFloat32Resampler(
        double inputRate,
        double outputRate,
        SoxrQuality quality)
    {
        if (!double.IsFinite(inputRate) || inputRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        }

        if (!double.IsFinite(outputRate) || outputRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputRate));
        }

        if (!Enum.IsDefined(quality))
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        Quality = quality;
        _outputInputRatio = outputRate / inputRate;
        double divideLength = Math.Max(1000.0, 48_000.0 * inputRate / outputRate);
        _divideLength = divideLength >= int.MaxValue
            ? int.MaxValue
            : checked((int)Math.Truncate(divideLength));

        SoxrIoSpec ioSpec = NativeMethods.soxr_io_spec(0, 0);
        SoxrQualitySpec qualitySpec = NativeMethods.soxr_quality_spec((uint)quality, 0);
        _handle = NativeMethods.soxr_create(
            inputRate,
            outputRate,
            1,
            out nint error,
            in ioSpec,
            in qualitySpec,
            0);
        if (error != 0)
        {
            _handle.Dispose();
            throw CreateNativeException(error);
        }

        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            throw new InvalidOperationException("libsoxr returned an invalid resampler handle.");
        }
    }

    public double InputRate { get; }
    public double OutputRate { get; }
    public SoxrQuality Quality { get; }
    public bool Ended => _ended;

    public static string LibraryVersion
        => Marshal.PtrToStringAnsi(NativeMethods.soxr_version())
            ?? throw new InvalidOperationException("libsoxr returned no version string.");

    public float[] Process(ReadOnlySpan<float> input, bool last = false)
    {
        ThrowIfDisposed();
        if (_ended)
        {
            throw new InvalidOperationException("Input after last input");
        }

        double requiredFramesValue = NativeMethods.soxr_delay(_handle)
            + (input.Length * _outputInputRatio)
            + 1.0;
        if (!double.IsFinite(requiredFramesValue)
            || requiredFramesValue < 0.0
            || requiredFramesValue > int.MaxValue)
        {
            throw new InvalidOperationException("libsoxr output buffer size is out of range.");
        }

        int requiredFrames = (int)requiredFramesValue;
        EnsureOutputBuffer(checked(requiredFrames * sizeof(float)), copy: false);
        int outputPosition = 0;

        fixed (float* inputPointer = input)
        {
            for (int inputPosition = 0; inputPosition < input.Length; inputPosition += _divideLength)
            {
                int inputLength = Math.Min(_divideLength, input.Length - inputPosition);
                float* currentInput = inputPointer + inputPosition;
                ProcessNative(currentInput, inputLength, ref outputPosition);
                if (_outputBuffer.Length <= outputPosition)
                {
                    Flush(currentInput, ref outputPosition);
                }
            }

            if (last)
            {
                _ended = true;
                Flush(null, ref outputPosition);
            }
        }

        return _outputBuffer.AsSpan(0, outputPosition).ToArray();
    }

    public float[] ProcessFinalBlock(ReadOnlySpan<float> input)
    {
        float[] output = Process(input, last: true);
        Clear();
        return output;
    }

    public void Clear()
    {
        ThrowIfDisposed();
        ThrowIfNativeError(NativeMethods.soxr_clear(_handle));
        _ended = false;
    }

    public void Dispose()
    {
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ProcessNative(float* input, int inputLength, ref int outputPosition)
    {
        nuint outputLength = 0;
        fixed (float* outputPointer = _outputBuffer)
        {
            nint error = NativeMethods.soxr_process(
                _handle,
                input,
                checked((nuint)inputLength),
                null,
                outputPointer + outputPosition,
                checked((nuint)(_outputBuffer.Length - outputPosition)),
                &outputLength);
            ThrowIfNativeError(error);
        }

        outputPosition = checked(outputPosition + (int)outputLength);
    }

    private void Flush(float* inputMarker, ref int outputPosition)
    {
        nuint outputLength;
        do
        {
            if (_outputBuffer.Length <= outputPosition)
            {
                EnsureOutputBuffer(checked(_outputBuffer.Length * sizeof(float) * 2), copy: true);
            }

            outputLength = 0;
            fixed (float* outputPointer = _outputBuffer)
            {
                nint error = NativeMethods.soxr_process(
                    _handle,
                    inputMarker,
                    0,
                    null,
                    outputPointer + outputPosition,
                    checked((nuint)(_outputBuffer.Length - outputPosition)),
                    &outputLength);
                ThrowIfNativeError(error);
            }

            outputPosition = checked(outputPosition + (int)outputLength);
        }
        while (outputLength > 0);
    }

    private void EnsureOutputBuffer(int requiredBytes, bool copy)
    {
        int currentBytes = checked(_outputBuffer.Length * sizeof(float));
        if (_outputBuffer.Length > 0 && requiredBytes < currentBytes)
        {
            return;
        }

        int newBytes = InitialBufferBytes;
        while (newBytes < requiredBytes)
        {
            newBytes = checked(newBytes * 2);
        }

        var replacement = new float[newBytes / sizeof(float)];
        if (copy && _outputBuffer.Length > 0)
        {
            _outputBuffer.CopyTo(replacement, 0);
        }

        _outputBuffer = replacement;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

    private static void ThrowIfNativeError(nint error)
    {
        if (error != 0)
        {
            throw CreateNativeException(error);
        }
    }

    private static InvalidOperationException CreateNativeException(nint error)
        => new(Marshal.PtrToStringAnsi(error) ?? "Unknown libsoxr error.");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SoxrIoSpec
    {
        private readonly int _inputType;
        private readonly int _outputType;
        private readonly double _scale;
        private readonly nint _reserved;
        private readonly uint _flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SoxrQualitySpec
    {
        private readonly double _precision;
        private readonly double _phaseResponse;
        private readonly double _passbandEnd;
        private readonly double _stopbandBegin;
        private readonly nint _reserved;
        private readonly uint _flags;
    }

    private sealed class SoxrSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SoxrSafeHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.soxr_delete(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        private const string LibraryName = "soxr";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern nint soxr_version();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern SoxrIoSpec soxr_io_spec(int inputType, int outputType);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern SoxrQualitySpec soxr_quality_spec(uint recipe, uint flags);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern SoxrSafeHandle soxr_create(
            double inputRate,
            double outputRate,
            uint channels,
            out nint error,
            in SoxrIoSpec ioSpec,
            in SoxrQualitySpec qualitySpec,
            nint runtimeSpec);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern nint soxr_process(
            SoxrSafeHandle handle,
            float* input,
            nuint inputLength,
            nuint* inputDone,
            float* output,
            nuint outputLength,
            nuint* outputDone);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern double soxr_delay(SoxrSafeHandle handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern nint soxr_clear(SoxrSafeHandle handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void soxr_delete(nint handle);
    }
}
