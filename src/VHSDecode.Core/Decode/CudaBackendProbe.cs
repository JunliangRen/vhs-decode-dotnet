using Microsoft.Win32.SafeHandles;

namespace VHSDecode.Core.Compute.Cuda;

/// <summary>
/// Resolves and validates the optional CUDA component before decode outputs are opened.
/// A successful result owns the native CUDA context and must be disposed.
/// </summary>
internal sealed class CudaBackendProbe(ICudaNativeApiLoader? loader = null)
{
    private readonly ICudaNativeApiLoader _loader = loader ?? new NativeCudaApiLoader();

    internal CudaBackendProbeResult Probe(CudaBackendProbeRequest request)
    {
        CudaBackendProbeResult result = ProbeCore(request);
        if (request.Diagnostic is not null)
        {
            try
            {
                request.Diagnostic(result.Message);
            }
            catch
            {
                // A diagnostic sink must not alter backend selection or leak a ready context.
            }
        }

        return result;
    }

    private CudaBackendProbeResult ProbeCore(CudaBackendProbeRequest request)
    {
        if (request.DeviceIndex < 0)
        {
            return Failure(
                request,
                CudaBackendFailure.InvalidDeviceIndex,
                $"CUDA device index {request.DeviceIndex} is invalid; the index must be non-negative.");
        }

        if (request.BlockLength < 2 || (request.BlockLength & 1) != 0)
        {
            return Failure(
                request,
                CudaBackendFailure.InvalidBlockLength,
                $"CUDA RF block length {request.BlockLength} is invalid; the length must be even and at least 2.");
        }

        CudaNativeApiLoadResult loadResult;
        try
        {
            loadResult = _loader.Load(request.ComponentDirectory);
        }
        catch (Exception ex)
        {
            return Failure(
                request,
                CudaBackendFailure.ComponentLoadFailed,
                $"CUDA component discovery failed: {ex.Message}");
        }

        if (!loadResult.Succeeded)
        {
            loadResult.Api?.Dispose();
            return Failure(request, loadResult.Failure, loadResult.Message);
        }

        ICudaNativeApi api = loadResult.Api!;
        CudaBackendContext? context = null;
        try
        {
            uint abiVersion = api.GetAbiVersion();
            if (abiVersion != CudaNativeAbi.Version)
            {
                api.Dispose();
                return Failure(
                    request,
                    CudaBackendFailure.AbiMismatch,
                    $"CUDA component ABI {abiVersion} is incompatible; ABI {CudaNativeAbi.Version} is required.");
            }

            CudaNativeStatus status = api.GetDeviceCount(out int deviceCount);
            if (status != CudaNativeStatus.Success)
            {
                string detail = NativeFailureDetail(api, nint.Zero, status);
                api.Dispose();
                return Failure(
                    request,
                    status is CudaNativeStatus.CudaUnavailable or CudaNativeStatus.DeviceNotFound
                        ? CudaBackendFailure.NoDevices
                        : CudaBackendFailure.NativeProbeFailed,
                    $"CUDA device enumeration failed: {detail}");
            }

            if (deviceCount <= 0)
            {
                api.Dispose();
                return Failure(
                    request,
                    CudaBackendFailure.NoDevices,
                    "No CUDA-capable devices are visible.");
            }

            if (request.DeviceIndex >= deviceCount)
            {
                api.Dispose();
                return Failure(
                    request,
                    CudaBackendFailure.InvalidDeviceIndex,
                    $"CUDA device index {request.DeviceIndex} is invalid; {deviceCount} device(s) are visible.");
            }

            status = api.GetDeviceInfo(request.DeviceIndex, out CudaDeviceInfo deviceInfo);
            if (status != CudaNativeStatus.Success)
            {
                string detail = NativeFailureDetail(api, nint.Zero, status);
                api.Dispose();
                return Failure(
                    request,
                    status == CudaNativeStatus.DeviceUnsupported
                        ? CudaBackendFailure.DeviceUnsupported
                        : CudaBackendFailure.NativeProbeFailed,
                    $"CUDA device {request.DeviceIndex} could not be inspected: {detail}");
            }

            if (!deviceInfo.MeetsMinimumComputeCapability || !deviceInfo.SupportsFp64)
            {
                api.Dispose();
                string capability = $"{deviceInfo.ComputeCapabilityMajor}.{deviceInfo.ComputeCapabilityMinor}";
                return Failure(
                    request,
                    CudaBackendFailure.DeviceUnsupported,
                    $"CUDA device {request.DeviceIndex} ({deviceInfo.Name}) is unsupported: " +
                    $"compute capability {capability} with FP64 support is required (minimum 7.5).");
            }

            status = api.CreateContext(request.DeviceIndex, out nint nativeContext);
            if (status != CudaNativeStatus.Success || nativeContext == nint.Zero)
            {
                string detail = NativeFailureDetail(api, nativeContext, status);
                if (nativeContext != nint.Zero)
                {
                    try
                    {
                        api.DestroyContext(nativeContext);
                    }
                    catch
                    {
                    }
                }
                api.Dispose();
                return Failure(
                    request,
                    status == CudaNativeStatus.AllocationFailed
                        ? CudaBackendFailure.AllocationFailed
                        : CudaBackendFailure.ContextCreationFailed,
                    $"CUDA context creation failed for device {request.DeviceIndex}: {detail}");
            }

            context = new CudaBackendContext(api, nativeContext, deviceInfo, request.BlockLength);
            status = context.InitializeCapabilities();
            if (status != CudaNativeStatus.Success)
            {
                string detail = context.GetLastError(status);
                context.Dispose();
                context = null;
                return Failure(
                    request,
                    CudaBackendFailure.DeviceUnsupported,
                    $"CUDA RF capabilities could not be queried on device {request.DeviceIndex}: {detail}");
            }

            if ((context.Capabilities & CudaBackendCapabilities.RfBatchTwoStage) == 0)
            {
                context.Dispose();
                context = null;
                return Failure(
                    request,
                    CudaBackendFailure.DeviceUnsupported,
                    $"CUDA component on device {request.DeviceIndex} does not support the RF batch graph.");
            }

            status = context.RunSelfTest(out CudaSelfTestMetrics selfTest);
            if (status != CudaNativeStatus.Success || !SelfTestPassed(selfTest))
            {
                string detail = status == CudaNativeStatus.Success
                    ? FormatSelfTestFailure(selfTest)
                    : NativeFailureDetail(api, nativeContext, status);
                context.Dispose();
                context = null;
                return Failure(
                    request,
                    status == CudaNativeStatus.AllocationFailed
                        ? CudaBackendFailure.AllocationFailed
                        : CudaBackendFailure.SelfTestFailed,
                    $"CUDA deterministic self-test failed on device {request.DeviceIndex}: {detail}");
            }

            return CudaBackendProbeResult.Ready(context, selfTest);
        }
        catch (Exception ex)
        {
            if (context is not null)
            {
                context.Dispose();
            }
            else
            {
                api.Dispose();
            }

            return Failure(
                request,
                CudaBackendFailure.UnexpectedFailure,
                $"CUDA initialization failed unexpectedly: {ex.Message}");
        }
    }

