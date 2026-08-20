#ifndef VHSDECODE_CUDA_FAST_H
#define VHSDECODE_CUDA_FAST_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(VHSDECODE_CUDA_FAST_EXPORTS)
#    define VHSDECODE_CUDA_FAST_API __declspec(dllexport)
#  else
#    define VHSDECODE_CUDA_FAST_API __declspec(dllimport)
#  endif
#  define VHSDECODE_CUDA_FAST_CALL __cdecl
#else
#  define VHSDECODE_CUDA_FAST_API __attribute__((visibility("default")))
#  define VHSDECODE_CUDA_FAST_CALL
#endif

#define VHSDECODE_CUDA_FAST_ABI_VERSION 0x00060000u
#define VHSDECODE_CUDA_FAST_NAME_CAPACITY 128u

#ifdef __cplusplus
extern "C" {
#endif

typedef enum vhsdecode_cuda_fast_status {
    VHSDECODE_CUDA_FAST_STATUS_OK = 0,
    VHSDECODE_CUDA_FAST_STATUS_NULL_POINTER = -20000,
    VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT = -20001,
    VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE = -20002,
    VHSDECODE_CUDA_FAST_STATUS_INPUT_ERROR = -20003,
    VHSDECODE_CUDA_FAST_STATUS_OUTPUT_ERROR = -20004,
    VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR = -20005,
    VHSDECODE_CUDA_FAST_STATUS_CANCELLED = -20006,
    VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR = -20007
} vhsdecode_cuda_fast_status;

typedef enum vhsdecode_cuda_fast_profile {
    VHSDECODE_CUDA_FAST_PROFILE_NTSC = 0,
    VHSDECODE_CUDA_FAST_PROFILE_PAL = 1,
    VHSDECODE_CUDA_FAST_PROFILE_PAL_M = 2
} vhsdecode_cuda_fast_profile;

typedef enum vhsdecode_cuda_fast_tape_speed {
    VHSDECODE_CUDA_FAST_TAPE_SPEED_SP = 0,
    VHSDECODE_CUDA_FAST_TAPE_SPEED_LP = 1,
    VHSDECODE_CUDA_FAST_TAPE_SPEED_EP = 2
} vhsdecode_cuda_fast_tape_speed;

typedef enum vhsdecode_cuda_fast_input_sample_format {
    VHSDECODE_CUDA_FAST_INPUT_FLOAT32 = 0,
    VHSDECODE_CUDA_FAST_INPUT_INT16 = 1
} vhsdecode_cuda_fast_input_sample_format;

typedef size_t (VHSDECODE_CUDA_FAST_CALL *vhsdecode_cuda_fast_read_callback)(
    void* user_data,
    void* destination,
    uint64_t sample_offset,
    size_t sample_count);

typedef int32_t (VHSDECODE_CUDA_FAST_CALL *vhsdecode_cuda_fast_cancel_callback)(
    void* user_data);

typedef int32_t (VHSDECODE_CUDA_FAST_CALL *vhsdecode_cuda_fast_bitstream_callback)(
    void* user_data,
    const uint8_t* data,
    size_t byte_count);

typedef struct vhsdecode_cuda_fast_preview_context vhsdecode_cuda_fast_preview_context;

typedef struct vhsdecode_cuda_fast_runtime_info_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t device_id;
    int32_t compute_major;
    int32_t compute_minor;
    int32_t multiprocessor_count;
    uint64_t total_vram_bytes;
    uint64_t free_vram_bytes;
    char device_name[VHSDECODE_CUDA_FAST_NAME_CAPACITY];
} vhsdecode_cuda_fast_runtime_info_v1;

typedef struct vhsdecode_cuda_fast_config_v1 {
    uint32_t struct_size;
    uint32_t profile;
    uint32_t tape_speed;
    int32_t device_id;
    double sample_rate_mhz;
    uint64_t total_samples;
    const char* output_base_utf8;
    int32_t overwrite;
    uint32_t input_sample_format;
    uint32_t maximum_output_fields;
    uint32_t device_decimation_factor;
    vhsdecode_cuda_fast_read_callback read_callback;
    vhsdecode_cuda_fast_cancel_callback cancel_callback;
    void* user_data;
} vhsdecode_cuda_fast_config_v1;

typedef struct vhsdecode_cuda_fast_result_v1 {
    uint32_t struct_size;
    uint32_t fields_written;
    uint32_t output_line_length;
    uint32_t output_field_lines;
    double elapsed_seconds;
} vhsdecode_cuda_fast_result_v1;

typedef struct vhsdecode_cuda_fast_preview_config_v1 {
    uint32_t struct_size;
    uint32_t profile;
    uint32_t tape_speed;
    int32_t device_id;
    double source_sample_rate_mhz;
    double decode_sample_rate_mhz;
    uint32_t output_width;
    uint32_t output_height;
    uint32_t frame_rate_numerator;
    uint32_t frame_rate_denominator;
    uint32_t constant_qp;
    uint32_t gop_length;
} vhsdecode_cuda_fast_preview_config_v1;

typedef struct vhsdecode_cuda_fast_preview_window_v1 {
    uint32_t struct_size;
    uint32_t input_sample_format;
    uint64_t total_source_samples;
    uint64_t target_source_sample;
    uint32_t requested_output_frames;
    uint32_t reserved;
    vhsdecode_cuda_fast_read_callback read_callback;
    vhsdecode_cuda_fast_cancel_callback cancel_callback;
    vhsdecode_cuda_fast_bitstream_callback bitstream_callback;
    void* user_data;
} vhsdecode_cuda_fast_preview_window_v1;

typedef struct vhsdecode_cuda_fast_preview_result_v1 {
    uint32_t struct_size;
    uint32_t frames_encoded;
    uint32_t fields_scanned;
    uint32_t reserved;
    uint64_t encoded_bytes;
    double elapsed_seconds;
} vhsdecode_cuda_fast_preview_result_v1;

VHSDECODE_CUDA_FAST_API uint32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_get_abi_version(void);

VHSDECODE_CUDA_FAST_API int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_get_runtime_info(
    int32_t device_id,
    vhsdecode_cuda_fast_runtime_info_v1* info);

VHSDECODE_CUDA_FAST_API int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_run(
    const vhsdecode_cuda_fast_config_v1* config,
    vhsdecode_cuda_fast_result_v1* result);

VHSDECODE_CUDA_FAST_API int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_preview_create(
    const vhsdecode_cuda_fast_preview_config_v1* config,
    vhsdecode_cuda_fast_preview_context** context);

VHSDECODE_CUDA_FAST_API int32_t VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_preview_decode_window(
    vhsdecode_cuda_fast_preview_context* context,
    const vhsdecode_cuda_fast_preview_window_v1* window,
    vhsdecode_cuda_fast_preview_result_v1* result);

VHSDECODE_CUDA_FAST_API void VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_preview_destroy(
    vhsdecode_cuda_fast_preview_context* context);

VHSDECODE_CUDA_FAST_API const char* VHSDECODE_CUDA_FAST_CALL
vhsdecode_cuda_fast_get_last_error(void);

#ifdef __cplusplus
}
#endif

#endif
