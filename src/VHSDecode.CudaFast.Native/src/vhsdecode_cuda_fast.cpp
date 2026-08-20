#include "vhsdecode_cuda_fast.h"

#include "cancellation_latch.h"
#include "format/video_format.h"
#include "gpu/device.h"
#include "io/raw_reader.h"
#include "io/tbc_writer.h"
#include "pipeline/pipeline.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <exception>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>

namespace {

thread_local std::string last_error;

int32_t fail(int32_t status, const char* message) {
    last_error = message == nullptr ? "Unknown CUDA-fast error." : message;
    return status;
}

VideoProfile map_profile(uint32_t profile) {
    switch (profile) {
        case VHSDECODE_CUDA_FAST_PROFILE_NTSC:
            return VideoProfile::NTSC_525_60_VHS;
        case VHSDECODE_CUDA_FAST_PROFILE_PAL:
            return VideoProfile::PAL_625_50_VHS;
        case VHSDECODE_CUDA_FAST_PROFILE_PAL_M:
            return VideoProfile::MPAL_525_60_VHS;
        default:
            throw std::invalid_argument("Unsupported CUDA-fast video profile.");
    }
}

TapeSpeed map_tape_speed(uint32_t speed) {
    switch (speed) {
        case VHSDECODE_CUDA_FAST_TAPE_SPEED_SP:
            return TapeSpeed::SP;
        case VHSDECODE_CUDA_FAST_TAPE_SPEED_LP:
            return TapeSpeed::LP;
        case VHSDECODE_CUDA_FAST_TAPE_SPEED_EP:
            return TapeSpeed::EP;
        default:
            throw std::invalid_argument("Unsupported CUDA-fast tape speed.");
    }
}

struct CallbackContext {
    const vhsdecode_cuda_fast_config_v1* config;
    vhsdecode::cuda_fast::detail::CancellationLatch cancellation;
};

struct PreviewCallbackContext {
    const vhsdecode_cuda_fast_preview_window_v1* window = nullptr;
    uint64_t source_base = 0;
    vhsdecode::cuda_fast::detail::CancellationLatch cancellation;
    std::atomic_bool sink_failed{false};
};

bool cancellation_requested(CallbackContext& context) {
    if (context.config->cancel_callback == nullptr) {
        return context.cancellation.requested();
    }
    return context.cancellation.poll([&context]() {
        return context.config->cancel_callback(context.config->user_data) != 0;
    });
}

size_t callback_read(
    void* user_data,
    void* destination,
    uint64_t sample_offset,
    size_t sample_count) {
    auto& context = *static_cast<CallbackContext*>(user_data);
    if (cancellation_requested(context)) {
        return 0;
    }
    return context.config->read_callback(
        context.config->user_data,
        destination,
        sample_offset,
        sample_count);
}

bool preview_cancellation_requested(PreviewCallbackContext& context) {
    if (context.window->cancel_callback == nullptr) {
        return context.cancellation.requested();
    }
    return context.cancellation.poll([&context]() {
        return context.window->cancel_callback(context.window->user_data) != 0;
    });
}

size_t preview_callback_read(
    void* user_data,
    void* destination,
    uint64_t sample_offset,
    size_t sample_count) {
    auto& context = *static_cast<PreviewCallbackContext*>(user_data);
    if (preview_cancellation_requested(context)
        || sample_offset > std::numeric_limits<uint64_t>::max() - context.source_base) {
        return 0;
    }
    return context.window->read_callback(
        context.window->user_data,
        destination,
        context.source_base + sample_offset,
        sample_count);
}

int32_t preview_callback_cancel(void* user_data) {
    auto& context = *static_cast<PreviewCallbackContext*>(user_data);
    return preview_cancellation_requested(context) ? 1 : 0;
}

int32_t preview_callback_bitstream(
    void* user_data,
    const uint8_t* data,
    size_t byte_count) {
    auto& context = *static_cast<PreviewCallbackContext*>(user_data);
    if (preview_cancellation_requested(context)) return -1;
    const int32_t status = context.window->bitstream_callback(
        context.window->user_data,
        data,
        byte_count);
    if (status != 0) {
        context.sink_failed.store(true, std::memory_order_release);
    }
    return status;
}

void fill_runtime_info(
    const GPUDevice& device,
    vhsdecode_cuda_fast_runtime_info_v1& info) {
    info.abi_version = VHSDECODE_CUDA_FAST_ABI_VERSION;
    info.device_id = device.device_id;
    info.compute_major = device.compute_major;
    info.compute_minor = device.compute_minor;
    info.multiprocessor_count = device.sm_count;
    info.total_vram_bytes = static_cast<uint64_t>(device.vram_total);
    info.free_vram_bytes = static_cast<uint64_t>(device.vram_free);
    std::strncpy(info.device_name, device.name, sizeof(info.device_name) - 1);
    info.device_name[sizeof(info.device_name) - 1] = '\0';
}

bool device_is_supported(const GPUDevice& device) {
    return device.compute_major > 7
        || (device.compute_major == 7 && device.compute_minor >= 5);
}

}  // namespace