    private static bool SelfTestPassed(CudaSelfTestMetrics metrics) =>
        metrics.Passed &&
        metrics.NativeCudaStatus == CudaNativeStatus.Success &&
        metrics.SampleCount == CudaNativeAbi.SelfTestSampleCount &&
        double.IsFinite(metrics.MaximumAbsoluteError) &&
        double.IsFinite(metrics.Nrmse) &&
        metrics.MaximumAbsoluteError <= CudaNativeAbi.MaximumSelfTestAbsoluteError &&
        metrics.Nrmse <= CudaNativeAbi.MaximumSelfTestNrmse;

    private static string FormatSelfTestFailure(CudaSelfTestMetrics metrics) =>
        $"passed={metrics.Passed}, samples={metrics.SampleCount}, " +
        $"maxAbs={metrics.MaximumAbsoluteError:R}, nrmse={metrics.Nrmse:R}, " +
        $"cudaStatus={metrics.NativeCudaStatus}";

    private static string NativeFailureDetail(
        ICudaNativeApi api,
        nint context,
        CudaNativeStatus status)
    {
        string detail;
        try
        {
            detail = api.GetLastError(context);
        }
        catch
        {
            detail = string.Empty;
        }

        return string.IsNullOrWhiteSpace(detail) ? status.ToString() : $"{status}: {detail}";
    }

    private static CudaBackendProbeResult Failure(
        CudaBackendProbeRequest request,
        CudaBackendFailure failure,
        string message) =>
        request.Mode == CudaBackendRequestMode.Auto
            ? CudaBackendProbeResult.CpuFallback(failure, message)
            : CudaBackendProbeResult.Fatal(failure, message);
}

internal sealed class CudaBackendProbeResult : IDisposable
{
    private CudaBackendContext? _context;

    private CudaBackendProbeResult(
        CudaBackendProbeOutcome outcome,
        CudaBackendFailure failure,
        string message,
        CudaBackendContext? context,
        CudaSelfTestMetrics? selfTest)
    {
        Outcome = outcome;
        Failure = failure;
        Message = message;
        _context = context;
        SelfTest = selfTest;
    }

    internal CudaBackendProbeOutcome Outcome { get; }

    internal CudaBackendFailure Failure { get; }

    internal string Message { get; }

    internal CudaBackendContext? Context => Volatile.Read(ref _context);

    internal CudaSelfTestMetrics? SelfTest { get; }

    internal bool IsReady => Outcome == CudaBackendProbeOutcome.Ready;

    internal bool ShouldFallbackToCpu => Outcome == CudaBackendProbeOutcome.CpuFallback;

    internal bool IsFatal => Outcome == CudaBackendProbeOutcome.FatalFailure;

