using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace VHSDecode.Core.Dsp.CudaFast;

internal readonly record struct LaserDiscCudaVideoChannels(float[] Video, float[] LowPass, float[] Burst);

internal interface ILaserDiscCudaVideo : IDisposable
{
    long ProcessedBlocks { get; }

    // Calls are serial. The provider returns independent, caller-owned channels.
    LaserDiscCudaVideoChannels[] Process(double[][] rawByBlock, int[] ordinals);
}

internal sealed unsafe class LaserDiscCudaVideo : ILaserDiscCudaVideo
{
    internal const int MinBatchSize = 20;
    internal const int BatchCapacity = 22;
    private const int BlockLength = 32_768;
    private const int ChannelCount = 3;
    private const int ErrorCapacity = 512;

    private readonly double* _input;
    private readonly float* _output;
    private readonly ProcessDelegate _process;
    private readonly DestroyDelegate _destroy;
    private readonly CancellationToken _cancellationToken;
    private nint _context;

    private LaserDiscCudaVideo(nint context, double* input, float* output,
        ProcessDelegate process, DestroyDelegate destroy, CancellationToken cancellationToken)
    {
        _context = context;
        _input = input;
        _output = output;
        _process = process;
        _destroy = destroy;
        _cancellationToken = cancellationToken;
    }

    public long ProcessedBlocks { get; private set; }

    internal static bool SupportsFilters(DecodeFilterSet filters) =>
        filters.Video.Length == BlockLength && filters.VideoLowPass05.Length == BlockLength
        && filters.LdVideoBurst is { Length: BlockLength } && filters.LdVideoPilot is null
        && filters.VideoLowPass05Offset == 32 && filters.LdVideoBurstOffset == 40;

    internal static LaserDiscCudaVideo Create(nint library, DecodeFilterSet filters, CancellationToken cancellationToken)
    {
        if (!SupportsFilters(filters))
        {
            throw new ArgumentException("CUDA LD video requires standard NTSC post-FM filters.", nameof(filters));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var create = GetExport<CreateDelegate>(library, "vhsdecode_cuda_fast_ld_video_create_v1");
        var process = GetExport<ProcessDelegate>(library, "vhsdecode_cuda_fast_ld_video_process_v1");
        var destroy = GetExport<DestroyDelegate>(library, "vhsdecode_cuda_fast_ld_video_destroy_v1");
        var coefficients = new Complex[ChannelCount * BlockLength];
        filters.Video.AsSpan().CopyTo(coefficients);
        filters.VideoLowPass05.AsSpan().CopyTo(coefficients.AsSpan(BlockLength));
        filters.LdVideoBurst!.AsSpan().CopyTo(coefficients.AsSpan(2 * BlockLength));

        Span<byte> error = stackalloc byte[ErrorCapacity];
        error.Clear();
        fixed (Complex* filter = coefficients)
        fixed (byte* message = error)
        {
            int status = create(0, (double*)filter, out nint context, out double* input, out float* output,
                message, ErrorCapacity);
            if (status != 0 || context == 0 || input == null || output == null)
            {
                if (context != 0)
                {
                    destroy(context);
                }

                throw new CudaFastBackendUnavailableException(
                    $"LD video setup failed ({DescribeError(error, status)}).");
            }

            return new LaserDiscCudaVideo(context, input, output, process, destroy, cancellationToken);
        }
    }

    public LaserDiscCudaVideoChannels[] Process(double[][] rawByBlock, int[] ordinals)
    {
        ObjectDisposedException.ThrowIf(_context == 0, this);
        _cancellationToken.ThrowIfCancellationRequested();
        int count = ordinals.Length;
        if (count is < MinBatchSize or > BatchCapacity)
        {
            throw new InvalidOperationException("Invalid CUDA LD video batch size.");
        }

        for (int i = 0; i < count; i++)
        {
            double[] samples = rawByBlock[ordinals[i]];
            if (samples.Length != BlockLength)
            {
                throw new InvalidOperationException("Invalid unquantized FM block shape for CUDA LD video.");
            }

            samples.AsSpan().CopyTo(new Span<double>(_input + i * BlockLength, BlockLength));
        }

        Span<byte> error = stackalloc byte[ErrorCapacity];
        error.Clear();
        fixed (byte* message = error)
        {
            int status = _process(_context, count, message, ErrorCapacity);
            if (status != 0)
            {
                throw new InvalidOperationException($"CUDA LD video failed ({DescribeError(error, status)}).");
            }
        }

        _cancellationToken.ThrowIfCancellationRequested();
        var channels = new LaserDiscCudaVideoChannels[count];
        int planeLength = count * BlockLength;
        for (int i = 0; i < count; i++)
        {
            float* output = _output + i * BlockLength;
            channels[i] = new LaserDiscCudaVideoChannels(
                new ReadOnlySpan<float>(output, BlockLength).ToArray(),
                new ReadOnlySpan<float>(output + planeLength, BlockLength).ToArray(),
                new ReadOnlySpan<float>(output + 2 * planeLength, BlockLength).ToArray());
        }

        ProcessedBlocks += count;
        return channels;
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
                $"the installed bridge lacks LD video support ({name}). Update the CUDA bridge.");
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
    private delegate int CreateDelegate(int deviceId, double* filters, out nint context,
        out double* input, out float* output, byte* error, nuint errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProcessDelegate(nint context, int count, byte* error, nuint errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(nint context);
}
