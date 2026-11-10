using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace VHSDecode.Core.Dsp.CudaFast;

internal interface IVhsCudaRealFft : IDisposable
{
    int RealLength { get; }
    long ForwardCalls { get; }
    long InverseCalls { get; }

    void Forward(ReadOnlySpan<double> input, Span<Complex> output);
    void Inverse(ReadOnlySpan<Complex> input, Span<double> output);
}

internal sealed unsafe class VhsCudaRealFft : IVhsCudaRealFft
{
    private const int ErrorCapacity = 512;

    private readonly object _gate = new();
    private readonly double* _real;
    private readonly Complex* _spectrum;
    private readonly TransformDelegate _transform;
    private readonly DestroyDelegate _destroy;
    private readonly CancellationToken _cancellationToken;
    private nint _context;
    private long _forwardCalls;
    private long _inverseCalls;

    private VhsCudaRealFft(int length, nint context, double* real, Complex* spectrum,
        TransformDelegate transform, DestroyDelegate destroy, CancellationToken cancellationToken)
    {
        RealLength = length;
        _context = context;
        _real = real;
        _spectrum = spectrum;
        _transform = transform;
        _destroy = destroy;
        _cancellationToken = cancellationToken;
    }

    public int RealLength { get; }

    private int SpectrumLength => (RealLength / 2) + 1;

    public long ForwardCalls => Interlocked.Read(ref _forwardCalls);

    public long InverseCalls => Interlocked.Read(ref _inverseCalls);

    internal static VhsCudaRealFft Create(nint library, int length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var create = GetExport<CreateDelegate>(library, "vhsdecode_cuda_fast_vhs_fft64_create_v1");
        var transform = GetExport<TransformDelegate>(library, "vhsdecode_cuda_fast_vhs_fft64_transform_v1");
        var destroy = GetExport<DestroyDelegate>(library, "vhsdecode_cuda_fast_vhs_fft64_destroy_v1");
        Span<byte> error = stackalloc byte[ErrorCapacity];
        error.Clear();

        fixed (byte* message = error)
        {
            int status = create(0, length, out nint context, out double* real, out Complex* spectrum,
                message, ErrorCapacity);
            try
            {
                if (status != 0 || context == 0 || real == null || spectrum == null)
                {
                    throw new CudaFastBackendUnavailableException(
                        $"Current VHS FP64 FFT setup failed ({DescribeError(error, status)}).");
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new VhsCudaRealFft(length, context, real, spectrum, transform, destroy, cancellationToken);
            }
            catch
            {
                if (context != 0)
                {
                    destroy(context);
                }
                throw;
            }
        }
    }

    public void Forward(ReadOnlySpan<double> input, Span<Complex> output)
    {
        if (input.Length != RealLength || output.Length != SpectrumLength)
        {
            throw new ArgumentException("Invalid Current VHS forward FFT buffer geometry.");
        }

        lock (_gate)
        {
            CheckAvailable();
            input.CopyTo(new Span<double>(_real, RealLength));
            Transform(inverse: false);
            new ReadOnlySpan<Complex>(_spectrum, SpectrumLength).CopyTo(output);
            Interlocked.Increment(ref _forwardCalls);
        }
    }

    public void Inverse(ReadOnlySpan<Complex> input, Span<double> output)
    {
        if (input.Length != SpectrumLength || output.Length != RealLength)
        {
            throw new ArgumentException("Invalid Current VHS inverse FFT buffer geometry.");
        }

        lock (_gate)
        {
            CheckAvailable();
            var spectrum = new Span<Complex>(_spectrum, SpectrumLength);
            input.CopyTo(spectrum);
            // Real inverse transforms use only the real component of these two bins.
            spectrum[0] = new Complex(spectrum[0].Real, 0.0);
            spectrum[^1] = new Complex(spectrum[^1].Real, 0.0);
            Transform(inverse: true);
            new ReadOnlySpan<double>(_real, RealLength).CopyTo(output);
            Interlocked.Increment(ref _inverseCalls);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_context != 0)
            {
                _destroy(_context);
                _context = 0;
            }
        }
    }

    private void CheckAvailable()
    {
        ObjectDisposedException.ThrowIf(_context == 0, this);
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private void Transform(bool inverse)
    {
        Span<byte> error = stackalloc byte[ErrorCapacity];
        error.Clear();
        fixed (byte* message = error)
        {
            int status = _transform(_context, inverse ? 1 : 0, message, ErrorCapacity);
            if (status != 0)
            {
                throw new InvalidOperationException($"Current VHS CUDA FFT failed ({DescribeError(error, status)}).");
            }
        }

        _cancellationToken.ThrowIfCancellationRequested();
    }

    private static T GetExport<T>(nint library, string name) where T : Delegate
        => NativeLibrary.TryGetExport(library, name, out nint address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : throw new CudaFastBackendUnavailableException($"the bridge is missing '{name}' for Current VHS.");

    private static string DescribeError(ReadOnlySpan<byte> error, int status)
    {
        int terminator = error.IndexOf((byte)0);
        string detail = Encoding.UTF8.GetString(terminator >= 0 ? error[..terminator] : error);
        return string.IsNullOrEmpty(detail) ? $"status {status}" : $"status {status}: {detail}";
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateDelegate(int device, int length, out nint context, out double* real,
        out Complex* spectrum, byte* error, nuint errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TransformDelegate(nint context, int inverse, byte* error, nuint errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(nint context);
}