    /// <summary>
    /// Transfers ownership of a ready CUDA context to the RF backend.
    /// The probe result no longer disposes the context after this call.
    /// </summary>
    internal CudaBackendContext TakeContext()
    {
        if (!IsReady)
        {
            throw new InvalidOperationException("Only a ready CUDA probe result has a context.");
        }

        CudaBackendContext? context = Interlocked.Exchange(ref _context, null);
        return context ?? throw new InvalidOperationException("The CUDA context has already been transferred or disposed.");
    }

    internal static CudaBackendProbeResult Ready(
        CudaBackendContext context,
        CudaSelfTestMetrics selfTest) =>
        new(
            CudaBackendProbeOutcome.Ready,
            CudaBackendFailure.None,
            $"CUDA backend selected: device {context.Device.Ordinal} ({context.Device.Name}).",
            context,
            selfTest);

    internal static CudaBackendProbeResult CpuFallback(CudaBackendFailure failure, string message) =>
        new(CudaBackendProbeOutcome.CpuFallback, failure, message, null, null);

    internal static CudaBackendProbeResult Fatal(CudaBackendFailure failure, string message) =>
        new(CudaBackendProbeOutcome.FatalFailure, failure, message, null, null);

    public void Dispose() => Interlocked.Exchange(ref _context, null)?.Dispose();
}

internal sealed class CudaBackendContext : IDisposable
{
    private readonly ICudaNativeApi _api;
    private readonly CudaContextSafeHandle _handle;

    internal CudaBackendContext(
        ICudaNativeApi api,
        nint nativeHandle,
        CudaDeviceInfo device,
        int blockLength)
    {
        _api = api;
        _handle = new CudaContextSafeHandle(api, nativeHandle);
        Device = device;
        BlockLength = blockLength;
    }

    internal CudaDeviceInfo Device { get; }

    internal int BlockLength { get; }

    internal CudaBackendCapabilities Capabilities { get; private set; }

    internal bool IsDisposed => _handle.IsClosed;

    internal CudaNativeStatus RunSelfTest(out CudaSelfTestMetrics metrics)
    {
        bool addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                metrics = default;
                return CudaNativeStatus.InvalidArgument;
            }

            return _api.RunSelfTest(_handle.DangerousGetHandle(), out metrics);
        }
        finally
        {
            if (addedReference)
            {
                _handle.DangerousRelease();
            }
        }
    }

    internal CudaNativeStatus ExecuteRfBatch(CudaRfBatchJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Validate(BlockLength);

        bool addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                return CudaNativeStatus.InvalidArgument;
            }

            return _api.ExecuteRfBatch(_handle.DangerousGetHandle(), job);
        }
        finally
        {
            if (addedReference)
            {
                _handle.DangerousRelease();
            }
        }
    }

    internal CudaNativeStatus GetCurrentDeviceInfo(
        out CudaDeviceInfo deviceInfo,
        out string errorDetail)
    {
        bool addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                deviceInfo = Device;
                errorDetail = "The CUDA context has already been disposed.";
                return CudaNativeStatus.InvalidArgument;
            }

            CudaNativeStatus status = _api.GetDeviceInfo(Device.Ordinal, out deviceInfo);
            if (status == CudaNativeStatus.Success)
            {
                errorDetail = string.Empty;
                return status;
            }

            string nativeDetail = _api.GetLastError(nint.Zero);
            errorDetail = string.IsNullOrWhiteSpace(nativeDetail)
                ? status.ToString()
                : $"{status}: {nativeDetail}";
            deviceInfo = Device;
            return status;
        }
        finally
        {
            if (addedReference)
            {
                _handle.DangerousRelease();
            }
        }
    }

    internal string GetLastError(CudaNativeStatus status)
    {
        bool addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                return status.ToString();
            }

            string detail = _api.GetLastError(_handle.DangerousGetHandle());
            return string.IsNullOrWhiteSpace(detail) ? status.ToString() : $"{status}: {detail}";
        }
        catch
        {
            return status.ToString();
        }
        finally
        {
            if (addedReference)
            {
                _handle.DangerousRelease();
            }
        }
    }

    internal CudaNativeStatus InitializeCapabilities()
    {
        bool addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                return CudaNativeStatus.InvalidArgument;
            }

            CudaNativeStatus status = _api.GetCapabilities(
                _handle.DangerousGetHandle(),
                out CudaBackendCapabilities capabilities);
            if (status == CudaNativeStatus.Success)
            {
                Capabilities = capabilities;
            }

            return status;
        }
        finally
        {
            if (addedReference)
            {
                _handle.DangerousRelease();
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    private sealed class CudaContextSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly ICudaNativeApi _api;

        internal CudaContextSafeHandle(ICudaNativeApi api, nint nativeHandle)
            : base(ownsHandle: true)
        {
            _api = api;
            SetHandle(nativeHandle);
        }

        protected override bool ReleaseHandle()
        {
            bool released = true;
            try
            {
                _api.DestroyContext(handle);
            }
            catch
            {
                released = false;
            }
            finally
            {
                _api.Dispose();
            }

            return released;
        }
    }
}