struct vhsdecode_cuda_fast_preview_context {
    explicit vhsdecode_cuda_fast_preview_context(
        VideoProfile profile,
        TapeSpeed tape_speed,
        double decode_sample_rate_mhz,
        uint32_t output_width,
        uint32_t output_height,
        uint32_t frame_rate_numerator,
        uint32_t frame_rate_denominator,
        uint32_t constant_qp,
        uint32_t gop_length)
        : format(profile, decode_sample_rate_mhz, tape_speed),
          output_width(output_width),
          output_height(output_height),
          frame_rate_numerator(frame_rate_numerator),
          frame_rate_denominator(frame_rate_denominator),
          constant_qp(constant_qp),
          gop_length(gop_length) {
    }

    GPUDevice device;
    VideoFormat format;
    RawReader reader;
    TBCWriter writer;
    std::unique_ptr<Pipeline> pipeline;
    std::mutex gate;
    int input_sample_format = -1;
    uint32_t output_width;
    uint32_t output_height;
    uint32_t frame_rate_numerator;
    uint32_t frame_rate_denominator;
    uint32_t constant_qp;
    uint32_t gop_length;
};

extern "C" uint32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_get_abi_version(void) {
    return VHSDECODE_CUDA_FAST_ABI_VERSION;
}

extern "C" int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_get_runtime_info(
    int32_t device_id,
    vhsdecode_cuda_fast_runtime_info_v1* info) {
    if (info == nullptr) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_NULL_POINTER,
            "CUDA-fast runtime-info pointer was null.");
    }
    if (info->struct_size != sizeof(vhsdecode_cuda_fast_runtime_info_v1)) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
            "CUDA-fast runtime-info structure size did not match ABI v6.");
    }

    try {
        GPUDevice device;
        if (!device.init(device_id)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE,
                "No usable CUDA device was found.");
        }
        if (!device_is_supported(device)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE,
                "CUDA-fast requires NVIDIA compute capability 7.5 or newer.");
        }
        fill_runtime_info(device, *info);
        last_error.clear();
        return VHSDECODE_CUDA_FAST_STATUS_OK;
    } catch (const std::exception& error) {
        return fail(VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR, error.what());
    } catch (...) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR,
            "Unknown exception while probing the CUDA-fast runtime.");
    }
}

