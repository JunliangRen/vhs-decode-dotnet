namespace VHSDecode.Core.Dsp.Ipp;

internal sealed unsafe class IppRealDft32 : IDisposable
{
    private readonly object _sync = new();
    private readonly IppDft32SafeHandle _context;

    internal IppRealDft32(int length)
    {
        if (length < 2 || (length & 1) != 0)
        {
            throw new ArgumentException(
                "Real DFT length must be an even integer of at least two.",
                nameof(length));
        }

        _ = IppRuntime.ProbeRequired();

        nint nativeContext;
        int status;
        try
        {
            status = IppNativeMethods.Dft32Create(length, out nativeContext);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            throw IppRuntime.CreateLoaderException(exception);
        }

        IppStatus.ThrowIfFailed(status, "dft32_create");
        if (nativeContext == 0)
        {
            throw new InvalidOperationException(
                "Native IPP dft32_create reported success but returned a null context.");
        }

        Length = length;
        SpectrumLength = checked((length / 2) + 1);
        _context = IppDft32SafeHandle.FromNativeHandle(nativeContext);
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
                $"Input length must equal the configured DFT length ({Length}).",
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
                int status = IppNativeMethods.Dft32ForwardReal(
                    _context,
                    inputPointer,
                    Length,
                    outputPointer,
                    SpectrumLength);
                IppStatus.ThrowIfFailed(status, "dft32_forward_real");
            }
        }
    }

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
                int status = IppNativeMethods.Dft32InverseReal(
                    _context,
                    inputPointer,
                    SpectrumLength,
                    outputPointer,
                    Length);
                IppStatus.ThrowIfFailed(status, "dft32_inverse_real");
            }
        }
    }

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
