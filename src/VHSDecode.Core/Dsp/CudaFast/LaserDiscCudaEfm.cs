using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using VHSDecode.Core.Decode;

namespace VHSDecode.Core.Dsp.CudaFast;

internal sealed unsafe class LaserDiscCudaEfm : ILaserDiscCudaEfm
{
    private const int BatchSize = 22;
    private const int BlockLength = LaserDiscEfmScratchHandoff.BlockLength;
    private const int ErrorCapacity = 512;

    private readonly double* _input;
    private readonly short* _output;
    private readonly ProcessDelegate _process;
    private readonly DestroyDelegate _destroy;
    private readonly CancellationToken _cancellationToken;
    private nint _context;

    private LaserDiscCudaEfm(
        nint context, double* input, short* output, ProcessDelegate process,
        DestroyDelegate destroy, string diagnostic, CancellationToken cancellationToken)
    {
        _context = context;
        _input = input;
        _output = output;
        _process = process;
        _destroy = destroy;
        Diagnostic = diagnostic;
        _cancellationToken = cancellationToken;
    }

    public string Diagnostic { get; }
    public long ProcessedBlocks { get; private set; }

    internal static LaserDiscCudaEfm Create(
        nint library, Complex[] filter, CudaFastRuntimeInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Length != BlockLength)
        {
            throw new ArgumentException("The LD CUDA EFM filter must contain 32768 samples.", nameof(filter));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var create = GetExport<CreateDelegate>(library, "vhsdecode_cuda_fast_ld_efm_create_v1");
        var process = GetExport<ProcessDelegate>(library, "vhsdecode_cuda_fast_ld_efm_process_v1");
        var destroy = GetExport<DestroyDelegate>(library, "vhsdecode_cuda_fast_ld_efm_destroy_v1");
        Span<byte> error = stackalloc byte[ErrorCapacity];
        error.Clear();
        fixed (Complex* coefficients = filter)
        fixed (byte* message = error)
        {
            int status = create(0, (double*)coefficients, out nint context, out double* input, out short* output,
                message, ErrorCapacity);
            if (status != 0 || context == 0 || input == null || output == null)
            {
                if (context != 0)
                {
                    destroy(context);
                }

                throw new CudaFastBackendUnavailableException(
                    $"LD EFM setup failed ({DescribeError(error, status)}).");
            }

            return new LaserDiscCudaEfm(context, input, output, process, destroy,
                $"CUDA-fast LD EFM: {info.DeviceName} (compute {info.ComputeMajor}.{info.ComputeMinor}, bridge ABI 0x{info.AbiVersion:X8}).",
                cancellationToken);
        }
    }

    public short[][] Process(double[][] preparedInputs, int[] ordinals, int count)
    {
        ObjectDisposedException.ThrowIf(_context == 0, this);
        var result = new short[count][];
        Span<byte> error = stackalloc byte[ErrorCapacity];
        for (int start = 0; start < count; start += BatchSize)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int batch = Math.Min(BatchSize, count - start);
            for (int i = 0; i < batch; i++)
            {
                double[] input = preparedInputs[ordinals[start + i]];
                if (input.Length != BlockLength)
                {
                    throw new InvalidOperationException("Invalid RF block size for CUDA LD EFM.");
                }

                input.AsSpan().CopyTo(new Span<double>(_input + i * BlockLength, BlockLength));
            }

            error.Clear();
            fixed (byte* message = error)
            {
                int status = _process(_context, batch, message, ErrorCapacity);
                if (status != 0)
                {
                    throw new InvalidOperationException($"CUDA LD EFM failed ({DescribeError(error, status)}).");
                }
            }

            _cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < batch; i++)
            {
                result[start + i] = new ReadOnlySpan<short>(_output + i * BlockLength, BlockLength).ToArray();
            }

            ProcessedBlocks += batch;
        }

        return result;
    }

    public void Dispose()
    {
        nint context = Interlocked.Exchange(ref _context, 0);
        if (context != 0)
        {
            _destroy(context);
        }
    }

    private static T GetExport<T>(nint library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out nint address))
        {
            throw new CudaFastBackendUnavailableException(
                $"the installed bridge lacks LD EFM support ({name}).");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static string DescribeError(ReadOnlySpan<byte> error, int status)
    {
        int end = error.IndexOf((byte)0);
        return end == 0
            ? $"native status {status}"
            : $"native status {status}: {Encoding.UTF8.GetString(end < 0 ? error : error[..end])}";
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateDelegate(
        int deviceId, double* filter, out nint context, out double* input, out short* output,
        byte* error, nuint errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProcessDelegate(nint context, int count, byte* error, nuint errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(nint context);
}