extern "C" int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_run(
    const vhsdecode_cuda_fast_config_v1* config,
    vhsdecode_cuda_fast_result_v1* result) {
    if (config == nullptr || result == nullptr) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_NULL_POINTER,
            "CUDA-fast configuration or result pointer was null.");
    }
    if (config->struct_size != sizeof(vhsdecode_cuda_fast_config_v1)
        || result->struct_size != sizeof(vhsdecode_cuda_fast_result_v1)) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
            "CUDA-fast structure size did not match ABI v6.");
    }
    const bool decode_rate_matches_decimation =
        (config->device_decimation_factor == 1
            && (std::abs(config->sample_rate_mhz - 40.0) <= 0.0000005
                || std::abs(config->sample_rate_mhz - 20.0) <= 0.0000005))
        || (config->device_decimation_factor == 2
            && std::abs(config->sample_rate_mhz - 20.0) <= 0.0000005);
    if (config->read_callback == nullptr
        || config->output_base_utf8 == nullptr
        || config->output_base_utf8[0] == '\0'
        || config->total_samples == 0
        || config->total_samples > std::numeric_limits<size_t>::max()
        || config->input_sample_format > VHSDECODE_CUDA_FAST_INPUT_INT16
        || (config->device_decimation_factor != 1
            && config->device_decimation_factor != 2)
        || !std::isfinite(config->sample_rate_mhz)
        || config->sample_rate_mhz < 8.0
        || config->sample_rate_mhz > 100.0
        || !decode_rate_matches_decimation) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
            "CUDA-fast configuration contained an invalid callback, path, sample count, decode rate, or decimation factor.");
    }

    try {
        CallbackContext callback_context{config};
        if (cancellation_requested(callback_context)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CANCELLED,
                "CUDA-fast decode was cancelled before startup.");
        }

        GPUDevice device;
        if (!device.init(config->device_id)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE,
                "No usable CUDA device was found.");
        }
        if (!device_is_supported(device)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE,
                "CUDA-fast requires NVIDIA compute capability 7.5 or newer.");
        }
        device.print_info();

        const VideoFormat format(
            map_profile(config->profile),
            config->sample_rate_mhz,
            map_tape_speed(config->tape_speed));
        RawReader reader;
        if (!reader.open_callback(
                callback_read,
                &callback_context,
                static_cast<size_t>(config->total_samples),
                config->input_sample_format == VHSDECODE_CUDA_FAST_INPUT_INT16
                    ? RawReaderCallbackFormat::Int16
                    : RawReaderCallbackFormat::Float32)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_INPUT_ERROR,
                "CUDA-fast could not initialize its managed RF reader.");
        }
        if (!reader.set_device_decimation_factor(
                static_cast<int>(config->device_decimation_factor))) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_INPUT_ERROR,
                "CUDA-fast could not configure its RF decimation factor.");
        }

        TBCWriter writer;
        if (!writer.open(
                config->output_base_utf8,
                format,
                config->overwrite != 0)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_OUTPUT_ERROR,
                "CUDA-fast could not open its TBC output files.");
        }

        const auto started = std::chrono::steady_clock::now();
        bool decoded = false;
        {
            Pipeline pipeline(
                device,
                format,
                reader,
                writer,
                config->maximum_output_fields);
            decoded = pipeline.run();
        }
        const auto completed = std::chrono::steady_clock::now();
        writer.close();

        // A managed read can observe cancellation after its entry poll and
        // return a short read. Refresh the external token after the pipeline
        // has joined its prefetch worker before classifying that failure.
        if (cancellation_requested(callback_context)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CANCELLED,
                "CUDA-fast decode was cancelled.");
        }
        if (!decoded) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR,
                "The CUDA-fast full signal pipeline did not complete.");
        }

        result->fields_written = static_cast<uint32_t>(writer.fields_written());
        result->output_line_length = static_cast<uint32_t>(format.output_line_len);
        result->output_field_lines = static_cast<uint32_t>(format.output_field_lines);
        result->elapsed_seconds =
            std::chrono::duration<double>(completed - started).count();
        last_error.clear();
        return VHSDECODE_CUDA_FAST_STATUS_OK;
    } catch (const std::bad_alloc&) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR,
            "CUDA-fast ran out of host memory.");
    } catch (const std::exception& error) {
        return fail(VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR, error.what());
    } catch (...) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR,
            "Unknown exception in the CUDA-fast full signal pipeline.");
    }
}

