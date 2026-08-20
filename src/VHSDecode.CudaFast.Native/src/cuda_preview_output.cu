#include "cuda_preview_output.h"

#include "pipeline/dropout_detect.h"

#include <cuda.h>
#include <cuda_runtime.h>
#include <ffnvcodec/nvEncodeAPI.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <sstream>
#include <utility>

#if defined(_WIN32)
#include <windows.h>
#endif

namespace {

constexpr float kMinimumBurstMagnitude = 64.0f;
constexpr float kNtscUScale = -3.8f / 256.0f;
constexpr float kNtscVScale = -2.75f / 256.0f;
constexpr float kPalUScale = -2.445f / 256.0f;
constexpr float kPalVScale = 1.733f / 256.0f;

bool preview_env_enabled(const char* name)
{
    const char* value = std::getenv(name);
    return value != nullptr && value[0] != '\0' && value[0] != '0';
}

float preview_env_weight(const char* name, float default_value)
{
    const char* value = std::getenv(name);
    if (value == nullptr || value[0] == '\0') return default_value;
    char* end = nullptr;
    const float parsed = std::strtof(value, &end);
    if (end == value || !std::isfinite(parsed)) return 0.0f;
    return std::clamp(parsed, 0.0f, 0.5f);
}

__device__ __forceinline__ uint8_t clamp_video_luma(float value)
{
    const int rounded = __float2int_rn(value);
    return static_cast<uint8_t>(max(16, min(235, rounded)));
}

__device__ __forceinline__ uint8_t clamp_video_chroma(float value)
{
    const int rounded = __float2int_rn(value);
    return static_cast<uint8_t>(max(16, min(240, rounded)));
}

__device__ __forceinline__ float luma_to_video_range(
    uint16_t sample,
    float output_zero,
    float output_scale,
    float vsync_ire)
{
    const float ire = (static_cast<float>(sample) - output_zero) / output_scale
        + vsync_ire;
    return 16.0f + ire * 2.19f;
}

__global__ void mark_preview_dropouts(
    uint8_t* __restrict__ mask,
    int width,
    int height,
    int source_first_line,
    int active_start,
    int active_end,
    const int* __restrict__ dropout_lines,
    const int* __restrict__ dropout_starts,
    const int* __restrict__ dropout_ends,
    const int* __restrict__ dropout_count)
{
    const int dropout = blockIdx.y;
    const int count = min(*dropout_count, MAX_DROPOUTS_PER_FIELD);
    if (dropout >= count) return;

    const int source_line = dropout_lines[dropout];
    const int field_y = source_line - source_first_line;
    if (field_y < 0 || field_y >= height / 2) return;

    const int active_width = active_end - active_start;
    for (int x = blockIdx.x * blockDim.x + threadIdx.x;
         x < width;
         x += blockDim.x * gridDim.x) {
        const int source_x = active_start
            + static_cast<int>((static_cast<long long>(x) * active_width) / width);
        if (source_x >= dropout_starts[dropout]
            && source_x < dropout_ends[dropout]) {
            const int output_y0 = field_y * 2;
            mask[static_cast<size_t>(output_y0) * width + x] = 1;
            if (output_y0 + 1 < height) {
                mask[static_cast<size_t>(output_y0 + 1) * width + x] = 1;
            }
        }
    }
}

__global__ void render_preview_luma(
    const uint16_t* __restrict__ source,
    const uint16_t* __restrict__ paired_source,
    cudaSurfaceObject_t destination,
    const uint8_t* __restrict__ dropout_mask,
    const uint8_t* __restrict__ paired_dropout_mask,
    int width,
    int height,
    int source_line_length,
    int source_line_count,
    int source_first_line,
    int active_start,
    int active_end,
    int is_first_field,
    float output_zero,
    float output_scale,
    float vsync_ire)
{
    const int x = blockIdx.x * blockDim.x + threadIdx.x;
    const int y = blockIdx.y * blockDim.y + threadIdx.y;
    if (x >= width || y >= height) return;

    const int active_width = active_end - active_start;
    const int source_x = active_start
        + static_cast<int>((static_cast<long long>(x) * active_width) / width);
    int field_y = y / 2;
    const size_t output_index = static_cast<size_t>(y) * width + x;
    const bool dropout = dropout_mask[output_index] != 0;
    if (dropout && paired_source != nullptr
        && (paired_dropout_mask == nullptr || paired_dropout_mask[output_index] == 0)) {
        const int paired_source_y = min(
            source_line_count - 1,
            source_first_line + field_y);
        const uint16_t paired_sample = paired_source[
            static_cast<size_t>(paired_source_y) * source_line_length + source_x];
        surf2Dwrite(
            clamp_video_luma(luma_to_video_range(
                paired_sample,
                output_zero,
                output_scale,
                vsync_ire)),
            destination,
            x * sizeof(uint8_t),
            y);
        return;
    }
    const bool direct_line = (y & 1) == (is_first_field != 0 ? 0 : 1);
    int field_y0 = field_y;
    int field_y1 = field_y;
    if (!direct_line) {
        if (is_first_field != 0) {
            field_y1 = min(height / 2 - 1, field_y + 1);
        } else {
            field_y0 = max(0, field_y - 1);
        }
    }

    if (dropout) {
        const int previous = field_y0 - 1;
        const int next = field_y1 + 1;
        if (previous >= 0) {
            field_y0 = previous;
            field_y1 = previous;
        } else if (next < height / 2) {
            field_y0 = next;
            field_y1 = next;
        }
    }

    const int source_y0 = min(source_line_count - 1, source_first_line + field_y0);
    const int source_y1 = min(source_line_count - 1, source_first_line + field_y1);
    const uint16_t sample0 = source[static_cast<size_t>(source_y0) * source_line_length + source_x];
    const uint16_t sample1 = source[static_cast<size_t>(source_y1) * source_line_length + source_x];
    const float value = 0.5f
        * (luma_to_video_range(sample0, output_zero, output_scale, vsync_ire)
            + luma_to_video_range(sample1, output_zero, output_scale, vsync_ire));
    surf2Dwrite(clamp_video_luma(value), destination, x * sizeof(uint8_t), y);
}

__global__ void detect_preview_bursts(
    const uint16_t* __restrict__ chroma,
    float2* __restrict__ bursts,
    int line_length,
    int line_count,
    int burst_start,
    int burst_end)
{
    const int line = blockIdx.x * blockDim.x + threadIdx.x;
    if (line >= line_count) return;

    float real = 0.0f;
    float imaginary = 0.0f;
    const int count = max(1, burst_end - burst_start);
    const size_t line_offset = static_cast<size_t>(line) * line_length;
    for (int x = burst_start; x < burst_end; ++x) {
        const float value = static_cast<float>(chroma[line_offset + x]) - 32767.0f;
        switch (x & 3) {
            case 0: real += value; break;
            case 1: imaginary -= value; break;
            case 2: real -= value; break;
            default: imaginary += value; break;
        }
    }
    bursts[line] = make_float2(real / count, imaginary / count);
}

__device__ bool decode_preview_chroma_line(
    const uint16_t* source,
    const float2* bursts,
    int source_line,
    int source_line_count,
    int source_line_length,
    int start,
    int end,
    bool pal,
    bool first_field,
    int field_phase_id,
    float* output_u,
    float* output_v)
{
    if (source_line < 0 || source_line >= source_line_count || end <= start) {
        return false;
    }
    const float2 burst = bursts[source_line];
    const float magnitude = sqrtf(burst.x * burst.x + burst.y * burst.y);
    if (!(magnitude >= kMinimumBurstMagnitude)) return false;

    float real = 0.0f;
    float imaginary = 0.0f;
    const size_t line_offset = static_cast<size_t>(source_line) * source_line_length;
    for (int x = start; x < end; ++x) {
        const float value = static_cast<float>(source[line_offset + x]) - 32767.0f;
        switch (x & 3) {
            case 0: real += value; break;
            case 1: imaginary -= value; break;
            case 2: real -= value; break;
            default: imaginary += value; break;
        }
    }
    const float scale = 1.0f / static_cast<float>(end - start);
    real *= scale;
    imaginary *= scale;
    const float burst_real = burst.x / magnitude;
    const float burst_imaginary = burst.y / magnitude;
    const float rotated_real = real * burst_real + imaginary * burst_imaginary;
    const float rotated_imaginary = imaginary * burst_real - real * burst_imaginary;

    if (!pal) {
        *output_u = 128.0f + rotated_real * kNtscUScale;
        *output_v = 128.0f + rotated_imaginary * kNtscVScale;
        return true;
    }

    int v_switch = 0;
    if (source_line >= 2 && source_line + 2 < source_line_count) {
        const float2 delayed = bursts[source_line - 2];
        const float2 advanced = bursts[source_line + 2];
        const float2 previous = bursts[source_line - 1];
        const float2 next = bursts[source_line + 1];
        const float current_real = (burst.x - 0.5f * (delayed.x + advanced.x)) * 0.5f;
        const float current_imaginary = (burst.y - 0.5f * (delayed.y + advanced.y)) * 0.5f;
        const float opposite_real = (next.x - previous.x) * 0.5f;
        const float opposite_imaginary = (next.y - previous.y) * 0.5f;
        const float current_magnitude = current_real * current_real
            + current_imaginary * current_imaginary;
        const float difference_real = current_real - opposite_real;
        const float difference_imaginary = current_imaginary - opposite_imaginary;
        const float difference_magnitude = difference_real * difference_real
            + difference_imaginary * difference_imaginary;
        if (current_magnitude > 0.0f) {
            v_switch = difference_magnitude < current_magnitude * 2.0f ? 1 : -1;
        }
    }
    if (v_switch == 0) {
        v_switch = first_field == ((source_line & 1) == 0) ? 1 : -1;
        const int phase = max(1, min(8, field_phase_id));
        if (phase == 3 || phase == 4 || phase == 7 || phase == 8) {
            v_switch = -v_switch;
        }
    }
    *output_u = 128.0f
        + (rotated_real + static_cast<float>(v_switch) * rotated_imaginary) * kPalUScale;
    *output_v = 128.0f
        + (rotated_real - static_cast<float>(v_switch) * rotated_imaginary) * kPalVScale;
    return true;
}

__global__ void render_preview_chroma(
    const uint16_t* __restrict__ source,
    const float2* __restrict__ bursts,
    const uint16_t* __restrict__ paired_source,
    const float2* __restrict__ paired_bursts,
    cudaSurfaceObject_t destination_uv,
    const uint8_t* __restrict__ dropout_mask,
    const uint8_t* __restrict__ paired_dropout_mask,
    uchar2* __restrict__ previous_chroma,
    int width,
    int height,
    int source_line_length,
    int source_line_count,
    int source_first_line,
    int active_start,
    int active_end,
    int is_first_field,
    int field_phase_id,
    int paired_is_first_field,
    int paired_field_phase_id,
    int pal,
    int have_previous_chroma,
    float temporal_weight)
{
    const int chroma_x = blockIdx.x * blockDim.x + threadIdx.x;
    const int chroma_y = blockIdx.y * blockDim.y + threadIdx.y;
    const int chroma_width = width / 2;
    const int chroma_height = height / 2;
    if (chroma_x >= chroma_width || chroma_y >= chroma_height) return;

    const int field_chroma_y = chroma_y / 2;
    const int luma_x = min(width - 1, chroma_x * 2);
    const int luma_y = min(height - 1, chroma_y * 2);
    const bool dropout = dropout_mask[static_cast<size_t>(luma_y) * width + luma_x] != 0
        || dropout_mask[static_cast<size_t>(luma_y) * width + min(width - 1, luma_x + 1)] != 0
        || dropout_mask[static_cast<size_t>(min(height - 1, luma_y + 1)) * width + luma_x] != 0;
    const bool paired_dropout = paired_dropout_mask != nullptr
        && (paired_dropout_mask[static_cast<size_t>(luma_y) * width + luma_x] != 0
            || paired_dropout_mask[static_cast<size_t>(luma_y) * width
                + min(width - 1, luma_x + 1)] != 0
            || paired_dropout_mask[static_cast<size_t>(min(height - 1, luma_y + 1))
                * width + luma_x] != 0);
    const bool use_paired = dropout && paired_source != nullptr
        && paired_bursts != nullptr && !paired_dropout;
    const uint16_t* selected_source = use_paired ? paired_source : source;
    const float2* selected_bursts = use_paired ? paired_bursts : bursts;
    const int selected_first_field = use_paired
        ? paired_is_first_field
        : is_first_field;
    const int selected_phase_id = use_paired
        ? paired_field_phase_id
        : field_phase_id;
    int source_line0 = source_first_line + field_chroma_y * 2;
    int source_line1 = min(source_line_count - 1, source_line0 + 1);
    if (dropout && !use_paired) {
        if (source_line0 >= source_first_line + 2) {
            source_line0 -= 2;
            source_line1 -= 2;
        } else if (source_line1 + 2 < source_line_count) {
            source_line0 += 2;
            source_line1 += 2;
        }
    }

    const int active_width = active_end - active_start;
    const int center = active_start + static_cast<int>(
        (static_cast<long long>(chroma_x * 2 + 1) * active_width)
        / (static_cast<long long>(chroma_width) * 2));
    const int start = max(active_start, center - 4);
    const int end = min(active_end, center + 6);
    float u0 = 128.0f;
    float v0 = 128.0f;
    float u1 = 128.0f;
    float v1 = 128.0f;
    const bool valid0 = decode_preview_chroma_line(
        selected_source,
        selected_bursts,
        source_line0,
        source_line_count,
        source_line_length,
        start,
        end,
        pal != 0,
        selected_first_field != 0,
        selected_phase_id,
        &u0,
        &v0);
    const bool valid1 = decode_preview_chroma_line(
        selected_source,
        selected_bursts,
        source_line1,
        source_line_count,
        source_line_length,
        start,
        end,
        pal != 0,
        selected_first_field != 0,
        selected_phase_id,
        &u1,
        &v1);
    float u = 128.0f;
    float v = 128.0f;
    if (valid0 && valid1) {
        u = 0.5f * (u0 + u1);
        v = 0.5f * (v0 + v1);
    } else if (valid0) {
        u = u0;
        v = v0;
    } else if (valid1) {
        u = u1;
        v = v1;
    }

    const uchar2 current = make_uchar2(clamp_video_chroma(u), clamp_video_chroma(v));
    uchar2 output = current;
    const size_t chroma_index = static_cast<size_t>(chroma_y) * chroma_width + chroma_x;
    if (previous_chroma != nullptr) {
        if (have_previous_chroma != 0 && temporal_weight > 0.0f) {
            const uchar2 previous = previous_chroma[chroma_index];
            output = make_uchar2(
                clamp_video_chroma(
                    static_cast<float>(current.x) * (1.0f - temporal_weight)
                        + static_cast<float>(previous.x) * temporal_weight),
                clamp_video_chroma(
                    static_cast<float>(current.y) * (1.0f - temporal_weight)
                        + static_cast<float>(previous.y) * temporal_weight));
        }
        previous_chroma[chroma_index] = current;
    }
    surf2Dwrite(output, destination_uv, chroma_x * sizeof(uchar2), chroma_y);
}

std::string nvenc_status_message(const char* operation, NVENCSTATUS status)
{
    std::ostringstream stream;
    stream << operation << " failed with NVENC status " << static_cast<int>(status) << '.';
    return stream.str();
}

}  // namespace

