using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using VHSDecode.Core.Rf;

namespace VHSDecode.Core.Dsp.CudaFast;

internal sealed class CudaFastPreviewDecodeSession : IDisposable
{
    private const int DeviceId = 0;

    // A two-second 40 MSPS PCM16 preview window carries preroll and batch
    // overscan across the following seek. Keep that bounded overlap in memory.
    internal const int FastContainerRewindSize = 32 * 1024 * 1024;

    private readonly string _inputPath;
    private readonly long _totalSourceSamples;
    private readonly IRfSampleLoader _loader;
    private readonly ICudaFastPreviewNativeSession _nativeSession;
    private readonly object _gate = new();
    private bool _disposed;

    internal CudaFastPreviewDecodeSession(
        string inputPath,
        long totalSourceSamples,
        IRfSampleLoader loader,
        ICudaFastPreviewNativeSession nativeSession,
        CudaFastRuntimeInfo runtimeInfo)
    {
        _inputPath = inputPath;
        _totalSourceSamples = totalSourceSamples;
        _loader = loader;
        _nativeSession = nativeSession;
        RuntimeInfo = runtimeInfo;
    }

    internal CudaFastRuntimeInfo RuntimeInfo { get; }

    internal static CudaFastPreviewDecodeSession Create(
        string inputPath,
        long totalSourceSamples,
        string system,
        string tapeSpeed,
        int width,
        int height,
        double outputFramesPerSecond,
        int constantQp,
        int gopLength,
        TextWriter diagnosticOutput,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSourceSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(constantQp);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(constantQp, 51);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gopLength);
        ArgumentNullException.ThrowIfNull(diagnosticOutput);

        (uint frameRateNumerator, uint frameRateDenominator) =
            Math.Abs(outputFramesPerSecond - 50.0) < 1e-9
                ? (50U, 1U)
                : Math.Abs(outputFramesPerSecond - (60_000.0 / 1_001.0)) < 1e-9
                    ? (60_000U, 1_001U)
                    : throw new NotSupportedException(
                        $"CUDA-fast preview does not support {outputFramesPerSecond:R} output FPS.");

        IRfSampleLoader? loader = null;
        ICudaFastPreviewNativeSession? nativeSession = null;
        try
        {
            ICudaFastNativeRuntime runtime = CudaFastNativeRuntime.RequireAvailable(
                diagnosticOutput,
                cancellationToken);
            CudaFastRuntimeInfo runtimeInfo = runtime.GetRuntimeInfo(DeviceId);
            nativeSession = ((ICudaFastPreviewNativeRuntime)runtime).CreatePreview(
                new CudaFastPreviewNativeConfiguration(
                    CudaFastDecodeRunner.ResolveProfile(system),
                    CudaFastDecodeRunner.ResolveTapeSpeed(tapeSpeed),
                    DeviceId,
                    SourceSampleRateMhz: 40.0,
                    DecodeSampleRateMhz: 20.0,
                    checked((uint)width),
                    checked((uint)height),
                    frameRateNumerator,
                    frameRateDenominator,
                    checked((uint)constantQp),
                    checked((uint)gopLength)));
            loader = CreatePreviewInputLoader(inputPath);
            return new CudaFastPreviewDecodeSession(
                inputPath,
                totalSourceSamples,
                loader,
                nativeSession,
                runtimeInfo);
        }
        catch
        {
            nativeSession?.Dispose();
            (loader as IDisposable)?.Dispose();
            throw;
        }
    }

    internal static IRfSampleLoader CreatePreviewInputLoader(string inputPath)
        => CudaFastDecodeRunner.CreateInputLoader(
            inputPath,
            fastContainerSeeking: true,
            fastContainerRewindSize: FastContainerRewindSize);

    internal CudaFastPreviewNativeResult DecodeWindow(
        long targetSourceSample,
        int requestedOutputFrames,
        Stream h264Destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(targetSourceSample);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            targetSourceSample,
            _totalSourceSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedOutputFrames);
        ArgumentNullException.ThrowIfNull(h264Destination);
        if (!h264Destination.CanWrite)
        {
            throw new ArgumentException("The H.264 destination must be writable.", nameof(h264Destination));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = new CudaFastDecodeRunner.ManagedReadContext(
                _loader,
                _inputPath,
                sourceStart: 0,
                _totalSourceSamples,
                cancellationToken);
            var window = new WindowContext(reader, h264Destination, cancellationToken);
            GCHandle handle = GCHandle.Alloc(window);
            try
            {
                CudaFastReadCallback readCallback = ReadCallback;
                CudaFastCancelCallback cancelCallback = CancelCallback;
                CudaFastBitstreamCallback bitstreamCallback = BitstreamCallback;
                CudaFastPreviewNativeResult result = _nativeSession.DecodeWindow(
                    new CudaFastPreviewWindowConfiguration(
                        reader.InputSampleFormat,
                        checked((ulong)_totalSourceSamples),
                        checked((ulong)targetSourceSample),
                        checked((uint)requestedOutputFrames),
                        readCallback,
                        cancelCallback,
                        bitstreamCallback,
                        GCHandle.ToIntPtr(handle)));
                window.ThrowIfFailed();
                reader.ThrowIfFailed();
                cancellationToken.ThrowIfCancellationRequested();
                if (result.FramesEncoded != checked((uint)requestedOutputFrames))
                {
                    throw new InvalidOperationException(
                        $"CUDA-fast preview encoded {result.FramesEncoded} frames; expected {requestedOutputFrames}.");
                }

                return result;
            }
            finally
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
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
            _nativeSession.Dispose();
            (_loader as IDisposable)?.Dispose();
        }
    }

    private static nuint ReadCallback(
        nint userData,
        nint destination,
        ulong sampleOffset,
        nuint sampleCount)
    {
        WindowContext context = GetContext(userData);
        try
        {
            return context.Reader.Read(destination, sampleOffset, sampleCount);
        }
        catch (Exception ex)
        {
            context.CaptureFailure(ex);
            context.Reader.CaptureFailure(ex);
            return 0;
        }
    }

    private static int CancelCallback(nint userData)
        => GetContext(userData).CancellationToken.IsCancellationRequested ? 1 : 0;

    private static unsafe int BitstreamCallback(
        nint userData,
        nint data,
        nuint byteCount)
    {
        WindowContext context = GetContext(userData);
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (byteCount > int.MaxValue)
            {
                throw new IOException(
                    $"NVENC returned an oversized {byteCount}-byte preview packet.");
            }
            context.Destination.Write(new ReadOnlySpan<byte>(
                (void*)data,
                checked((int)byteCount)));
            return 0;
        }
        catch (Exception ex)
        {
            context.CaptureFailure(ex);
            return -1;
        }
    }

    private static WindowContext GetContext(nint userData)
        => (WindowContext)(GCHandle.FromIntPtr(userData).Target
            ?? throw new InvalidOperationException(
                "CUDA-fast preview callback context was released."));

    private sealed class WindowContext(
        CudaFastDecodeRunner.ManagedReadContext reader,
        Stream destination,
        CancellationToken cancellationToken)
    {
        private ExceptionDispatchInfo? _failure;

        internal CudaFastDecodeRunner.ManagedReadContext Reader { get; } = reader;

        internal Stream Destination { get; } = destination;

        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal void CaptureFailure(Exception exception)
            => Interlocked.CompareExchange(
                ref _failure,
                ExceptionDispatchInfo.Capture(exception),
                null);

        internal void ThrowIfFailed() => _failure?.Throw();
    }
}