extern "C" int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_preview_create(
    const vhsdecode_cuda_fast_preview_config_v1* config,
    vhsdecode_cuda_fast_preview_context** context) {
    if (config == nullptr || context == nullptr) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_NULL_POINTER,
            "CUDA-fast preview configuration or context pointer was null.");
    }
    *context = nullptr;
    if (config->struct_size != sizeof(vhsdecode_cuda_fast_preview_config_v1)) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
            "CUDA-fast preview configuration size did not match ABI v6.");
    }
    const bool ntsc = config->profile == VHSDECODE_CUDA_FAST_PROFILE_NTSC;
    const bool pal = config->profile == VHSDECODE_CUDA_FAST_PROFILE_PAL;
    const bool dimensions_valid = ntsc
        ? config->output_width == 640 && config->output_height == 480
        : pal && config->output_width == 768 && config->output_height == 576;
    const bool frame_rate_valid = ntsc
        ? config->frame_rate_numerator == 60000
            && config->frame_rate_denominator == 1001
        : pal && config->frame_rate_numerator == 50
            && config->frame_rate_denominator == 1;
    if ((!ntsc && !pal)
        || config->tape_speed > VHSDECODE_CUDA_FAST_TAPE_SPEED_EP
        || !std::isfinite(config->source_sample_rate_mhz)
        || !std::isfinite(config->decode_sample_rate_mhz)
        || std::abs(config->source_sample_rate_mhz - 40.0) > 1e-9
        || std::abs(config->decode_sample_rate_mhz - 20.0) > 1e-9
        || !dimensions_valid
        || !frame_rate_valid
        || config->constant_qp > 51
        || config->gop_length == 0) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
            "CUDA-fast preview requires VHS PAL/NTSC, 40-to-20 MSPS, the native preview geometry/rate, and a valid QP/GOP.");
    }

    try {
        auto candidate = std::make_unique<vhsdecode_cuda_fast_preview_context>(
            map_profile(config->profile),
            map_tape_speed(config->tape_speed),
            config->decode_sample_rate_mhz,
            config->output_width,
            config->output_height,
            config->frame_rate_numerator,
            config->frame_rate_denominator,
            config->constant_qp,
            config->gop_length);
        if (!candidate->device.init(config->device_id)
            || !device_is_supported(candidate->device)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE,
                "CUDA-fast preview requires an NVIDIA GPU with compute capability 7.5 or newer.");
        }
        candidate->pipeline = std::make_unique<Pipeline>(
            candidate->device,
            candidate->format,
            candidate->reader,
            candidate->writer,
            0);
        *context = candidate.release();
        last_error.clear();
        return VHSDECODE_CUDA_FAST_STATUS_OK;
    } catch (const std::bad_alloc&) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR,
            "CUDA-fast preview ran out of host memory during initialization.");
    } catch (const std::exception& error) {
        return fail(VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR, error.what());
    } catch (...) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR,
            "Unknown exception while initializing CUDA-fast preview.");
    }
}

