using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Compute.Cuda;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CudaBackendProbeTests
{
    [Fact(DisplayName = "Missing optional CUDA component falls back only in auto mode")]
    public void MissingOptionalCudaComponentFallsBackOnlyInAutoMode()
    {
        var loader = new StubLoader(CudaNativeApiLoadResult.Failed(
            CudaBackendFailure.ComponentMissing,
            "missing sidecar"));
        var probe = new CudaBackendProbe(loader);

        using CudaBackendProbeResult automatic = probe.Probe(CudaBackendProbeRequest.Auto());
        using CudaBackendProbeResult required = probe.Probe(CudaBackendProbeRequest.Required());

        Assert.True(automatic.ShouldFallbackToCpu);
        Assert.False(automatic.IsFatal);
        Assert.Equal(CudaBackendFailure.ComponentMissing, automatic.Failure);
        Assert.True(required.IsFatal);
        Assert.False(required.ShouldFallbackToCpu);
        Assert.Equal(CudaBackendFailure.ComponentMissing, required.Failure);
    }

    [Fact(DisplayName = "CUDA ABI mismatch is structured and disposes the loaded component")]
    public void CudaAbiMismatchIsStructuredAndDisposesLoadedComponent()
    {
        var api = new StubNativeApi { AbiVersion = CudaNativeAbi.Version + 1 };
        var probe = ProbeFor(api);

        using CudaBackendProbeResult result = probe.Probe(CudaBackendProbeRequest.Required());

        Assert.True(result.IsFatal);
        Assert.Equal(CudaBackendFailure.AbiMismatch, result.Failure);
        Assert.Contains("ABI", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, api.DisposeCount);
        Assert.Equal(0, api.DestroyCount);
    }

    [Fact(DisplayName = "No visible CUDA devices produces an auto CPU fallback")]
    public void NoVisibleCudaDevicesProducesAnAutoCpuFallback()
    {
        var api = new StubNativeApi { DeviceCount = 0 };
        var probe = ProbeFor(api);

        using CudaBackendProbeResult result = probe.Probe(CudaBackendProbeRequest.Auto());

        Assert.True(result.ShouldFallbackToCpu);
        Assert.Equal(CudaBackendFailure.NoDevices, result.Failure);
        Assert.Equal(1, api.DisposeCount);
        Assert.Equal(0, api.CreateCount);
    }

    [Fact(DisplayName = "Out-of-range CUDA device is rejected before context creation")]
    public void OutOfRangeCudaDeviceIsRejectedBeforeContextCreation()
    {
        var api = new StubNativeApi { DeviceCount = 1 };
        var probe = ProbeFor(api);

        using CudaBackendProbeResult result = probe.Probe(
            CudaBackendProbeRequest.Required(deviceIndex: 2));

        Assert.True(result.IsFatal);
        Assert.Equal(CudaBackendFailure.InvalidDeviceIndex, result.Failure);
        Assert.Contains("1 device(s)", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, api.CreateCount);
        Assert.Equal(1, api.DisposeCount);
    }

    [Fact(DisplayName = "CUDA context allocation failure is reported without leaking the API")]
    public void CudaContextAllocationFailureIsReportedWithoutLeakingApi()
    {
        var api = new StubNativeApi { CreateStatus = CudaNativeStatus.AllocationFailed };
        var probe = ProbeFor(api);

        using CudaBackendProbeResult result = probe.Probe(CudaBackendProbeRequest.Required());

        Assert.True(result.IsFatal);
        Assert.Equal(CudaBackendFailure.AllocationFailed, result.Failure);
        Assert.Equal(1, api.CreateCount);
        Assert.Equal(0, api.DestroyCount);
        Assert.Equal(1, api.DisposeCount);
    }

    [Fact(DisplayName = "CUDA self-test allocation failure destroys its native context")]
    public void CudaSelfTestAllocationFailureDestroysItsNativeContext()
    {
        var api = new StubNativeApi { SelfTestStatus = CudaNativeStatus.AllocationFailed };
        var probe = ProbeFor(api);

        using CudaBackendProbeResult result = probe.Probe(CudaBackendProbeRequest.Auto());

        Assert.True(result.ShouldFallbackToCpu);
        Assert.Equal(CudaBackendFailure.AllocationFailed, result.Failure);
        Assert.Equal(1, api.SelfTestCount);
        Assert.Equal(1, api.DestroyCount);
        Assert.Equal(1, api.DisposeCount);
    }

    [Fact(DisplayName = "CUDA self-test enforces managed sample and error tolerances")]
    public void CudaSelfTestEnforcesManagedSampleAndErrorTolerances()
    {
        var api = new StubNativeApi
        {
            SelfTestMetrics = PassingMetrics with
            {
                MaximumAbsoluteError = CudaNativeAbi.MaximumSelfTestAbsoluteError * 2
            }
        };
        var probe = ProbeFor(api);

        using CudaBackendProbeResult result = probe.Probe(CudaBackendProbeRequest.Required());

        Assert.True(result.IsFatal);
        Assert.Equal(CudaBackendFailure.SelfTestFailed, result.Failure);
        Assert.Contains("maxAbs", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, api.DestroyCount);
        Assert.Equal(1, api.DisposeCount);
    }

    [Fact(DisplayName = "Ready CUDA result owns one self-tested context until disposal")]
    public void ReadyCudaResultOwnsOneSelfTestedContextUntilDisposal()
    {
        var api = new StubNativeApi();
        var probe = ProbeFor(api);
        var diagnostics = new List<string>();
        CudaBackendProbeResult result = probe.Probe(
            CudaBackendProbeRequest.Required(
                blockLength: 20_000,
                diagnostic: diagnostics.Add));

        Assert.True(result.IsReady);
        Assert.NotNull(result.Context);
        CudaBackendContext context = result.Context!;
        Assert.Equal(20_000, context.BlockLength);
        Assert.Equal("Mock RTX", context.Device.Name);
        Assert.Equal(PassingMetrics, result.SelfTest);
        Assert.Single(diagnostics);
        Assert.Contains("CUDA backend selected", diagnostics[0], StringComparison.Ordinal);
        Assert.Equal(0, api.DestroyCount);
        Assert.Equal(0, api.DisposeCount);

        result.Dispose();
        result.Dispose();

        Assert.Null(result.Context);
        Assert.True(context.IsDisposed);
        Assert.Equal(1, api.DestroyCount);
        Assert.Equal(1, api.DisposeCount);
    }

    [Fact(DisplayName = "Managed CUDA ABI v1 structs retain their native sizes")]
    public void ManagedCudaAbiV1StructsRetainTheirNativeSizes()
    {
        Assert.Equal(328, NativeCudaApi.DeviceInfoStructSize);
        Assert.Equal(64, NativeCudaApi.SelfTestMetricsStructSize);
        Assert.Equal(232, NativeCudaApi.RfBatchJobStructSize);
        Assert.Equal(144, NativeCudaApi.LdFrequencyOptionsStructSize);
        Assert.Equal(16, Marshal.SizeOf<Complex>());
    }

    [Fact(DisplayName = "Single-file CUDA discovery checks beside decode.exe before its extraction directory")]
    public void SingleFileCudaDiscoveryChecksBesideExecutableFirst()
    {
        string executableDirectory = Path.Combine(Path.GetTempPath(), "cuda-published-app");
        string executablePath = Path.Combine(executableDirectory, "decode.exe");
        string extractionDirectory = Path.Combine(Path.GetTempPath(), ".net", "decode", "bundle-id");

        IReadOnlyList<string> directories = NativeCudaApiLoader.ResolveComponentDirectories(
            componentDirectory: null,
            processPath: executablePath,
            appBaseDirectory: extractionDirectory);

        Assert.Equal(
            [Path.GetFullPath(executableDirectory), Path.GetFullPath(extractionDirectory)],
            directories);
    }

    [Fact(DisplayName = "Ready CUDA context ownership can be transferred to the RF backend")]
    public void ReadyCudaContextOwnershipCanBeTransferredToTheRfBackend()
    {
        var api = new StubNativeApi();
        using CudaBackendProbeResult result = ProbeFor(api).Probe(CudaBackendProbeRequest.Required());

        using CudaBackendContext context = result.TakeContext();
        result.Dispose();

        Assert.Null(result.Context);
        Assert.False(context.IsDisposed);
        Assert.Equal(0, api.DestroyCount);
        Assert.Throws<InvalidOperationException>(() => result.TakeContext());

        context.Dispose();
        Assert.Equal(1, api.DestroyCount);
        Assert.Equal(1, api.DisposeCount);
    }

    [Fact(DisplayName = "CVBS RF batch does not require the standard analytic RF filter")]
    public void CvbsRfBatchDoesNotRequireTheStandardAnalyticRfFilter()
    {
        var cvbs = new CudaRfBatchJob
        {
            Flags = CudaRfBatchFlags.OutputVideo,
            Mode = CudaRfMode.Cvbs,
            SampleCount = 4,
            BatchCount = 1,
            DemodPhaseScale = 0,
            Input = new double[4],
            VideoOutput = new double[4]
        };
        cvbs.Validate(contextBlockLength: 4);

        var standard = new CudaRfBatchJob
        {
            Flags = CudaRfBatchFlags.OutputVideo,
            Mode = CudaRfMode.StandardConjugate,
            SampleCount = 4,
            BatchCount = 1,
            DemodPhaseScale = 1,
            Input = new double[4],
            DemodVideoFilter = new Complex[3],
            VideoOutput = new double[4]
        };
        Assert.Throws<ArgumentException>(() => standard.Validate(contextBlockLength: 4));
    }

    private static readonly CudaSelfTestMetrics PassingMetrics = new(
        Passed: true,
        MaximumAbsoluteError: 5e-10,
        Nrmse: 5e-12,
        SampleCount: CudaNativeAbi.SelfTestSampleCount,
        NativeCudaStatus: CudaNativeStatus.Success);

    private static CudaBackendProbe ProbeFor(StubNativeApi api) =>
        new(new StubLoader(CudaNativeApiLoadResult.Success(api)));

    private sealed class StubLoader(CudaNativeApiLoadResult result) : ICudaNativeApiLoader
    {
        public CudaNativeApiLoadResult Load(string? componentDirectory) => result;
    }

    private sealed class StubNativeApi : ICudaNativeApi
    {
        internal uint AbiVersion { get; init; } = CudaNativeAbi.Version;
        internal CudaNativeStatus DeviceCountStatus { get; init; } = CudaNativeStatus.Success;
        internal int DeviceCount { get; init; } = 1;
        internal CudaNativeStatus DeviceInfoStatus { get; init; } = CudaNativeStatus.Success;
        internal CudaDeviceInfo DeviceInfo { get; init; } = new(
            Ordinal: 0,
            Name: "Mock RTX",
            ComputeCapabilityMajor: 8,
            ComputeCapabilityMinor: 9,
            TotalGlobalMemoryBytes: 12UL * 1024 * 1024 * 1024,
            DriverVersion: 13_000,
            RuntimeVersion: 13_000,
            CufftVersion: 12_000,
            SupportsFp64: true,
            SupportsConcurrentCopyAndCompute: true);
        internal CudaNativeStatus CreateStatus { get; init; } = CudaNativeStatus.Success;
        internal nint Context { get; init; } = (nint)0x1234;
        internal CudaNativeStatus SelfTestStatus { get; init; } = CudaNativeStatus.Success;
        internal CudaSelfTestMetrics SelfTestMetrics { get; init; } = PassingMetrics;
        internal string LastError { get; init; } = "mock native failure";

        internal int CreateCount { get; private set; }
        internal int SelfTestCount { get; private set; }
        internal int DestroyCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public uint GetAbiVersion() => AbiVersion;

        public CudaNativeStatus GetDeviceCount(out int deviceCount)
        {
            deviceCount = DeviceCount;
            return DeviceCountStatus;
        }

        public CudaNativeStatus GetDeviceInfo(int deviceIndex, out CudaDeviceInfo deviceInfo)
        {
            deviceInfo = DeviceInfo with { Ordinal = deviceIndex };
            return DeviceInfoStatus;
        }

        public CudaNativeStatus CreateContext(int deviceIndex, out nint context)
        {
            CreateCount++;
            context = CreateStatus == CudaNativeStatus.Success ? Context : nint.Zero;
            return CreateStatus;
        }

        public void DestroyContext(nint context)
        {
            Assert.Equal(Context, context);
            DestroyCount++;
        }

        public CudaNativeStatus RunSelfTest(nint context, out CudaSelfTestMetrics metrics)
        {
            Assert.Equal(Context, context);
            SelfTestCount++;
            metrics = SelfTestMetrics;
            return SelfTestStatus;
        }

        public CudaNativeStatus GetCapabilities(
            nint context,
            out CudaBackendCapabilities capabilities)
        {
            Assert.Equal(Context, context);
            capabilities = CudaBackendCapabilities.RfBatchTwoStage |
                           CudaBackendCapabilities.RfHighPass |
                           CudaBackendCapabilities.RfMtf |
                           CudaBackendCapabilities.RfAnalytic |
                           CudaBackendCapabilities.RfEnvelope |
                           CudaBackendCapabilities.RfConjugateDemod |
                           CudaBackendCapabilities.RfVideo |
                           CudaBackendCapabilities.RfVideoLowPass |
                           CudaBackendCapabilities.RfModeStandard |
                           CudaBackendCapabilities.RfModeVhsRustApproximation |
                           CudaBackendCapabilities.RfModeCvbs |
                           CudaBackendCapabilities.VhsRustDemodKernel;
            return CudaNativeStatus.Success;
        }

        public CudaNativeStatus ExecuteRfBatch(nint context, CudaRfBatchJob job)
        {
            Assert.Equal(Context, context);
            return CudaNativeStatus.Success;
        }

        public string GetLastError(nint context) => LastError;

        public void Dispose() => DisposeCount++;
    }
}
