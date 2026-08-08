using System.Numerics;
using VHSDecode.Core.Dsp.Ipp;

namespace VHSDecode.Core.Dsp;

public sealed unsafe class IppComplexFft64 : IDisposable
{
    public const int MaximumLength = 1 << 27;

    private readonly object _sync = new();
    private readonly IppComplexFft64SafeHandle _context;

    static IppComplexFft64()
    {
        IppComplexLayout.EnsureSupported();
    }

    public IppComplexFft64(int length)
    {
        if (length < 2
            || length > MaximumLength
            || (length & (length - 1)) != 0)
        {
            throw new ArgumentException(
                $"Complex FFT length must be a power of two from 2 through {MaximumLength}.",
                nameof(length));
        }

        _ = IppRuntime.ProbeRequired();

        nint nativeContext;
        int status;
        try
        {
            status = IppNativeMethods.ComplexFft64Create(length, out nativeContext);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            throw IppRuntime.CreateLoaderException(exception);
        }

        IppStatus.ThrowIfFailed(status, "cfft64_create");
        if (nativeContext == 0)
        {
            throw new InvalidOperationException(
                "Native IPP cfft64_create reported success but returned a null context.");
        }

        Length = length;
        _context = IppComplexFft64SafeHandle.FromNativeHandle(nativeContext);
    }

    public int Length { get; }

    public void Forward(ReadOnlySpan<Complex> input, Span<Complex> output)
        => Transform(input, output, inverse: false);

    public void Inverse(ReadOnlySpan<Complex> input, Span<Complex> output)
        => Transform(input, output, inverse: true);

    public void Dispose()
    {
        lock (_sync)
        {
            _context.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void Transform(
        ReadOnlySpan<Complex> input,
        Span<Complex> output,
        bool inverse)
    {
        if (input.Length != Length)
        {
            throw new ArgumentException(
                $"Input length must equal the configured FFT length ({Length}).",
                nameof(input));
        }

        if (output.Length < Length)
        {
            throw new ArgumentException(
                $"Output must contain at least {Length} complex elements.",
                nameof(output));
        }

        Span<Complex> destination = output[..Length];
        if (input.Overlaps(destination, out int elementOffset)
            && elementOffset != 0)
        {
            throw new ArgumentException(
                "Complex FFT input and output may be identical but must not partially overlap.",
                nameof(output));
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            fixed (Complex* inputPointer = input)
            fixed (Complex* outputPointer = destination)
            {
                int status = inverse
                    ? IppNativeMethods.ComplexFft64Inverse(
                        _context,
                        (IppComplex64*)inputPointer,
                        Length,
                        (IppComplex64*)outputPointer,
                        Length)
                    : IppNativeMethods.ComplexFft64Forward(
                        _context,
                        (IppComplex64*)inputPointer,
                        Length,
                        (IppComplex64*)outputPointer,
                        Length);
                IppStatus.ThrowIfFailed(
                    status,
                    inverse ? "cfft64_inverse" : "cfft64_forward");
            }
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_context.IsClosed, this);
}