extern "C" int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_preview_decode_window(
    vhsdecode_cuda_fast_preview_context* context,
    const vhsdecode_cuda_fast_preview_window_v1* window,
    vhsdecode_cuda_fast_preview_result_v1* result) {
    if (context == nullptr || window == nullptr || result == nullptr) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_NULL_POINTER,
            "CUDA-fast preview context, window, or result pointer was null.");
    }
    if (window->struct_size != sizeof(vhsdecode_cuda_fast_preview_window_v1)
        || result->struct_size != sizeof(vhsdecode_cuda_fast_preview_result_v1)
        || window->read_callback == nullptr
        || window->bitstream_callback == nullptr
        || window->input_sample_format > VHSDECODE_CUDA_FAST_INPUT_INT16
        || window->total_source_samples == 0
        || window->total_source_samples > std::numeric_limits<size_t>::max()
        || window->target_source_sample >= window->total_source_samples
        || window->requested_output_frames == 0) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
            "CUDA-fast preview window contained an invalid callback, sample range, format, or frame count.");
    }

    std::lock_guard<std::mutex> lock(context->gate);
    try {
        if (context->input_sample_format >= 0
            && context->input_sample_format != static_cast<int>(window->input_sample_format)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
                "CUDA-fast preview cannot change RF callback format within a persistent session.");
        }
        context->input_sample_format = static_cast<int>(window->input_sample_format);

        PreviewCallbackContext callback_context{window};
        if (preview_cancellation_requested(callback_context)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CANCELLED,
                "CUDA-fast preview window was cancelled before startup.");
        }

        constexpr double kPrerollSeconds = 0.12;
        const uint64_t preroll_samples = static_cast<uint64_t>(std::llround(
            40'000'000.0 * kPrerollSeconds));
        callback_context.source_base = window->target_source_sample > preroll_samples
            ? window->target_source_sample - preroll_samples
            : 0;
        const uint64_t source_count =
            window->total_source_samples - callback_context.source_base;
        if (!context->reader.open_callback(
                preview_callback_read,
                &callback_context,
                static_cast<size_t>(source_count),
                window->input_sample_format == VHSDECODE_CUDA_FAST_INPUT_INT16
                    ? RawReaderCallbackFormat::Int16
                    : RawReaderCallbackFormat::Float32)
            || !context->reader.set_device_decimation_factor(2)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_INPUT_ERROR,
                "CUDA-fast preview could not initialize its 40-MSPS managed RF window.");
        }

        CudaPreviewOutputSettings output_settings;
        output_settings.device_id = context->device.device_id;
        output_settings.width = context->output_width;
        output_settings.height = context->output_height;
        output_settings.frame_rate_numerator = context->frame_rate_numerator;
        output_settings.frame_rate_denominator = context->frame_rate_denominator;
        output_settings.constant_qp = context->constant_qp;
        output_settings.gop_length = context->gop_length;
        output_settings.target_sample = static_cast<size_t>(
            (window->target_source_sample - callback_context.source_base) / 2);
        output_settings.requested_frames = window->requested_output_frames;
        output_settings.bitstream_callback = preview_callback_bitstream;
        output_settings.cancel_callback = preview_callback_cancel;
        output_settings.user_data = &callback_context;

        if (!context->writer.open_preview(context->format, output_settings)) {
            const std::string message = context->writer.preview_error();
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE,
                message.empty()
                    ? "CUDA-fast preview could not initialize direct CUDA/NVENC output."
                    : message.c_str());
        }

        const auto started = std::chrono::steady_clock::now();
        const bool decoded = context->pipeline->run();
        const auto completed = std::chrono::steady_clock::now();
        result->frames_encoded = context->writer.preview_frames_encoded();
        result->fields_scanned = context->writer.preview_fields_scanned();
        result->encoded_bytes = context->writer.preview_encoded_bytes();
        result->elapsed_seconds =
            std::chrono::duration<double>(completed - started).count();

        // A managed read can observe cancellation after its entry poll and
        // return a short read. Refresh the external token after the pipeline
        // has joined its prefetch worker before classifying that failure.
        if (preview_cancellation_requested(callback_context)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_CANCELLED,
                "CUDA-fast preview window was cancelled.");
        }
        if (callback_context.sink_failed.load(std::memory_order_acquire)) {
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_OUTPUT_ERROR,
                "The CUDA-fast preview H.264 sink rejected NVENC output.");
        }
        if (!decoded) {
            const std::string message = context->writer.preview_error();
            return fail(
                VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR,
                message.empty()
                    ? "The CUDA-fast preview signal pipeline did not complete."
                    : message.c_str());
        }

        last_error.clear();
        return VHSDECODE_CUDA_FAST_STATUS_OK;
    } catch (const std::bad_alloc&) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR,
            "CUDA-fast preview ran out of host memory.");
    } catch (const std::exception& error) {
        return fail(VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR, error.what());
    } catch (...) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR,
            "Unknown exception in CUDA-fast preview.");
    }
}

extern "C" void VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_preview_destroy(vhsdecode_cuda_fast_preview_context* context) {
    delete context;
}

extern "C" const char* VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_get_last_error(void) {
    return last_error.c_str();
}
