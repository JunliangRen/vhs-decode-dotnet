using System.Runtime.InteropServices;
using System.Text.Json;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp.CudaFast;
using VHSDecode.Core.HiFi;
using VHSDecode.Core.Rf;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CudaFastBackendTests
{
    [Fact(DisplayName = "CUDA-fast managed structures match native ABI v5")]
    public void ManagedStructuresMatchNativeAbi()
    {
        Assert.Equal(168, CudaFastNativeRuntime.RuntimeInfoStructureSize);
        Assert.Equal(80, CudaFastNativeRuntime.ConfigurationStructureSize);
        Assert.Equal(24, CudaFastNativeRuntime.ResultStructureSize);
        Assert.Equal(56, CudaFastNativeRuntime.PreviewConfigurationStructureSize);
        Assert.Equal(64, CudaFastNativeRuntime.PreviewWindowStructureSize);
        Assert.Equal(32, CudaFastNativeRuntime.PreviewResultStructureSize);
    }

    [Fact(DisplayName = "CUDA-fast staged native bridge loads with the pinned ABI")]
    public void StagedNativeBridgeLoadsWithPinnedAbi()
    {
        CudaFastRuntimeProvisioner provisioner = CudaFastRuntimeProvisioner.CreateProduction();
        Assert.SkipUnless(
            OperatingSystem.IsWindows()
                && CudaFastNativeRuntime.BuildCandidatePaths().Any(File.Exists)
                && CudaFastRuntimeProvisioner.BuildCandidatePaths(
                        CudaFastRuntimeProvisioner.CaptureSearchEnvironment(
                            provisioner.CacheLibraryPath))
                    .Any(File.Exists),
            "The optional CUDA-fast bridge or a local cuFFT runtime was not staged for this test build.");

        // Loading the bridge validates its exported ABI in the runtime constructor.
        // Do not require a physical CUDA device on hosted build runners for this ABI check.
        Assert.NotNull(CudaFastNativeRuntime.RequireAvailable());
    }

    [Fact(DisplayName = "CUDA-fast runtime diagnostic identifies the full GPU path")]
    public void RuntimeDiagnosticIdentifiesFullGpuPath()
    {
        string diagnostic = CudaFastDecodeRunner.FormatRuntimeDiagnostic(
            new CudaFastRuntimeInfo(
                CudaFastNativeRuntime.AbiVersion,
                0,
                8,
                9,
                46,
                12UL * 1024 * 1024 * 1024,
                8UL * 1024 * 1024 * 1024,
                "Test GPU"));

        Assert.Contains("CUDA-fast full DSP backend", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Test GPU", diagnostic, StringComparison.Ordinal);
        Assert.Contains("compute 8.9", diagnostic, StringComparison.Ordinal);
        Assert.Contains("12.0 GiB", diagnostic, StringComparison.Ordinal);
        Assert.Contains("0x00050000", diagnostic, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast native build pins the signal data plane to FP32")]
    public void NativeBuildPinsSignalDataPlaneToFp32()
    {
        string cmake = ReadNativeBuildDefinition();

        Assert.Contains(
            "function(vhsdecode_cuda_fast_convert_pipeline_to_fp32 variable_name)",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "string(REPLACE \"cufftDoubleComplex\" \"cufftComplex\"",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "string(REPLACE \"CUFFT_D2Z\" \"CUFFT_R2C\"",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "const std::vector<float> h_rf_fp32",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "cudaMalloc(&d_rf_filter, freq_bins * sizeof(float))",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "Keep only this one-time host-side design in",
            cmake,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast uploads native PCM16 and converts it on the GPU")]
    public void NativeBuildUsesDirectPcm16Upload()
    {
        string cmake = ReadNativeBuildDefinition();
        string geometry = ReadNativeSource("src", "cuda_fast_geometry.cu");
        string rawReader = ReadNativeSource("src", "raw_reader.cpp");

        Assert.Contains("d_raw_s16", cmake, StringComparison.Ordinal);
        Assert.Contains("reader.callback_returns_int16()", cmake, StringComparison.Ordinal);
        Assert.Contains("cuda_fast_convert_s16_to_float", cmake, StringComparison.Ordinal);
        Assert.Contains("convert_s16_to_float<<<", geometry, StringComparison.Ordinal);
        Assert.Contains("RawReaderCallbackFormat::Int16", rawReader, StringComparison.Ordinal);
        Assert.Contains("read_raw_at", rawReader, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA preview keeps the 40-to-20 MSPS and NVENC data plane on the GPU")]
    public void NativePreviewKeepsDownsampledFramesOnTheGpu()
    {
        string cmake = ReadNativeBuildDefinition();
        string normalizedCmake = cmake.Replace("\r\n", "\n", StringComparison.Ordinal);
        string cancellation = ReadNativeSource("src", "cancellation_latch.h");
        string cancellationTest = ReadNativeSource("tests", "cancellation_latch_test.cpp");
        string decimator = ReadNativeSource("src", "cuda_fast_decimator.cu");
        string output = ReadNativeSource("src", "cuda_preview_output.cu");
        string writer = ReadNativeSource("overlay", "io", "tbc_writer.h");

        Assert.Contains("reader.device_decimation_factor() == 2", cmake, StringComparison.Ordinal);
        Assert.Contains("cuda_fast_read_upload_half_rate(", cmake, StringComparison.Ordinal);
        Assert.Contains("cuda_fast_read_half_rate_s16(", cmake, StringComparison.Ordinal);
        Assert.Contains("cuda_fast_upload_half_rate_s16(", cmake, StringComparison.Ordinal);
        Assert.Contains("prefetch_half_rate_read_ok", cmake, StringComparison.Ordinal);
        Assert.Contains("CUVHS_DISABLE_RF_PREFETCH", cmake, StringComparison.Ordinal);
        Assert.Contains("constexpr int kHalfWidth = 15;", decimator, StringComparison.Ordinal);
        Assert.Contains("0.5000046374907835f", decimator, StringComparison.Ordinal);
        Assert.Contains("source_buffer_count * sizeof(int16_t)", decimator, StringComparison.Ordinal);
        Assert.Contains("cudaMemcpyHostToDevice", decimator, StringComparison.Ordinal);
        Assert.Contains("NV_ENC_INPUT_RESOURCE_TYPE_CUDADEVICEPTR", output, StringComparison.Ordinal);
        Assert.Contains("registration.resourceToRegister = d_nv12;", output, StringComparison.Ordinal);
        Assert.Contains("NV_ENC_BUFFER_FORMAT_NV12", output, StringComparison.Ordinal);
        Assert.DoesNotContain("cudaMemcpyDeviceToHost", output, StringComparison.Ordinal);
        Assert.Contains("accepts_device_fields()", writer, StringComparison.Ordinal);
        Assert.Contains("std::atomic_bool requested_{false};", cancellation, StringComparison.Ordinal);
        Assert.Contains("std::memory_order_acquire", cancellation, StringComparison.Ordinal);
        Assert.Contains("std::memory_order_release", cancellation, StringComparison.Ordinal);
        Assert.Contains(
            "Parallel preview/prefetch cancellation latch test passed.",
            cancellationTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "vhsdecode_cuda_fast_cancellation_test",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "[=[    if (!writer.accepts_device_fields()) {\n"
                + "        // Progress display",
            normalizedCmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "[=[        if (!writer.accepts_device_fields()) {\n"
                + "        // Progress dashboard",
            normalizedCmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "[=[    if (!writer.accepts_device_fields()) {\n"
                + "    // Final summary",
            normalizedCmake,
            StringComparison.Ordinal);

        int directOutput = cmake.IndexOf(
            "writer.write_preview_device_fields(",
            StringComparison.Ordinal);
        int regularDownload = cmake.IndexOf(
            "// Download TBC results + dropout metadata and write to disk]=]",
            directOutput,
            StringComparison.Ordinal);
        Assert.True(directOutput >= 0 && regularDownload > directOutput);
    }

    [Fact(DisplayName = "CUDA preview uses fast container seeking without changing full decode")]
    public void PreviewInputLoaderUsesFastContainerSeekingOnlyWhenRequested()
    {
        using var full = Assert.IsType<CudaFastDecodeRunner.FfmpegPcm16InputAdapter>(
            CudaFastDecodeRunner.CreateInputLoader("capture.ldf"));
        using var preview = Assert.IsType<CudaFastDecodeRunner.FfmpegPcm16InputAdapter>(
            CudaFastDecodeRunner.CreateInputLoader(
                "capture.ldf",
                fastContainerSeeking: true));

        Assert.False(full.FastInputSeek);
        Assert.True(preview.FastInputSeek);
    }

    [Fact(DisplayName = "CUDA-fast reuses and releases its persistent chroma workspace")]
    public void NativeBuildReusesAndReleasesPersistentChromaWorkspace()
    {
        string cmake = ReadNativeBuildDefinition();
        string nativeBuildScript = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "tools", "build-cuda-fast-native.ps1"));

        Assert.Contains("struct CudaFastChromaWorkspace", cmake, StringComparison.Ordinal);
        Assert.Contains("ensure_chroma_workspace(", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "CUVHS_DISABLE_PERSISTENT_CHROMA_WORKSPACE",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("chroma_state_release(&chroma_state);", cmake, StringComparison.Ordinal);
        Assert.Contains("bool chroma_decode(", cmake, StringComparison.Ordinal);
        Assert.Contains("if (!chroma_decode(", cmake, StringComparison.Ordinal);
        string normalizedCmake = cmake.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "CUDA-fast chroma decode failed\\n\");\n"
                + "        delete[] h_k3_debug;\n"
                + "        if (d_k3_debug) cudaFree(d_k3_debug);\n"
                + "        cudaFree(d_field_offsets);",
            normalizedCmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "CUVHS_FORCE_CHROMA_WORKSPACE_FAILURE",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "CUVHS_FORCE_CHROMA_WORKSPACE_FAILURE",
            nativeBuildScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "CUDA-fast accepted a forced chroma-workspace failure.",
            nativeBuildScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!persistent_workspace) local_workspace.release();",
            cmake,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast caps automatic FP32 batches while preserving diagnostics override")]
    public void NativeBuildCapsAutomaticFp32BatchSize()
    {
        string cmake = ReadNativeBuildDefinition();

        Assert.Contains("if (batch_override <= 0)", cmake, StringComparison.Ordinal);
        Assert.Contains("batch_size = std::min(batch_size, 16);", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "batch_size = batch_override > 0 ? batch_override",
            cmake,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast seeds and carries PAL head-track phase from field parity")]
    public void NativeBuildSeedsAndCarriesPalHeadTrackPhaseFromFieldParity()
    {
        string cmake = ReadNativeBuildDefinition();

        Assert.Contains(
            "VideoProfile::PAL_625_50_VHS) phase_mode = 3",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "const int cadence_track = (f & 1) ? (1 - current_track) : current_track;",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "h_track[f] = parity == 0 ? 1 : parity == 1 ? 0 : cadence_track;",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "state->current_track = (num_fields & 1) ? (1 - current_track) : current_track;",
            cmake,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast preserves detected field parity and drops an incomplete leading frame")]
    public void NativeBuildPreservesDetectedFieldParityAtOutputStart()
    {
        string cmake = ReadNativeBuildDefinition();

        Assert.Contains("bool output_have_parity = false;", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "(!output_have_parity && h_is_first[i] == 0)",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains(
            "const bool has_usable_hsync_lattice = usable_hsync_count",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("k2b_startup_hsync_streak >= 2", cmake, StringComparison.Ordinal);
        Assert.Contains("(seed_hsync_cadence ? 0 : 1)", cmake, StringComparison.Ordinal);
        Assert.Contains("h_out_is_first_k2b[field] = -1;", cmake, StringComparison.Ordinal);
        Assert.Contains("output_have_parity = true;", cmake, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast scans past invalid leader until the requested valid field count")]
    public void NativeAndManagedPathsBoundOutputInsteadOfInputSearch()
    {
        string cmake = ReadNativeBuildDefinition();
        string header = ReadNativeSource("include", "vhsdecode_cuda_fast.h");
        string runner = File.ReadAllText(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "VHSDecode.Core",
                "Dsp",
                "CudaFast",
                "CudaFastDecodeRunner.cs"));

        Assert.Contains("uint32_t maximum_output_fields;", header, StringComparison.Ordinal);
        Assert.Contains("writer.fields_written() >= maximum_output_fields", cmake, StringComparison.Ordinal);
        Assert.Contains("target %u output fields", cmake, StringComparison.Ordinal);
        Assert.Contains("awaiting stable sync", cmake, StringComparison.Ordinal);
        Assert.Contains("%zu output fields (%d scanned)", cmake, StringComparison.Ordinal);
        Assert.Contains("return availableSamples;", runner, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast TBC metadata includes the default VHS headroom")]
    public void NativeWriterIncludesDefaultVhsLevelAdjustment()
    {
        string writer = ReadNativeSource("src", "tbc_writer.cpp");

        Assert.Contains("constexpr double default_level_adjust = 0.1;", writer, StringComparison.Ordinal);
        Assert.Contains("* (1.0 - default_level_adjust)", writer, StringComparison.Ordinal);
        Assert.Contains("* (1.0 + default_level_adjust)", writer, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast VSYNC diagnostics tolerate an empty restored lattice")]
    public void NativeBuildSafelyLogsAnEmptyRestoredVsyncLattice()
    {
        string cmake = ReadNativeBuildDefinition();

        Assert.Contains(
            "dense_offsets.empty() ? size_t{0} : dense_offsets.front()",
            cmake,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast parallel pulse scan preserves deterministic overflow behavior")]
    public void NativeBuildUsesDeterministicParallelPulseScan()
    {
        string cmake = ReadNativeBuildDefinition();
        string source = ReadNativeSource("overlay", "pipeline", "sync_pulses.cu");
        string nativeTest = ReadNativeSource("tests", "sync_pulses_test.cu");
        string ntscTest = ReadNativeSource("tests", "synthetic_ntsc_test.cpp");
        string nativeBuildScript = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "tools", "build-cuda-fast-native.ps1"));

        Assert.Contains(
            "overlay/pipeline/sync_pulses.cu",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("find_pulse_edges", source, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(pulse_count + field, 1)", source, StringComparison.Ordinal);
        Assert.Contains("order_pulses", source, StringComparison.Ordinal);
        Assert.Contains("find_pulses_sequential", source, StringComparison.Ordinal);
        Assert.Contains("detected_count > max_pulses", source, StringComparison.Ordinal);
        Assert.Contains("verify_regular_and_boundary_pulses", nativeTest, StringComparison.Ordinal);
        Assert.Contains("verify_overflow_fallback", nativeTest, StringComparison.Ordinal);
        Assert.Contains("make_synthetic_ntsc_rf", ntscTest, StringComparison.Ordinal);
        Assert.Contains("files_equal", ntscTest, StringComparison.Ordinal);
        Assert.Contains("constexpr int kFieldCount = 48", ntscTest, StringComparison.Ordinal);
        Assert.Contains("metadata_sequence_valid", ntscTest, StringComparison.Ordinal);
        Assert.Contains("{1, 4, 3, 2}", ntscTest, StringComparison.Ordinal);
        Assert.Contains("VHSDECODE_CUDA_FAST_PROFILE_NTSC", ntscTest, StringComparison.Ordinal);
        Assert.Contains("'CUVHS_BATCH_SIZE'", nativeBuildScript, StringComparison.Ordinal);
        Assert.Contains("'5'", nativeBuildScript, StringComparison.Ordinal);
        Assert.Contains("output_last_parity", cmake, StringComparison.Ordinal);
        Assert.Contains("per-decode writer cadence state", cmake, StringComparison.Ordinal);
        Assert.Contains("CUVHS_STATIC_WRITER_PARITY", cmake, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast optimized native path preserves deterministic output contracts")]
    public void NativeOptimizationsPreserveDeterministicOutputContracts()
    {
        string cmake = ReadNativeBuildDefinition();
        string header = ReadNativeSource("overlay", "pipeline", "dropout_detect.h");
        string source = ReadNativeSource("overlay", "pipeline", "dropout_detect.cu");
        string writer = ReadNativeSource("src", "tbc_writer.cpp");
        string nativeTest = ReadNativeSource("tests", "dropout_detect_test.cu");
        string nativeBuildScript = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "tools", "build-cuda-fast-native.ps1"));

        Assert.Contains("configure_file(", cmake, StringComparison.Ordinal);
        Assert.Contains("overlay/pipeline/dropout_detect.cu", cmake, StringComparison.Ordinal);
        Assert.Contains("d_field_offsets", header, StringComparison.Ordinal);
        Assert.Contains("field_window_samples", header, StringComparison.Ordinal);
        Assert.Contains("constexpr int kMergeThreshold = 30;", source, StringComparison.Ordinal);
        Assert.Contains("constexpr int kMinimumLength = 10;", source, StringComparison.Ordinal);
        Assert.Contains("dropout_end - dropout_start > kMinimumLength", source, StringComparison.Ordinal);
        Assert.Contains("constexpr int kEventMaskSamples = 32;", source, StringComparison.Ordinal);
        Assert.Contains("classify_dropout_events", source, StringComparison.Ordinal);
        Assert.Contains("__ballot_sync(active, is_down)", source, StringComparison.Ordinal);
        Assert.Contains("__ballot_sync(active, is_up)", source, StringComparison.Ordinal);
        Assert.Contains("map_dropout_range(", source, StringComparison.Ordinal);
        Assert.Contains("k_compute_field_mean_parallel", cmake, StringComparison.Ordinal);
        Assert.Contains("CUVHS_FORCE_SERIAL_BURST_DC", cmake, StringComparison.Ordinal);
        Assert.Contains("start_raw_prefetch(0, initial_prefetch_fields);", cmake, StringComparison.Ordinal);
        Assert.Contains("CUVHS_DISABLE_RF_PREFETCH", cmake, StringComparison.Ordinal);
        Assert.Contains("prefetch_num_fields >= num_fields", cmake, StringComparison.Ordinal);
        Assert.Contains("CUVHS_FORCE_JSON_EVERY_CHUNK", cmake, StringComparison.Ordinal);
        Assert.Contains("std::chrono::seconds(1)", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "stop_raw_prefetch();\n    return writer.finalize();",
            cmake.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("constexpr size_t kOutputBufferBytes = 4U * 1024U * 1024U;", writer, StringComparison.Ordinal);
        Assert.Contains("std::setvbuf(luma_fp, nullptr, _IOFBF, kOutputBufferBytes);", writer, StringComparison.Ordinal);
        Assert.Contains("std::setvbuf(chroma_fp, nullptr, _IOFBF, kOutputBufferBytes);", writer, StringComparison.Ordinal);
        Assert.Contains("verify_exact_pal_mapping_with_dynamic_offsets", nativeTest, StringComparison.Ordinal);
        Assert.Contains("const int expected_counts[] = {4, 2};", nativeTest, StringComparison.Ordinal);
        Assert.Contains("$dropoutTestName", nativeBuildScript, StringComparison.Ordinal);
        Assert.Contains("$dropoutTestOutputPath", nativeBuildScript, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipRuntimeTests", nativeBuildScript, StringComparison.Ordinal);
        Assert.Contains("if (-not $SkipRuntimeTests)", nativeBuildScript, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast TBC applies the measured Exact-aligned luma phase")]
    public void NativeTbcUsesMeasuredExactAlignedLumaPhase()
    {
        string cmake = ReadNativeBuildDefinition();

        Assert.Contains("CUVHS_TBC_SAMPLE_PHASE", cmake, StringComparison.Ordinal);
        Assert.Contains("Match the Exact path's observed fractional luma phase", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "coord += 0.375f * line_len / (float)output_line_len;",
            cmake,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "CUDA-fast rejects explicit options it cannot honor")]
    public void ExplicitUnsupportedOptionIsRejected()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.u8");
            File.WriteAllBytes(inputPath, new byte[16]);
            ParsedCommand command = Parse(
                inputPath,
                Path.Combine(directory, "output"),
                "--no_resample",
                "--sharpness",
                "1");

            NotSupportedException exception = Assert.Throws<NotSupportedException>(
                () => CudaFastDecodeRunner.ValidateCommand(command));

            Assert.Contains("--sl/--sharpness", exception.Message, StringComparison.Ordinal);
            Assert.Contains("was not ignored", exception.Message, StringComparison.Ordinal);
            Assert.Contains("no CPU fallback", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast rejects an explicit CPU compatibility profile")]
    public void ExplicitCompatibilityProfileIsRejected()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.u8");
            File.WriteAllBytes(inputPath, new byte[16]);
            ParsedCommand command = Parse(
                inputPath,
                Path.Combine(directory, "output"),
                "--no_resample",
                "--compat-version",
                "current");

            NotSupportedException exception = Assert.Throws<NotSupportedException>(
                () => CudaFastDecodeRunner.ValidateCommand(command));

            Assert.Contains("--compat-version", exception.Message, StringComparison.Ordinal);
            Assert.Contains("was not ignored", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast rejects resampled RF input rates")]
    public void ResampledInputRateIsRejected()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.u8");
            File.WriteAllBytes(inputPath, new byte[16]);
            ParsedCommand command = Parse(
                inputPath,
                Path.Combine(directory, "output"),
                "--frequency",
                "28");

            NotSupportedException exception = Assert.Throws<NotSupportedException>(
                () => CudaFastDecodeRunner.ValidateCommand(command));

            Assert.Contains("native-rate 40 MSPS", exception.Message, StringComparison.Ordinal);
            Assert.Contains("28", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast probes unpacked raw sample counts without decoding")]
    public void UnpackedRawSampleCountUsesContainerWidth()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string s16Path = Path.Combine(directory, "input.s16");
            string u8Path = Path.Combine(directory, "input.u8");
            string packedPath = Path.Combine(directory, "input.lds");
            File.WriteAllBytes(s16Path, new byte[18]);
            File.WriteAllBytes(u8Path, new byte[19]);
            File.WriteAllBytes(packedPath, new byte[20]);

            Assert.True(CudaFastDecodeRunner.TryGetInputSampleCount(s16Path, out long s16Samples));
            Assert.Equal(9, s16Samples);
            Assert.True(CudaFastDecodeRunner.TryGetInputSampleCount(u8Path, out long u8Samples));
            Assert.Equal(19, u8Samples);
            Assert.False(CudaFastDecodeRunner.TryGetInputSampleCount(packedPath, out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast finalization rebases fileLoc and trims postroll fields")]
    public void FinalizationRebasesAndTrimsOutputs()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string outputBase = Path.Combine(directory, "output");
            File.WriteAllBytes(outputBase + ".tbc", new byte[240]);
            File.WriteAllBytes(outputBase + "_chroma.tbc", new byte[240]);
            WriteMetadata(outputBase, 6, fieldWidth: 10, fieldHeight: 2);

            CudaFastDecodeRunner.CudaFastOutputSummary summary =
                CudaFastDecodeRunner.FinalizeOutputs(
                    outputBase,
                    sourceStart: 100,
                    requestedFields: 4,
                    new CudaFastNativeResult(
                        FieldsWritten: 6,
                        OutputLineLength: 10,
                        OutputFieldLines: 2,
                        ElapsedSeconds: 0.25));

            Assert.Equal(4, summary.WrittenFields);
            Assert.Equal(160, new FileInfo(outputBase + ".tbc").Length);
            Assert.Equal(160, new FileInfo(outputBase + "_chroma.tbc").Length);
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(outputBase + ".tbc.json"));
            JsonElement root = document.RootElement;
            Assert.Equal(
                4,
                root.GetProperty("videoParameters")
                    .GetProperty("numberOfSequentialFields")
                    .GetInt32());
            Assert.Equal(
                "cuda-fast",
                root.GetProperty("videoParameters").GetProperty("dspBackend").GetString());
            JsonElement fields = root.GetProperty("fields");
            Assert.Equal(4, fields.GetArrayLength());
            Assert.Equal(110, fields[0].GetProperty("fileLoc").GetInt64());
            Assert.Equal(4, fields[3].GetProperty("seqNo").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast finalization rejects output shorter than metadata")]
    public void FinalizationRejectsShortOutput()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string outputBase = Path.Combine(directory, "output");
            File.WriteAllBytes(outputBase + ".tbc", new byte[20]);
            File.WriteAllBytes(outputBase + "_chroma.tbc", new byte[20]);
            WriteMetadata(outputBase, 2, fieldWidth: 10, fieldHeight: 2);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => CudaFastDecodeRunner.FinalizeOutputs(
                    outputBase,
                    sourceStart: 0,
                    requestedFields: 2,
                    new CudaFastNativeResult(
                        FieldsWritten: 2,
                        OutputLineLength: 10,
                        OutputFieldLines: 2,
                        ElapsedSeconds: 0.1)));

            Assert.Contains("shorter than its metadata contract", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast runner feeds managed RF samples to the native full path")]
    public void RunnerFeedsManagedSamplesToNativePath()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.U8");
            string outputBase = Path.Combine(directory, "output");
            File.WriteAllBytes(inputPath, Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
            ParsedCommand command = Parse(
                inputPath,
                outputBase,
                "--no_resample",
                "--length",
                "1",
                "--overwrite");
            var nativeRuntime = new RecordingNativeRuntime();
            var runner = new CudaFastDecodeRunner(() => nativeRuntime);

            TbcFieldSequenceDecodeResult result = runner.TryDecodeAndWrite(
                command,
                TextWriter.Null,
                CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.WrittenFieldCount);
            Assert.Equal(1, nativeRuntime.RunCount);
            Assert.Equal(16, nativeRuntime.ReadSampleCount);
            Assert.Equal(40.0, nativeRuntime.SampleRateMhz);
            Assert.Equal(CudaFastProfile.Ntsc, nativeRuntime.Profile);
            Assert.Equal(CudaFastInputSampleFormat.Float32, nativeRuntime.InputSampleFormat);
            Assert.Equal(
                Enumerable.Range(0, 16).Select(value => (float)value).ToArray(),
                nativeRuntime.Float32Samples);
            Assert.Equal(64UL, nativeRuntime.TotalSamples);
            Assert.Equal(2U, nativeRuntime.MaximumOutputFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast raw PCM16 input uses the native-width callback")]
    public void RunnerFeedsPcm16SamplesWithoutCpuFloatConversion()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.s16");
            string outputBase = Path.Combine(directory, "output");
            short[] samples = Enumerable.Range(0, 64)
                .Select(value => unchecked((short)(value * 997 - 30_000)))
                .ToArray();
            var bytes = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(inputPath, bytes);
            ParsedCommand command = Parse(
                inputPath,
                outputBase,
                "--no_resample",
                "--length",
                "1",
                "--overwrite");
            var nativeRuntime = new RecordingNativeRuntime();
            var runner = new CudaFastDecodeRunner(() => nativeRuntime);

            TbcFieldSequenceDecodeResult result = runner.TryDecodeAndWrite(
                command,
                TextWriter.Null,
                CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Equal(CudaFastInputSampleFormat.Int16, nativeRuntime.InputSampleFormat);
            Assert.Equal(16, nativeRuntime.ReadSampleCount);
            Assert.Equal(samples.AsSpan(0, 16).ToArray(), nativeRuntime.Int16Samples);

            var fallbackLoader = new FallbackOnlyInt16Loader(
                [short.MinValue, -1.0, 0.0, short.MaxValue]);
            var fallbackDestination = new short[4];
            int fallbackRead = CudaFastDecodeRunner.ReadInt16WithFallback(
                fallbackLoader,
                Stream.Null,
                sample: 0,
                fallbackDestination);
            Assert.Equal(4, fallbackRead);
            Assert.Equal(
                [short.MinValue, (short)-1, (short)0, short.MaxValue],
                fallbackDestination);
            Assert.Equal(1, fallbackLoader.DirectReadCount);
            Assert.Equal(1, fallbackLoader.FallbackReadCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast unavailable runtime fails without CPU fallback")]
    public void UnavailableRuntimeFailsWithoutFallback()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.u8");
            string outputBase = Path.Combine(directory, "output");
            File.WriteAllBytes(inputPath, new byte[64]);
            ParsedCommand command = Parse(inputPath, outputBase, "--no_resample");
            var runner = new CudaFastDecodeRunner(
                () => throw new CudaFastBackendUnavailableException("test runtime missing"));

            TbcFieldSequenceDecodeResult result = runner.TryDecodeAndWrite(
                command,
                TextWriter.Null,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("explicit 'cuda-fast'", result.Message, StringComparison.Ordinal);
            Assert.Contains("test runtime missing", result.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outputBase + ".tbc"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA-fast forwards cancellation through the native callback")]
    public void CancellationIsForwardedToNativeCallback()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.u8");
            File.WriteAllBytes(inputPath, new byte[64]);
            ParsedCommand command = Parse(
                inputPath,
                Path.Combine(directory, "output"),
                "--no_resample");
            using var cancellation = new CancellationTokenSource();
            var runner = new CudaFastDecodeRunner(
                () => new CancellingNativeRuntime(cancellation));

            Assert.Throws<OperationCanceledException>(
                () => runner.TryDecodeAndWrite(
                    command,
                    TextWriter.Null,
                    cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "DecodeRunner routes cuda-fast without constructing the CPU field engine")]
    public void DecodeRunnerUsesIndependentCudaPath()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.u8");
            string outputBase = Path.Combine(directory, "output");
            File.WriteAllBytes(inputPath, new byte[64]);
            ParsedCommand command = Parse(inputPath, outputBase, "--no_resample");
            var cudaRunner = new RecordingDecodeRunner();
            int engineFactoryCalls = 0;
            var runner = new DecodeRunner(
                cancellationToken =>
                {
                    engineFactoryCalls++;
                    return new TbcFieldSequenceDecodeEngine(cancellationToken: cancellationToken);
                },
                new HiFiDecodeRunner(),
                cudaRunner);

            int exitCode = runner.Run(
                command,
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(1, cudaRunner.CallCount);
            Assert.Equal(0, engineFactoryCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA preview reuses its native session and streams compressed packets")]
    public void PreviewSessionReusesNativeContextAndStreamsPackets()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.s16");
            short[] source = [10, 20, 30, 40, 50, 60, 70, 80];
            byte[] input = new byte[source.Length * sizeof(short)];
            Buffer.BlockCopy(source, 0, input, 0, input.Length);
            File.WriteAllBytes(inputPath, input);
            IRfSampleLoader loader = CudaFastDecodeRunner.CreateInputLoader(inputPath);
            var native = new RecordingPreviewNativeSession();
            var runtimeInfo = new CudaFastRuntimeInfo(
                CudaFastNativeRuntime.AbiVersion,
                0,
                8,
                9,
                46,
                12UL * 1024 * 1024 * 1024,
                8UL * 1024 * 1024 * 1024,
                "Test GPU");

            using (var session = new CudaFastPreviewDecodeSession(
                inputPath,
                source.Length,
                loader,
                native,
                runtimeInfo))
            {
                using var firstOutput = new MemoryStream();
                using var secondOutput = new MemoryStream();
                CudaFastPreviewNativeResult first = session.DecodeWindow(
                    targetSourceSample: 2,
                    requestedOutputFrames: 4,
                    firstOutput,
                    CancellationToken.None);
                CudaFastPreviewNativeResult second = session.DecodeWindow(
                    targetSourceSample: 4,
                    requestedOutputFrames: 4,
                    secondOutput,
                    CancellationToken.None);

                Assert.Equal(4U, first.FramesEncoded);
                Assert.Equal(4U, second.FramesEncoded);
                Assert.Equal([2UL, 4UL], native.TargetSamples);
                Assert.Equal([30, 40, 50], native.ReadSamples[0]);
                Assert.Equal([50, 60, 70], native.ReadSamples[1]);
                Assert.Equal([0, 0, 1, 1], firstOutput.ToArray());
                Assert.Equal([0, 0, 1, 2], secondOutput.ToArray());
                Assert.All(
                    native.InputFormats,
                    format => Assert.Equal(CudaFastInputSampleFormat.Int16, format));
                Assert.Same(runtimeInfo, session.RuntimeInfo);
            }

            Assert.Equal(2, native.CallCount);
            Assert.True(native.Disposed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(DisplayName = "CUDA preview cancellation stops before entering the persistent native session")]
    public void PreviewSessionHonorsCancellationBeforeNativeDecode()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string inputPath = Path.Combine(directory, "input.s16");
            File.WriteAllBytes(inputPath, new byte[32]);
            IRfSampleLoader loader = CudaFastDecodeRunner.CreateInputLoader(inputPath);
            var native = new RecordingPreviewNativeSession();
            using var session = new CudaFastPreviewDecodeSession(
                inputPath,
                totalSourceSamples: 16,
                loader,
                native,
                new CudaFastRuntimeInfo(
                    CudaFastNativeRuntime.AbiVersion,
                    0,
                    8,
                    9,
                    46,
                    12UL * 1024 * 1024 * 1024,
                    8UL * 1024 * 1024 * 1024,
                    "Test GPU"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => session.DecodeWindow(
                targetSourceSample: 0,
                requestedOutputFrames: 4,
                Stream.Null,
                cancellation.Token));
            Assert.Equal(0, native.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ParsedCommand Parse(
        string inputPath,
        string outputBase,
        params string[] options)
    {
        string[] arguments =
        [
            "--dsp-backend",
            "cuda-fast",
            .. options,
            inputPath,
            outputBase
        ];
        return new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vhsdecode-cuda-fast-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ReadNativeBuildDefinition()
        => File.ReadAllText(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "VHSDecode.CudaFast.Native",
                "CMakeLists.txt"));

    private static string ReadNativeSource(params string[] relativePath)
        => File.ReadAllText(
            Path.Combine(
                [
                    RepositoryRoot(),
                    "src",
                    "VHSDecode.CudaFast.Native",
                    .. relativePath
                ]));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "VHSDecodeDotNet.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void WriteMetadata(
        string outputBase,
        int fieldCount,
        int fieldWidth = 1,
        int fieldHeight = 1)
    {
        object metadata = new
        {
            videoParameters = new
            {
                numberOfSequentialFields = fieldCount,
                fieldWidth,
                fieldHeight
            },
            fields = Enumerable.Range(1, fieldCount).Select(
                index => new
                {
                    isFirstField = index % 2 == 1,
                    seqNo = index,
                    fileLoc = index * 10,
                    dropOuts = new
                    {
                        fieldLine = Array.Empty<int>(),
                        startx = Array.Empty<int>(),
                        endx = Array.Empty<int>()
                    }
                })
        };
        File.WriteAllText(
            outputBase + ".tbc.json",
            JsonSerializer.Serialize(metadata));
    }

    private sealed class RecordingNativeRuntime : ICudaFastNativeRuntime
    {
        internal int RunCount { get; private set; }

        internal int ReadSampleCount { get; private set; }

        internal double SampleRateMhz { get; private set; }

        internal CudaFastProfile Profile { get; private set; }

        internal CudaFastInputSampleFormat InputSampleFormat { get; private set; }

        internal ulong TotalSamples { get; private set; }

        internal uint MaximumOutputFields { get; private set; }

        internal short[] Int16Samples { get; private set; } = [];

        internal float[] Float32Samples { get; private set; } = [];

        public CudaFastRuntimeInfo GetRuntimeInfo(int deviceId = 0)
            => new(
                CudaFastNativeRuntime.AbiVersion,
                deviceId,
                8,
                9,
                46,
                12UL * 1024 * 1024 * 1024,
                8UL * 1024 * 1024 * 1024,
                "Test GPU");

        public CudaFastNativeResult Run(CudaFastNativeRunConfiguration configuration)
        {
            RunCount++;
            SampleRateMhz = configuration.SampleRateMhz;
            Profile = configuration.Profile;
            InputSampleFormat = configuration.InputSampleFormat;
            TotalSamples = configuration.TotalSamples;
            MaximumOutputFields = configuration.MaximumOutputFields;
            int bytesPerSample = configuration.InputSampleFormat == CudaFastInputSampleFormat.Int16
                ? sizeof(short)
                : sizeof(float);
            nint buffer = Marshal.AllocHGlobal(16 * bytesPerSample);
            try
            {
                ReadSampleCount = checked((int)configuration.ReadCallback(
                    configuration.UserData,
                    buffer,
                    sampleOffset: 0,
                    sampleCount: 16));
                if (configuration.InputSampleFormat == CudaFastInputSampleFormat.Int16)
                {
                    Int16Samples = new short[ReadSampleCount];
                    Marshal.Copy(buffer, Int16Samples, 0, ReadSampleCount);
                }
                else
                {
                    Float32Samples = new float[ReadSampleCount];
                    Marshal.Copy(buffer, Float32Samples, 0, ReadSampleCount);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            File.WriteAllBytes(configuration.OutputBase + ".tbc", new byte[4]);
            File.WriteAllBytes(configuration.OutputBase + "_chroma.tbc", new byte[4]);
            WriteMetadata(configuration.OutputBase, 2);
            return new CudaFastNativeResult(
                FieldsWritten: 2,
                OutputLineLength: 1,
                OutputFieldLines: 1,
                ElapsedSeconds: 0.01);
        }
    }

    private sealed class RecordingPreviewNativeSession : ICudaFastPreviewNativeSession
    {
        internal int CallCount { get; private set; }

        internal bool Disposed { get; private set; }

        internal List<ulong> TargetSamples { get; } = [];

        internal List<short[]> ReadSamples { get; } = [];

        internal List<CudaFastInputSampleFormat> InputFormats { get; } = [];

        public CudaFastPreviewNativeResult DecodeWindow(
            CudaFastPreviewWindowConfiguration configuration)
        {
            CallCount++;
            TargetSamples.Add(configuration.TargetSourceSample);
            InputFormats.Add(configuration.InputSampleFormat);
            nint input = Marshal.AllocHGlobal(3 * sizeof(short));
            try
            {
                int read = checked((int)configuration.ReadCallback(
                    configuration.UserData,
                    input,
                    configuration.TargetSourceSample,
                    sampleCount: 3));
                var samples = new short[read];
                Marshal.Copy(input, samples, 0, read);
                ReadSamples.Add(samples);
            }
            finally
            {
                Marshal.FreeHGlobal(input);
            }

            byte[] packet = [0, 0, 1, checked((byte)CallCount)];
            nint packetMemory = Marshal.AllocHGlobal(packet.Length);
            try
            {
                Marshal.Copy(packet, 0, packetMemory, packet.Length);
                Assert.Equal(0, configuration.BitstreamCallback(
                    configuration.UserData,
                    packetMemory,
                    checked((nuint)packet.Length)));
            }
            finally
            {
                Marshal.FreeHGlobal(packetMemory);
            }

            return new CudaFastPreviewNativeResult(
                configuration.RequestedOutputFrames,
                configuration.RequestedOutputFrames + 1,
                checked((ulong)packet.Length),
                0.01);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FallbackOnlyInt16Loader(double[] samples) : IInt16RfSampleLoader
    {
        private readonly double[] _samples = samples;

        internal int DirectReadCount { get; private set; }

        internal int FallbackReadCount { get; private set; }

        public bool TryReadInt16(
            Stream stream,
            long sample,
            Span<short> destination,
            out int samplesRead)
        {
            DirectReadCount++;
            samplesRead = 0;
            return false;
        }

        public double[]? Read(Stream stream, long sample, int readLength)
        {
            FallbackReadCount++;
            return _samples.AsSpan(checked((int)sample), readLength).ToArray();
        }
    }

    private sealed class RecordingDecodeRunner : ICudaFastDecodeRunner
    {
        internal int CallCount { get; private set; }

        public TbcFieldSequenceDecodeResult TryDecodeAndWrite(
            ParsedCommand command,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.Equal("cuda-fast", command.Get<string>("dsp_backend"));
            return new TbcFieldSequenceDecodeResult(
                Success: true,
                Message: "fake CUDA success",
                Paths: null,
                Fields: [],
                WrittenFieldCount: 2);
        }
    }

    private sealed class CancellingNativeRuntime : ICudaFastNativeRuntime
    {
        private readonly CancellationTokenSource _cancellation;

        internal CancellingNativeRuntime(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public CudaFastRuntimeInfo GetRuntimeInfo(int deviceId = 0)
            => new(
                CudaFastNativeRuntime.AbiVersion,
                deviceId,
                8,
                9,
                46,
                12UL * 1024 * 1024 * 1024,
                8UL * 1024 * 1024 * 1024,
                "Test GPU");

        public CudaFastNativeResult Run(CudaFastNativeRunConfiguration configuration)
        {
            _cancellation.Cancel();
            Assert.Equal(1, configuration.CancelCallback(configuration.UserData));
            throw new OperationCanceledException(_cancellation.Token);
        }
    }
}