struct CudaPreviewOutput::Impl {
    VideoFormat format = VideoFormat(VideoProfile::NTSC_525_60_VHS, 20.0);
    CudaPreviewOutputSettings settings;
    std::string error;
    uint32_t frames_encoded = 0;
    uint32_t fields_scanned = 0;
    uint64_t encoded_bytes = 0;
    bool opened = false;
    bool finalized = false;
    bool have_parity = false;
    bool last_parity = false;

#if defined(_WIN32)
    HMODULE nvenc_module = nullptr;
#endif
    NV_ENCODE_API_FUNCTION_LIST api{};
    void* encoder = nullptr;
    CUarray nv12_array = nullptr;
    CUarray nv12_luma_plane = nullptr;
    CUarray nv12_chroma_plane = nullptr;
    cudaSurfaceObject_t nv12_luma_surface = 0;
    cudaSurfaceObject_t nv12_chroma_surface = 0;
    uint32_t nv12_allocation_width = 0;
    float2* d_bursts = nullptr;
    float2* d_paired_bursts = nullptr;
    uint8_t* d_dropout_mask = nullptr;
    uint8_t* d_paired_dropout_mask = nullptr;
    uchar2* d_previous_chroma = nullptr;
    bool cross_field_dropout = false;
    bool have_previous_chroma = false;
    float chroma_temporal_weight = 0.0f;
    NV_ENC_REGISTERED_PTR registered_input = nullptr;
    NV_ENC_INPUT_PTR mapped_input = nullptr;
    NV_ENC_OUTPUT_PTR bitstream = nullptr;

