namespace VHSDecode.Core.Dsp.Ipp;

internal sealed unsafe class IppRealFft32 : IDisposable
{
    internal const int MaximumLength = 1 << 27;

    private readonly object _sync = new();
    private readonly IppFft32SafeHandle _context;

    internal IppRealFft32(int length)
    {
        if (length < 2
            || length > MaximumLength
            || (length & (length - 1)) != 0)
        {
            throw new ArgumentException(
                $"Real FFT length must be a power of two from 2 through {MaximumLength}.",
                nameof(length));
        }

        IppComplexLayout.EnsureSupported();
        _ = IppRuntime.ProbeRequired();

        nint nativeContext;
        int status;
        try
        {
            status = IppNativeMethods.Fft32Create(length, out nativeContext);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            throw IppRuntime.CreateLoaderException(exception);
        }

        IppStatus.ThrowIfFailed(status, "fft32_create");
        if (nativeContext == 0)
        {
            throw new InvalidOperationException(
                "Native IPP fft32_create reported success but returned a null context.");
        }

        Length = length;
        SpectrumLength = checked((length / 2) + 1);
        _context = IppFft32SafeHandle.FromNativeHandle(nativeContext);
    }

    internal int Length { get; }

    internal int SpectrumLength { get; }

    internal void Forward(
        ReadOnlySpan<float> input,
        Span<IppComplex32> output)
    {
        if (input.Length != Length)
        {
            throw new ArgumentException(
                $"Input length must equal the configured FFT length ({Length}).",
                nameof(input));
        }
        if (output.Length < SpectrumLength)
        {
            throw new ArgumentException(
                $"Output must contain at least {SpectrumLength} complex elements.",
                nameof(output));
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (float* inputPointer = input)
            fixed (IppComplex32* outputPointer = output)
            {
                int status = IppNativeMethods.Fft32ForwardReal(
                    _context,
                    inputPointer,
                    Length,
                    outputPointer,
                    SpectrumLength);
                IppStatus.ThrowIfFailed(status, "fft32_forward_real");
            }
        }
    }

    internal void Forward(
        ReadOnlySpan<float> input,
        Span<Complex32> output)
        => Forward(
            input,
            System.Runtime.InteropServices.MemoryMarshal.Cast<Complex32, IppComplex32>(output));

    internal void Inverse(
        ReadOnlySpan<IppComplex32> input,
        Span<float> output)
    {
        if (input.Length != SpectrumLength)
        {
            throw new ArgumentException(
                $"Input spectrum length must equal {SpectrumLength}.",
                nameof(input));
        }
        if (output.Length < Length)
        {
            throw new ArgumentException(
                $"Output must contain at least {Length} real elements.",
                nameof(output));
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (IppComplex32* inputPointer = input)
            fixed (float* outputPointer = output)
            {
                int status = IppNativeMethods.Fft32InverseReal(
                    _context,
                    inputPointer,
                    SpectrumLength,
                    outputPointer,
                    Length);
                IppStatus.ThrowIfFailed(status, "fft32_inverse_real");
            }
        }
    }

    internal void Inverse(
        ReadOnlySpan<Complex32> input,
        Span<float> output)
        => Inverse(
            System.Runtime.InteropServices.MemoryMarshal.Cast<Complex32, IppComplex32>(input),
            output);

    public void Dispose()
    {
        lock (_sync)
        {
            _context.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_context.IsClosed, this);
}
