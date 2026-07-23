using System.Numerics;
using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

public sealed unsafe class IppRealFft : IDisposable
{
    public const int MaximumLength = 1 << 27;

    private readonly object _sync = new();
    private readonly IppFft64SafeHandle _context;

    static IppRealFft()
    {
        IppComplexLayout.EnsureSupported();
    }

    public IppRealFft(int length)
    {
        if (length < 2
            || length > MaximumLength
            || (length & (length - 1)) != 0)
        {
            throw new ArgumentException(
                $"Real FFT length must be a power of two from 2 through {MaximumLength}.",
                nameof(length));
        }

        _ = IppRuntime.ProbeRequired();

        nint nativeContext;
        int status;
        try
        {
            status = IppNativeMethods.Fft64Create(length, out nativeContext);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            throw IppRuntime.CreateLoaderException(exception);
        }

        IppStatus.ThrowIfFailed(status, "fft64_create");
        if (nativeContext == 0)
        {
            throw new InvalidOperationException(
                "Native IPP fft64_create reported success but returned a null context.");
        }

        Length = length;
        SpectrumLength = checked((length / 2) + 1);
        _context = IppFft64SafeHandle.FromNativeHandle(nativeContext);
    }

    public int Length { get; }
    public int SpectrumLength { get; }

    public void Forward(ReadOnlySpan<double> input, Span<Complex> output)
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
            fixed (double* inputPointer = input)
            fixed (Complex* outputPointer = output)
            {
                int status = IppNativeMethods.Fft64ForwardReal(
                    _context,
                    inputPointer,
                    Length,
                    (IppComplex64*)outputPointer,
                    SpectrumLength);
                IppStatus.ThrowIfFailed(status, "fft64_forward_real");
            }
        }
    }

    public void Inverse(ReadOnlySpan<Complex> input, Span<double> output)
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
            fixed (Complex* inputPointer = input)
            fixed (double* outputPointer = output)
            {
                int status = IppNativeMethods.Fft64InverseReal(
                    _context,
                    (IppComplex64*)inputPointer,
                    SpectrumLength,
                    outputPointer,
                    Length);
                IppStatus.ThrowIfFailed(status, "fft64_inverse_real");
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
