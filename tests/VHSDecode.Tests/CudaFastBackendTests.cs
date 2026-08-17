using System.Runtime.InteropServices;
using System.Text.Json;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp.CudaFast;
using VHSDecode.Core.HiFi;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CudaFastBackendTests
{
    [Fact(DisplayName = "CUDA-fast managed structures match native ABI v4")]
    public void ManagedStructuresMatchNativeAbi()
    {
        Assert.Equal(168, CudaFastNativeRuntime.RuntimeInfoStructureSize);
        Assert.Equal(80, CudaFastNativeRuntime.ConfigurationStructureSize);
        Assert.Equal(24, CudaFastNativeRuntime.ResultStructureSize);
    }

    [Fact(DisplayName = "CUDA-fast staged native bridge reports the pinned ABI")]
    public void StagedNativeBridgeReportsPinnedAbi()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows()
                && CudaFastNativeRuntime.BuildCandidatePaths().Any(File.Exists),
            "The optional CUDA-fast bridge was not staged for this test build.");

        ICudaFastNativeRuntime runtime = CudaFastNativeRuntime.RequireAvailable();
        CudaFastRuntimeInfo info = runtime.GetRuntimeInfo();

        Assert.Equal(CudaFastNativeRuntime.AbiVersion, info.AbiVersion);
        Assert.NotEmpty(info.DeviceName);
        Assert.True(info.ComputeMajor >= 7);
        Assert.True(info.TotalVramBytes > 0);
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
        Assert.Contains("0x00040000", diagnostic, StringComparison.Ordinal);
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

    [Fact(DisplayName = "CUDA-fast reuses and releases its persistent chroma workspace")]
    public void NativeBuildReusesAndReleasesPersistentChromaWorkspace()
    {
        string cmake = ReadNativeBuildDefinition();

        Assert.Contains("struct CudaFastChromaWorkspace", cmake, StringComparison.Ordinal);
        Assert.Contains("ensure_chroma_workspace(", cmake, StringComparison.Ordinal);
        Assert.Contains(
            "CUVHS_DISABLE_PERSISTENT_CHROMA_WORKSPACE",
            cmake,
            StringComparison.Ordinal);
        Assert.Contains("chroma_state_release(&chroma_state);", cmake, StringComparison.Ordinal);
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
        Assert.Contains("return writer.finalize();", cmake, StringComparison.Ordinal);
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