    bool fail(std::string message)
    {
        if (error.empty()) error = std::move(message);
        return false;
    }

    bool nvenc_ok(const char* operation, NVENCSTATUS status)
    {
        return status == NV_ENC_SUCCESS
            || fail(nvenc_status_message(operation, status));
    }

    bool cancelled() const
    {
        return settings.cancel_callback != nullptr
            && settings.cancel_callback(settings.user_data) != 0;
    }

    bool initialize_nvenc()
    {
#if !defined(_WIN32)
        return fail("Direct CUDA/NVENC preview currently supports Windows only.");
#else
        if (cudaSetDevice(settings.device_id) != cudaSuccess) {
            return fail("CUDA preview could not select the configured GPU.");
        }
        if (cudaFree(nullptr) != cudaSuccess) {
            return fail("CUDA preview could not initialize the primary CUDA context.");
        }
        CUcontext cuda_context = nullptr;
        if (cuCtxGetCurrent(&cuda_context) != CUDA_SUCCESS || cuda_context == nullptr) {
            return fail("CUDA preview could not obtain the current CUDA context for NVENC.");
        }

        nvenc_module = LoadLibraryW(L"nvEncodeAPI64.dll");
        if (nvenc_module == nullptr) {
            return fail("nvEncodeAPI64.dll was not found in the NVIDIA display driver.");
        }
        using GetMaxVersion = NVENCSTATUS (NVENCAPI*)(uint32_t*);
        using CreateInstance = NVENCSTATUS (NVENCAPI*)(NV_ENCODE_API_FUNCTION_LIST*);
        auto get_max_version = reinterpret_cast<GetMaxVersion>(
            GetProcAddress(nvenc_module, "NvEncodeAPIGetMaxSupportedVersion"));
        auto create_instance = reinterpret_cast<CreateInstance>(
            GetProcAddress(nvenc_module, "NvEncodeAPICreateInstance"));
        if (get_max_version == nullptr || create_instance == nullptr) {
            return fail("The NVIDIA driver does not expose the required NVENC entry points.");
        }
        uint32_t driver_version = 0;
        if (!nvenc_ok("NvEncodeAPIGetMaxSupportedVersion", get_max_version(&driver_version))) {
            return false;
        }
        const uint32_t requested_version =
            (NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION;
        if (driver_version < requested_version) {
            std::ostringstream message;
            message << "The NVIDIA driver supports NVENC API 0x" << std::hex
                << driver_version << " but preview requires 0x" << requested_version << '.';
            return fail(message.str());
        }

        std::memset(&api, 0, sizeof(api));
        api.version = NV_ENCODE_API_FUNCTION_LIST_VER;
        if (!nvenc_ok("NvEncodeAPICreateInstance", create_instance(&api))) return false;

        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open_params{};
        open_params.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        open_params.deviceType = NV_ENC_DEVICE_TYPE_CUDA;
        open_params.device = cuda_context;
        open_params.apiVersion = NVENCAPI_VERSION;
        if (!nvenc_ok(
                "nvEncOpenEncodeSessionEx",
                api.nvEncOpenEncodeSessionEx(&open_params, &encoder))) {
            return false;
        }

        NV_ENC_PRESET_CONFIG preset{};
        preset.version = NV_ENC_PRESET_CONFIG_VER;
        preset.presetCfg.version = NV_ENC_CONFIG_VER;
        if (!nvenc_ok(
                "nvEncGetEncodePresetConfigEx",
                api.nvEncGetEncodePresetConfigEx(
                    encoder,
                    NV_ENC_CODEC_H264_GUID,
                    NV_ENC_PRESET_P1_GUID,
                    NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY,
                    &preset))) {
            return false;
        }

        NV_ENC_CONFIG encode_config = preset.presetCfg;
        encode_config.version = NV_ENC_CONFIG_VER;
        encode_config.profileGUID = NV_ENC_H264_PROFILE_MAIN_GUID;
        encode_config.gopLength = settings.gop_length;
        encode_config.frameIntervalP = 1;
        encode_config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CONSTQP;
        encode_config.rcParams.constQP.qpIntra = settings.constant_qp;
        encode_config.rcParams.constQP.qpInterP = settings.constant_qp;
        encode_config.rcParams.constQP.qpInterB = settings.constant_qp;
        NV_ENC_CONFIG_H264& h264 = encode_config.encodeCodecConfig.h264Config;
        h264.level = NV_ENC_LEVEL_H264_31;
        h264.idrPeriod = settings.gop_length;
        h264.repeatSPSPPS = 1;
        h264.outputAUD = 1;
        h264.chromaFormatIDC = 1;
        NV_ENC_CONFIG_H264_VUI_PARAMETERS& vui = h264.h264VUIParameters;
        vui.videoSignalTypePresentFlag = 1;
        vui.videoFormat = format.system == VideoSystem::PAL
            ? NV_ENC_VUI_VIDEO_FORMAT_PAL
            : NV_ENC_VUI_VIDEO_FORMAT_NTSC;
        vui.videoFullRangeFlag = 0;
        vui.colourDescriptionPresentFlag = 1;
        vui.colourPrimaries = format.system == VideoSystem::PAL
            ? NV_ENC_VUI_COLOR_PRIMARIES_BT470BG
            : NV_ENC_VUI_COLOR_PRIMARIES_SMPTE170M;
        vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709;
        vui.colourMatrix = format.system == VideoSystem::PAL
            ? NV_ENC_VUI_MATRIX_COEFFS_BT470BG
            : NV_ENC_VUI_MATRIX_COEFFS_SMPTE170M;
        vui.timingInfoPresentFlag = 1;
        vui.numUnitInTicks = settings.frame_rate_denominator;
        vui.timeScale = settings.frame_rate_numerator * 2;

        NV_ENC_INITIALIZE_PARAMS initialize{};
        initialize.version = NV_ENC_INITIALIZE_PARAMS_VER;
        initialize.encodeGUID = NV_ENC_CODEC_H264_GUID;
        initialize.presetGUID = NV_ENC_PRESET_P1_GUID;
        initialize.encodeWidth = settings.width;
        initialize.encodeHeight = settings.height;
        initialize.darWidth = settings.width;
        initialize.darHeight = settings.height;
        initialize.frameRateNum = settings.frame_rate_numerator;
        initialize.frameRateDen = settings.frame_rate_denominator;
        initialize.enableEncodeAsync = 0;
        initialize.enablePTD = 1;
        initialize.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
        initialize.encodeConfig = &encode_config;
        initialize.maxEncodeWidth = settings.width;
        initialize.maxEncodeHeight = settings.height;
        if (!nvenc_ok(
                "nvEncInitializeEncoder",
                api.nvEncInitializeEncoder(encoder, &initialize))) {
            return false;
        }

        CUDA_ARRAY3D_DESCRIPTOR nv12_descriptor{};
        nv12_descriptor.Width = settings.width;
        nv12_descriptor.Height = settings.height;
        nv12_descriptor.Format = CU_AD_FORMAT_NV12;
        nv12_descriptor.NumChannels = 3;
        nv12_descriptor.Flags = CUDA_ARRAY3D_SURFACE_LDST
            | CUDA_ARRAY3D_VIDEO_ENCODE_DECODE;
        CUDA_ARRAY3D_DESCRIPTOR nv12_luma_descriptor{};
        if (cuArray3DCreate(&nv12_array, &nv12_descriptor) != CUDA_SUCCESS
            || cuArrayGetPlane(&nv12_luma_plane, nv12_array, 0) != CUDA_SUCCESS
            || cuArrayGetPlane(&nv12_chroma_plane, nv12_array, 1) != CUDA_SUCCESS
            || cuArray3DGetDescriptor(
                &nv12_luma_descriptor,
                nv12_luma_plane) != CUDA_SUCCESS) {
            return fail("CUDA preview could not allocate its block-linear NV12 array.");
        }
        // CU_AD_FORMAT_NV12 is a special multi-planar array: NumChannels=3 on
        // the parent describes its Y/U/V content, not three packed bytes per
        // luma pixel. NVENC therefore needs the byte width of the luma plane,
        // as also used by FFmpeg's CUDA-array registration path. Derive that
        // width from the actual plane descriptor instead of multiplying the
        // parent descriptor by three.
        if (nv12_luma_descriptor.Format != CU_AD_FORMAT_UNSIGNED_INT8
            || nv12_luma_descriptor.NumChannels != 1
            || nv12_luma_descriptor.Width < settings.width
            || nv12_luma_descriptor.Width > std::numeric_limits<uint32_t>::max()) {
            return fail("CUDA preview returned an invalid NV12 luma-plane descriptor.");
        }
        nv12_allocation_width = static_cast<uint32_t>(nv12_luma_descriptor.Width);

        cudaResourceDesc luma_resource{};
        luma_resource.resType = cudaResourceTypeArray;
        luma_resource.res.array.array = reinterpret_cast<cudaArray_t>(nv12_luma_plane);
        cudaResourceDesc chroma_resource{};
        chroma_resource.resType = cudaResourceTypeArray;
        chroma_resource.res.array.array = reinterpret_cast<cudaArray_t>(nv12_chroma_plane);
        if (cudaCreateSurfaceObject(&nv12_luma_surface, &luma_resource) != cudaSuccess
            || cudaCreateSurfaceObject(&nv12_chroma_surface, &chroma_resource) != cudaSuccess) {
            return fail("CUDA preview could not create its block-linear NV12 surfaces.");
        }
        if (cudaMalloc(
                &d_bursts,
                static_cast<size_t>(format.output_field_lines) * sizeof(float2)) != cudaSuccess
            || cudaMalloc(
                &d_paired_bursts,
                static_cast<size_t>(format.output_field_lines) * sizeof(float2)) != cudaSuccess
            || cudaMalloc(
                &d_dropout_mask,
                static_cast<size_t>(settings.width) * settings.height) != cudaSuccess
            || cudaMalloc(
                &d_paired_dropout_mask,
                static_cast<size_t>(settings.width) * settings.height) != cudaSuccess
            || cudaMalloc(
                &d_previous_chroma,
                static_cast<size_t>(settings.width / 2)
                    * (settings.height / 2) * sizeof(uchar2)) != cudaSuccess) {
            return fail("CUDA preview could not allocate persistent renderer buffers.");
        }

        NV_ENC_REGISTER_RESOURCE registration{};
        registration.version = NV_ENC_REGISTER_RESOURCE_VER;
        registration.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_CUDAARRAY;
        registration.width = settings.width;
        registration.height = settings.height;
        registration.pitch = nv12_allocation_width;
        registration.resourceToRegister = reinterpret_cast<void*>(nv12_array);
        registration.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
        registration.bufferUsage = NV_ENC_INPUT_IMAGE;
        if (!nvenc_ok(
                "nvEncRegisterResource",
                api.nvEncRegisterResource(encoder, &registration))) {
            return false;
        }
        registered_input = registration.registeredResource;

        NV_ENC_CREATE_BITSTREAM_BUFFER create_bitstream{};
        create_bitstream.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
        if (!nvenc_ok(
                "nvEncCreateBitstreamBuffer",
                api.nvEncCreateBitstreamBuffer(encoder, &create_bitstream))) {
            return false;
        }
        bitstream = create_bitstream.bitstreamBuffer;
        return true;
#endif
    }

    bool render_field(
        const uint16_t* d_luma,
        const uint16_t* d_chroma,
        const uint16_t* d_paired_luma,
        const uint16_t* d_paired_chroma,
        const int* d_dropout_lines,
        const int* d_dropout_starts,
        const int* d_dropout_ends,
        const int* d_dropout_count,
        const int* d_paired_dropout_lines,
        const int* d_paired_dropout_starts,
        const int* d_paired_dropout_ends,
        const int* d_paired_dropout_count,
        bool first_field,
        int phase_id,
        bool paired_first_field,
        int paired_phase_id)
    {
        if (cudaMemset(
                d_dropout_mask,
                0,
                static_cast<size_t>(settings.width) * settings.height) != cudaSuccess) {
            return fail("CUDA preview could not clear its dropout mask.");
        }
        const int source_first_line = format.system == VideoSystem::PAL ? 22 : 20;
        const int active_start = format.system == VideoSystem::PAL ? 185 : 134;
        const int active_end = format.system == VideoSystem::PAL ? 1107 : 894;
        if (d_dropout_count != nullptr) {
            const dim3 dropout_grid((settings.width + 255) / 256, MAX_DROPOUTS_PER_FIELD);
            mark_preview_dropouts<<<dropout_grid, 256>>>(
                d_dropout_mask,
                static_cast<int>(settings.width),
                static_cast<int>(settings.height),
                source_first_line,
                active_start,
                active_end,
                d_dropout_lines,
                d_dropout_starts,
                d_dropout_ends,
                d_dropout_count);
        }
        const bool use_paired = cross_field_dropout
            && d_paired_luma != nullptr && d_paired_chroma != nullptr;
        if (use_paired) {
            if (cudaMemset(
                    d_paired_dropout_mask,
                    0,
                    static_cast<size_t>(settings.width) * settings.height) != cudaSuccess) {
                return fail("CUDA preview could not clear its paired dropout mask.");
            }
            if (d_paired_dropout_count != nullptr) {
                const dim3 paired_dropout_grid(
                    (settings.width + 255) / 256,
                    MAX_DROPOUTS_PER_FIELD);
                mark_preview_dropouts<<<paired_dropout_grid, 256>>>(
                    d_paired_dropout_mask,
                    static_cast<int>(settings.width),
                    static_cast<int>(settings.height),
                    source_first_line,
                    active_start,
                    active_end,
                    d_paired_dropout_lines,
                    d_paired_dropout_starts,
                    d_paired_dropout_ends,
                    d_paired_dropout_count);
            }
        }

        const dim3 luma_block(32, 8);
        const dim3 luma_grid(
            (settings.width + luma_block.x - 1) / luma_block.x,
            (settings.height + luma_block.y - 1) / luma_block.y);
        render_preview_luma<<<luma_grid, luma_block>>>(
            d_luma,
            use_paired ? d_paired_luma : nullptr,
            nv12_luma_surface,
            d_dropout_mask,
            use_paired ? d_paired_dropout_mask : nullptr,
            static_cast<int>(settings.width),
            static_cast<int>(settings.height),
            format.output_line_len,
            format.output_field_lines,
            source_first_line,
            active_start,
            active_end,
            first_field ? 1 : 0,
            static_cast<float>(format.output_zero),
            static_cast<float>(format.output_scale),
            static_cast<float>(format.vsync_ire));

        constexpr int burst_threads = 128;
        const int burst_blocks = (format.output_field_lines + burst_threads - 1) / burst_threads;
        const int burst_start = static_cast<int>(
            format.burst_start_us * 1e-6 * format.output_rate + 0.5);
        const int burst_end = static_cast<int>(
            format.burst_end_us * 1e-6 * format.output_rate + 0.5);
        detect_preview_bursts<<<burst_blocks, burst_threads>>>(
            d_chroma,
            d_bursts,
            format.output_line_len,
            format.output_field_lines,
            burst_start,
            burst_end);
        if (use_paired) {
            detect_preview_bursts<<<burst_blocks, burst_threads>>>(
                d_paired_chroma,
                d_paired_bursts,
                format.output_line_len,
                format.output_field_lines,
                burst_start,
                burst_end);
        }

        const dim3 chroma_block(32, 8);
        const dim3 chroma_grid(
            (settings.width / 2 + chroma_block.x - 1) / chroma_block.x,
            (settings.height / 2 + chroma_block.y - 1) / chroma_block.y);
        render_preview_chroma<<<chroma_grid, chroma_block>>>(
            d_chroma,
            d_bursts,
            use_paired ? d_paired_chroma : nullptr,
            use_paired ? d_paired_bursts : nullptr,
            nv12_chroma_surface,
            d_dropout_mask,
            use_paired ? d_paired_dropout_mask : nullptr,
            chroma_temporal_weight > 0.0f ? d_previous_chroma : nullptr,
            static_cast<int>(settings.width),
            static_cast<int>(settings.height),
            format.output_line_len,
            format.output_field_lines,
            source_first_line,
            active_start,
            active_end,
            first_field ? 1 : 0,
            phase_id,
            paired_first_field ? 1 : 0,
            paired_phase_id,
            format.system == VideoSystem::PAL ? 1 : 0,
            have_previous_chroma ? 1 : 0,
            chroma_temporal_weight);

        if (chroma_temporal_weight > 0.0f) {
            have_previous_chroma = true;
        }

        const cudaError_t launch_status = cudaGetLastError();
        if (launch_status != cudaSuccess) {
            return fail(std::string("CUDA preview renderer launch failed: ")
                + cudaGetErrorString(launch_status));
        }
        const cudaError_t synchronize_status = cudaStreamSynchronize(nullptr);
        if (synchronize_status != cudaSuccess) {
            return fail(std::string("CUDA preview renderer synchronization failed: ")
                + cudaGetErrorString(synchronize_status));
        }
        return true;
    }

    bool encode_current_surface()
    {
        NV_ENC_MAP_INPUT_RESOURCE mapping{};
        mapping.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
        mapping.registeredResource = registered_input;
        if (!nvenc_ok("nvEncMapInputResource", api.nvEncMapInputResource(encoder, &mapping))) {
            return false;
        }
        mapped_input = mapping.mappedResource;

        NV_ENC_PIC_PARAMS picture{};
        picture.version = NV_ENC_PIC_PARAMS_VER;
        picture.inputBuffer = mapped_input;
        picture.bufferFmt = mapping.mappedBufferFmt;
        picture.inputWidth = settings.width;
        picture.inputHeight = settings.height;
        picture.inputPitch = nv12_allocation_width;
        picture.outputBitstream = bitstream;
        picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
        picture.inputTimeStamp = frames_encoded;
        picture.inputDuration = 1;
        if (frames_encoded % settings.gop_length == 0) {
            picture.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR
                | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
        }
        const NVENCSTATUS encode_status = api.nvEncEncodePicture(encoder, &picture);
        if (encode_status != NV_ENC_SUCCESS) {
            api.nvEncUnmapInputResource(encoder, mapped_input);
            mapped_input = nullptr;
            if (encode_status == NV_ENC_ERR_NEED_MORE_INPUT) {
                return fail("NVENC unexpectedly buffered a zero-latency preview frame.");
            }
            return fail(nvenc_status_message("nvEncEncodePicture", encode_status));
        }

        NV_ENC_LOCK_BITSTREAM lock{};
        lock.version = NV_ENC_LOCK_BITSTREAM_VER;
        lock.outputBitstream = bitstream;
        lock.doNotWait = 0;
        if (!nvenc_ok("nvEncLockBitstream", api.nvEncLockBitstream(encoder, &lock))) {
            api.nvEncUnmapInputResource(encoder, mapped_input);
            mapped_input = nullptr;
            return false;
        }
        bool callback_ok = true;
        if (lock.bitstreamSizeInBytes > 0) {
            callback_ok = settings.bitstream_callback(
                settings.user_data,
                static_cast<const uint8_t*>(lock.bitstreamBufferPtr),
                lock.bitstreamSizeInBytes) == 0;
            if (callback_ok) encoded_bytes += lock.bitstreamSizeInBytes;
        }
        const NVENCSTATUS unlock_status = api.nvEncUnlockBitstream(encoder, bitstream);
        const NVENCSTATUS unmap_status = api.nvEncUnmapInputResource(encoder, mapped_input);
        mapped_input = nullptr;
        if (!callback_ok) return fail("The managed H.264 sink rejected NVENC output.");
        if (!nvenc_ok("nvEncUnlockBitstream", unlock_status)) return false;
        if (!nvenc_ok("nvEncUnmapInputResource", unmap_status)) return false;
        ++frames_encoded;
        return true;
    }

    bool finalize_encoder()
    {
        if (finalized) return error.empty();
        finalized = true;
        // P1 ULL + no B-frames emits each picture synchronously. Do not send
        // EOS here: the persistent encoder is reused by later seek windows,
        // each of which starts with a forced IDR and fresh SPS/PPS.
        return error.empty();
    }

    void release()
    {
        if (encoder != nullptr && mapped_input != nullptr) {
            api.nvEncUnmapInputResource(encoder, mapped_input);
            mapped_input = nullptr;
        }
        if (encoder != nullptr && bitstream != nullptr) {
            api.nvEncDestroyBitstreamBuffer(encoder, bitstream);
            bitstream = nullptr;
        }
        if (encoder != nullptr && registered_input != nullptr) {
            api.nvEncUnregisterResource(encoder, registered_input);
            registered_input = nullptr;
        }
        if (encoder != nullptr) {
            api.nvEncDestroyEncoder(encoder);
            encoder = nullptr;
        }
        if (d_dropout_mask != nullptr) {
            cudaFree(d_dropout_mask);
            d_dropout_mask = nullptr;
        }
        if (d_paired_dropout_mask != nullptr) {
            cudaFree(d_paired_dropout_mask);
            d_paired_dropout_mask = nullptr;
        }
        if (d_previous_chroma != nullptr) {
            cudaFree(d_previous_chroma);
            d_previous_chroma = nullptr;
        }
        if (d_bursts != nullptr) {
            cudaFree(d_bursts);
            d_bursts = nullptr;
        }
        if (d_paired_bursts != nullptr) {
            cudaFree(d_paired_bursts);
            d_paired_bursts = nullptr;
        }
        if (nv12_chroma_surface != 0) {
            cudaDestroySurfaceObject(nv12_chroma_surface);
            nv12_chroma_surface = 0;
        }
        if (nv12_luma_surface != 0) {
            cudaDestroySurfaceObject(nv12_luma_surface);
            nv12_luma_surface = 0;
        }
        if (nv12_array != nullptr) {
            cuArrayDestroy(nv12_array);
            nv12_array = nullptr;
            nv12_luma_plane = nullptr;
            nv12_chroma_plane = nullptr;
        }
#if defined(_WIN32)
        if (nvenc_module != nullptr) {
            FreeLibrary(nvenc_module);
            nvenc_module = nullptr;
        }
#endif
        opened = false;
    }
};

CudaPreviewOutput::CudaPreviewOutput()
    : impl_(std::make_unique<Impl>())
{
}

CudaPreviewOutput::~CudaPreviewOutput()
{
    close();
}

bool CudaPreviewOutput::open(
    const VideoFormat& format,
    const CudaPreviewOutputSettings& settings)
{
    if (settings.width == 0 || settings.height == 0
        || (settings.width & 1U) != 0 || (settings.height & 3U) != 0
        || settings.frame_rate_numerator == 0 || settings.frame_rate_denominator == 0
        || settings.constant_qp > 51 || settings.gop_length == 0
        || settings.requested_frames == 0 || settings.bitstream_callback == nullptr) {
        return impl_->fail("CUDA preview output configuration was invalid.");
    }
    if (impl_->opened) {
        const CudaPreviewOutputSettings& current = impl_->settings;
        if (current.device_id != settings.device_id
            || current.width != settings.width
            || current.height != settings.height
            || current.frame_rate_numerator != settings.frame_rate_numerator
            || current.frame_rate_denominator != settings.frame_rate_denominator
            || current.constant_qp != settings.constant_qp
            || current.gop_length != settings.gop_length
            || impl_->format.profile != format.profile
            || std::abs(impl_->format.sample_rate - format.sample_rate) > 0.5) {
            return impl_->fail(
                "CUDA preview cannot change its persistent NVENC format within a session.");
        }
        impl_->settings = settings;
        impl_->error.clear();
        impl_->frames_encoded = 0;
        impl_->fields_scanned = 0;
        impl_->encoded_bytes = 0;
        impl_->finalized = false;
        impl_->have_parity = false;
        impl_->last_parity = false;
        impl_->cross_field_dropout = !preview_env_enabled(
            "CUVHS_DISABLE_PREVIEW_CROSS_FIELD_DROPOUT");
        impl_->chroma_temporal_weight = preview_env_weight(
            "CUVHS_PREVIEW_CHROMA_TEMPORAL_WEIGHT",
            0.25f);
        impl_->have_previous_chroma = false;
        return true;
    }

    close();
    impl_ = std::make_unique<Impl>();
    impl_->format = format;
    impl_->settings = settings;
    impl_->cross_field_dropout = !preview_env_enabled(
        "CUVHS_DISABLE_PREVIEW_CROSS_FIELD_DROPOUT");
    impl_->chroma_temporal_weight = preview_env_weight(
        "CUVHS_PREVIEW_CHROMA_TEMPORAL_WEIGHT",
        0.25f);
    impl_->have_previous_chroma = false;
    if (!impl_->initialize_nvenc()) {
        impl_->release();
        return false;
    }
    impl_->opened = true;
    return true;
}

bool CudaPreviewOutput::write_device_fields(
    const uint16_t* d_luma,
    const uint16_t* d_chroma,
    const int* d_dropout_lines,
    const int* d_dropout_starts,
    const int* d_dropout_ends,
    const int* d_dropout_counts,
    const int* host_is_first_field,
    const size_t* host_field_offsets,
    const int* host_field_phase_ids,
    size_t raw_offset,
    int field_count)
{
    if (!impl_->opened || d_luma == nullptr || d_chroma == nullptr
        || host_is_first_field == nullptr || host_field_offsets == nullptr
        || field_count < 0) {
        return impl_->fail("CUDA preview received an invalid device field batch.");
    }
    const size_t field_samples = static_cast<size_t>(impl_->format.output_line_len)
        * impl_->format.output_field_lines;
    for (int field = 0; field < field_count && !complete(); ++field) {
        ++impl_->fields_scanned;
        if (impl_->cancelled()) {
            return impl_->fail("CUDA preview encoding was cancelled.");
        }
        if (host_is_first_field[field] < 0) continue;
        if (raw_offset > std::numeric_limits<size_t>::max() - host_field_offsets[field]) {
            return impl_->fail("CUDA preview field position overflowed.");
        }
        const size_t file_location = raw_offset + host_field_offsets[field];
        if (file_location < impl_->settings.target_sample) continue;

        bool first_field = host_is_first_field[field] == 1;
        if (impl_->have_parity && first_field == impl_->last_parity) {
            first_field = !impl_->last_parity;
        }
        impl_->have_parity = true;
        impl_->last_parity = first_field;
        const int phase_id = host_field_phase_ids != nullptr
            ? host_field_phase_ids[field]
            : 1;
        int paired_field = -1;
        if (impl_->cross_field_dropout) {
            const int step = first_field ? 1 : -1;
            for (int candidate = field + step;
                 candidate >= 0 && candidate < field_count;
                 candidate += step) {
                if (host_is_first_field[candidate] < 0) continue;
                const bool candidate_first = host_is_first_field[candidate] == 1;
                if (candidate_first != first_field) {
                    paired_field = candidate;
                    break;
                }
            }
        }
        const size_t dropout_base = static_cast<size_t>(field)
            * MAX_DROPOUTS_PER_FIELD;
        const size_t paired_dropout_base = paired_field >= 0
            ? static_cast<size_t>(paired_field) * MAX_DROPOUTS_PER_FIELD
            : 0;
        const int paired_phase_id = paired_field >= 0
            && host_field_phase_ids != nullptr
                ? host_field_phase_ids[paired_field]
                : 1;
        if (!impl_->render_field(
                d_luma + static_cast<size_t>(field) * field_samples,
                d_chroma + static_cast<size_t>(field) * field_samples,
                paired_field >= 0
                    ? d_luma + static_cast<size_t>(paired_field) * field_samples
                    : nullptr,
                paired_field >= 0
                    ? d_chroma + static_cast<size_t>(paired_field) * field_samples
                    : nullptr,
                d_dropout_lines != nullptr ? d_dropout_lines + dropout_base : nullptr,
                d_dropout_starts != nullptr ? d_dropout_starts + dropout_base : nullptr,
                d_dropout_ends != nullptr ? d_dropout_ends + dropout_base : nullptr,
                d_dropout_counts != nullptr ? d_dropout_counts + field : nullptr,
                paired_field >= 0 && d_dropout_lines != nullptr
                    ? d_dropout_lines + paired_dropout_base
                    : nullptr,
                paired_field >= 0 && d_dropout_starts != nullptr
                    ? d_dropout_starts + paired_dropout_base
                    : nullptr,
                paired_field >= 0 && d_dropout_ends != nullptr
                    ? d_dropout_ends + paired_dropout_base
                    : nullptr,
                paired_field >= 0 && d_dropout_counts != nullptr
                    ? d_dropout_counts + paired_field
                    : nullptr,
                first_field,
                phase_id,
                paired_field >= 0 && host_is_first_field[paired_field] == 1,
                paired_phase_id)
            || !impl_->encode_current_surface()) {
            return false;
        }
    }
    return true;
}

bool CudaPreviewOutput::finalize()
{
    if (!impl_->opened) return impl_->error.empty();
    if (impl_->frames_encoded != impl_->settings.requested_frames) {
        std::ostringstream message;
        message << "CUDA preview encoded " << impl_->frames_encoded
            << " frames; expected " << impl_->settings.requested_frames << '.';
        return impl_->fail(message.str());
    }
    return impl_->finalize_encoder();
}

void CudaPreviewOutput::close()
{
    if (impl_ != nullptr) impl_->release();
}

bool CudaPreviewOutput::complete() const
{
    return impl_->frames_encoded >= impl_->settings.requested_frames;
}

uint32_t CudaPreviewOutput::frames_encoded() const
{
    return impl_->frames_encoded;
}

uint32_t CudaPreviewOutput::fields_scanned() const
{
    return impl_->fields_scanned;
}

uint64_t CudaPreviewOutput::encoded_bytes() const
{
    return impl_->encoded_bytes;
}

const std::string& CudaPreviewOutput::error() const
{
    return impl_->error;
}
