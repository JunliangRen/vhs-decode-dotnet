#include "vhsdecode_cuda_fast.h"

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
    bool cancelled = false;
};

bool cancellation_requested(CallbackContext& context) {
    if (context.config->cancel_callback == nullptr) {
        return false;
    }
    context.cancelled = context.cancelled
        || context.config->cancel_callback(context.config->user_data) != 0;
    return context.cancelled;
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
            "CUDA-fast runtime-info structure size did not match ABI v4.");
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
            "CUDA-fast structure size did not match ABI v4.");
    }
    if (config->read_callback == nullptr
        || config->output_base_utf8 == nullptr
        || config->output_base_utf8[0] == '\0'
        || config->total_samples == 0
        || config->total_samples > std::numeric_limits<size_t>::max()
        || config->input_sample_format > VHSDECODE_CUDA_FAST_INPUT_INT16
        || !std::isfinite(config->sample_rate_mhz)
        || config->sample_rate_mhz < 8.0
        || config->sample_rate_mhz > 100.0) {
        return fail(
            VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT,
            "CUDA-fast configuration contained an invalid callback, path, sample count, or sample rate.");
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

        if (callback_context.cancelled) {
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

extern "C" const char* VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_get_last_error(void) {
    return last_error.c_str();
}
