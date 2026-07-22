#ifndef VHSDECODE_CUDA_H
#define VHSDECODE_CUDA_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(VHSDECODE_CUDA_BUILD_DLL)
#    define VHSDECODE_CUDA_API __declspec(dllexport)
#  else
#    define VHSDECODE_CUDA_API __declspec(dllimport)
#  endif
#  define VHSDECODE_CUDA_CALL __cdecl
#else
#  define VHSDECODE_CUDA_API __attribute__((visibility("default")))
#  define VHSDECODE_CUDA_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define VHSDECODE_CUDA_ABI_VERSION 1u
#define VHSDECODE_CUDA_STREAM_COUNT 2u
#define VHSDECODE_CUDA_DEVICE_FLAG_FP64 0x00000001u
#define VHSDECODE_CUDA_DEVICE_FLAG_CONCURRENT_COPY_COMPUTE 0x00000002u
#define VHSDECODE_CUDA_DEVICE_FLAG_MEMORY_INFO 0x00000004u

/* ABI v1 publishes cudaMemGetInfo's free-byte count through two formerly
 * reserved 32-bit device-info words. Older ABI-v1 components leave the flag
 * clear and these words at zero, so the structure size remains unchanged. */
#define VHSDECODE_CUDA_DEVICE_INFO_FREE_MEMORY_LOW_RESERVED_INDEX 0u
#define VHSDECODE_CUDA_DEVICE_INFO_FREE_MEMORY_HIGH_RESERVED_INDEX 1u

#define VHSDECODE_CUDA_CAP_RF_BATCH_TWO_STAGE UINT64_C(0x0000000000000001)
#define VHSDECODE_CUDA_CAP_RF_HIGH_PASS UINT64_C(0x0000000000000002)
#define VHSDECODE_CUDA_CAP_RF_MTF UINT64_C(0x0000000000000004)
#define VHSDECODE_CUDA_CAP_RF_ANALYTIC UINT64_C(0x0000000000000008)
#define VHSDECODE_CUDA_CAP_RF_ENVELOPE UINT64_C(0x0000000000000010)
#define VHSDECODE_CUDA_CAP_RF_CONJUGATE_DEMOD UINT64_C(0x0000000000000020)
#define VHSDECODE_CUDA_CAP_RF_VIDEO UINT64_C(0x0000000000000040)
#define VHSDECODE_CUDA_CAP_RF_VIDEO_LOW_PASS UINT64_C(0x0000000000000080)
#define VHSDECODE_CUDA_CAP_RF_MODE_STANDARD UINT64_C(0x0000000000000100)
#define VHSDECODE_CUDA_CAP_RF_MODE_VHS_RUST UINT64_C(0x0000000000000200)
#define VHSDECODE_CUDA_CAP_RF_MODE_CVBS UINT64_C(0x0000000000000400)
#define VHSDECODE_CUDA_CAP_VHS_RUST_DEMOD_KERNEL UINT64_C(0x0000000000000800)
#define VHSDECODE_CUDA_CAP_LD_EFM_FREQUENCY UINT64_C(0x0000000000001000)
#define VHSDECODE_CUDA_CAP_LD_ANALOG_AUDIO_FREQUENCY UINT64_C(0x0000000000002000)

#define VHSDECODE_CUDA_RF_OUTPUT_HIGH_PASS 0x00000001u
#define VHSDECODE_CUDA_RF_OUTPUT_ANALYTIC 0x00000002u
#define VHSDECODE_CUDA_RF_OUTPUT_ENVELOPE 0x00000004u
#define VHSDECODE_CUDA_RF_OUTPUT_DEMOD_RAW 0x00000008u
#define VHSDECODE_CUDA_RF_OUTPUT_VIDEO 0x00000010u
#define VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS 0x00000020u
#define VHSDECODE_CUDA_RF_APPLY_MTF 0x00000040u
#define VHSDECODE_CUDA_RF_OUTPUT_LD_EFM 0x00000080u
#define VHSDECODE_CUDA_RF_OUTPUT_LD_ANALOG_AUDIO 0x00000100u
#define VHSDECODE_CUDA_RF_ALL_FLAGS 0x000001ffu

/* ABI v1 keeps vhsdecode_cuda_rf_batch_job at 232 bytes. When either LD
 * output flag is present, reserved[0] contains the uintptr_t bit pattern of a
 * vhsdecode_cuda_ld_frequency_options pointer. The pointed-to structure and
 * every host buffer remain owned by the caller and are retained only for the
 * duration of the synchronous RF batch call. */
#define VHSDECODE_CUDA_RF_LD_OPTIONS_RESERVED_INDEX 0u

typedef enum vhsdecode_cuda_status {
    VHSDECODE_CUDA_SUCCESS = 0,
    VHSDECODE_CUDA_INVALID_ARGUMENT = 1,
    VHSDECODE_CUDA_ABI_MISMATCH = 2,
    VHSDECODE_CUDA_UNAVAILABLE = 3,
    VHSDECODE_CUDA_DEVICE_NOT_FOUND = 4,
    VHSDECODE_CUDA_DEVICE_UNSUPPORTED = 5,
    VHSDECODE_CUDA_ALLOCATION_FAILED = 6,
    VHSDECODE_CUDA_CUDA_ERROR = 7,
    VHSDECODE_CUDA_CUFFT_ERROR = 8,
    VHSDECODE_CUDA_SELF_TEST_FAILED = 9,
    VHSDECODE_CUDA_BUFFER_TOO_SMALL = 10,
    VHSDECODE_CUDA_NOT_SUPPORTED = 11,
    VHSDECODE_CUDA_INTERNAL_ERROR = 12
} vhsdecode_cuda_status;

typedef enum vhsdecode_cuda_fft_direction {
    VHSDECODE_CUDA_FFT_FORWARD = -1,
    VHSDECODE_CUDA_FFT_INVERSE = 1
} vhsdecode_cuda_fft_direction;

typedef enum vhsdecode_cuda_rf_mode {
    VHSDECODE_CUDA_RF_MODE_STANDARD_CONJUGATE = 0,
    VHSDECODE_CUDA_RF_MODE_VHS_RUST_APPROXIMATION = 1,
    VHSDECODE_CUDA_RF_MODE_CVBS = 2
} vhsdecode_cuda_rf_mode;

#pragma pack(push, 8)

typedef struct vhsdecode_cuda_complex64 {
    double real;
    double imag;
} vhsdecode_cuda_complex64;

/* Optional LD frequency-domain branches attached through RF job reserved[0].
 * efm_filter contains sample_count / 2 + 1 positive-frequency FP64 bins. Its
 * omitted negative-frequency half is defined to be zero, matching the current
 * one-sided LD EFM filter; efm_output contains sample_count real FP64 samples
 * per batch before CPU clipping to int16.
 *
 * Each analog-audio filter contains its channel's audio_*_bin_count FP64
 * values, in the same order as DecodeFilterSetBuilder.SliceSpectrum followed
 * by the stage-1 filter. Outputs are normalized complex inverse-FFT samples,
 * block-major, for CPU conjugate-product unwrap, offset addition, and phase 2.
 * A slice must stay in the positive R2C half-spectrum, have a power-of-two even
 * bin count, and satisfy low_bin + bin_count / 2 <= sample_count / 2. */
typedef struct vhsdecode_cuda_ld_frequency_options {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t audio_left_low_bin;
    uint32_t audio_left_bin_count;
    uint32_t audio_right_low_bin;
    uint32_t audio_right_bin_count;
    uint32_t reserved32[2];
    const vhsdecode_cuda_complex64* efm_filter;
    const vhsdecode_cuda_complex64* audio_left_filter;
    const vhsdecode_cuda_complex64* audio_right_filter;
    double* efm_output;
    vhsdecode_cuda_complex64* audio_left_output;
    vhsdecode_cuda_complex64* audio_right_output;
    uint64_t reserved[8];
} vhsdecode_cuda_ld_frequency_options;

typedef struct vhsdecode_cuda_device_info {
    uint32_t struct_size;
    int32_t ordinal;
    uint32_t flags;
    int32_t compute_capability_major;
    int32_t compute_capability_minor;
    int32_t driver_version;
    int32_t runtime_version;
    int32_t cufft_version;
    uint64_t total_global_memory;
    char name[256];
    uint32_t reserved[8];
} vhsdecode_cuda_device_info;

typedef struct vhsdecode_cuda_self_test_metrics {
    uint32_t struct_size;
    uint32_t passed;
    double max_abs_error;
    double nrmse;
    uint64_t sample_count;
    int32_t cuda_status;
    uint32_t reserved[7];
} vhsdecode_cuda_self_test_metrics;

/* One host-facing submission for the common RF two-stage graph. Every filter
 * contains sample_count / 2 + 1 complex FP64 bins and is reused across the
 * batch. Only pointers required by flags need to be non-null. ANALYTIC requires
 * both analytic output pointers. demod_raw is conjugate-product phase scaled
 * into Hz; CPU code retains final clipping and state decisions.
 * demod_phase_scale must be sample_rate_hz / (2*pi). In CVBS mode, the
 * reconstructed input is mapped as value * cvbs_raw_scale + cvbs_raw_offset;
 * use 1 and 0 respectively when auto-sync defers that mapping to the CPU. */
typedef struct vhsdecode_cuda_rf_batch_job {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t sample_count;
    uint32_t batch_count;
    uint32_t stream_index;
    uint32_t mode;
    double demod_phase_scale;
    double cvbs_raw_scale;
    double cvbs_raw_offset;
    const double* input;
    const vhsdecode_cuda_complex64* rf_video_filter;
    const vhsdecode_cuda_complex64* rf_high_pass_filter;
    const vhsdecode_cuda_complex64* mtf_filter;
    const vhsdecode_cuda_complex64* demod_video_filter;
    const vhsdecode_cuda_complex64* demod_video_low_pass_filter;
    const vhsdecode_cuda_complex64* previous_analytic_per_batch;
    vhsdecode_cuda_complex64* last_analytic_per_batch;
    double* rf_high_pass_output;
    double* analytic_real_output;
    double* analytic_imag_output;
    double* envelope_output;
    double* demod_raw_output;
    double* video_output;
    double* video_low_pass_output;
    uint64_t reserved[8];
} vhsdecode_cuda_rf_batch_job;

#pragma pack(pop)

typedef struct vhsdecode_cuda_context vhsdecode_cuda_context;
typedef struct vhsdecode_cuda_buffer vhsdecode_cuda_buffer;

/* ABI, discovery, lifetime, and diagnostics. */
VHSDECODE_CUDA_API uint32_t VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_abi_version(void);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_device_count(int32_t* device_count);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_device_info(int32_t device_ordinal,
                               vhsdecode_cuda_device_info* device_info);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_create(int32_t device_ordinal, vhsdecode_cuda_context** context);

VHSDECODE_CUDA_API void VHSDECODE_CUDA_CALL
vhsdecode_cuda_destroy(vhsdecode_cuda_context* context);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_self_test(vhsdecode_cuda_context* context,
                         vhsdecode_cuda_self_test_metrics* metrics);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_last_error(vhsdecode_cuda_context* context,
                              char* destination,
                              uint64_t destination_size);

VHSDECODE_CUDA_API const char* VHSDECODE_CUDA_CALL
vhsdecode_cuda_status_string(vhsdecode_cuda_status status);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_capabilities(vhsdecode_cuda_context* context,
                                uint64_t* capabilities);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_rf_batch_execute(vhsdecode_cuda_context* context,
                                const vhsdecode_cuda_rf_batch_job* job);

/* Pinned host and opaque device allocations. Device buffers retain their
 * creating context and cannot be shared across contexts. */
VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_host_alloc(vhsdecode_cuda_context* context,
                          uint64_t byte_count,
                          void** memory);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_host_free(vhsdecode_cuda_context* context, void* memory);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_create(vhsdecode_cuda_context* context,
                             uint64_t byte_count,
                             vhsdecode_cuda_buffer** buffer);

VHSDECODE_CUDA_API void VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_destroy(vhsdecode_cuda_buffer* buffer);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_upload(vhsdecode_cuda_context* context,
                             vhsdecode_cuda_buffer* destination,
                             uint64_t destination_offset,
                             const void* source,
                             uint64_t byte_count,
                             uint32_t stream_index);

/* Download waits for its stream before returning, so destination may be
 * ordinary managed or pageable host memory. */
VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_download(vhsdecode_cuda_context* context,
                               const vhsdecode_cuda_buffer* source,
                               uint64_t source_offset,
                               void* destination,
                               uint64_t byte_count,
                               uint32_t stream_index);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_stream_synchronize(vhsdecode_cuda_context* context,
                                  uint32_t stream_index);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_synchronize(vhsdecode_cuda_context* context);

/* Batched FP64 transforms. R2C output and C2R input contain
 * (sample_count / 2 + 1) complex values per batch. Inverse transforms are
 * unnormalised unless normalize_inverse is nonzero. */
VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_fft_r2c(vhsdecode_cuda_context* context,
                       const vhsdecode_cuda_buffer* input,
                       vhsdecode_cuda_buffer* output,
                       uint32_t sample_count,
                       uint32_t batch_count,
                       uint32_t stream_index);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_fft_c2r(vhsdecode_cuda_context* context,
                       const vhsdecode_cuda_buffer* input,
                       vhsdecode_cuda_buffer* output,
                       uint32_t sample_count,
                       uint32_t batch_count,
                       uint32_t normalize_inverse,
                       uint32_t stream_index);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_fft_c2c(vhsdecode_cuda_context* context,
                       const vhsdecode_cuda_buffer* input,
                       vhsdecode_cuda_buffer* output,
                       uint32_t sample_count,
                       uint32_t batch_count,
                       vhsdecode_cuda_fft_direction direction,
                       uint32_t normalize_inverse,
                       uint32_t stream_index);

/* Multiply each spectrum by a complex filter. filter_batch_stride == 0 reuses
 * one filter for every batch; otherwise it is the filter stride in elements. */
VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_frequency_multiply(vhsdecode_cuda_context* context,
                                  vhsdecode_cuda_buffer* spectrum,
                                  const vhsdecode_cuda_buffer* filter,
                                  uint64_t bins_per_batch,
                                  uint32_t batch_count,
                                  uint64_t filter_batch_stride,
                                  uint32_t stream_index);

/* Convert an R2C half-spectrum into the half-spectrum of its real Hilbert
 * transform: DC and Nyquist are zero, positive bins are multiplied by -i. */
VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_hilbert_r2c(vhsdecode_cuda_context* context,
                           vhsdecode_cuda_buffer* spectrum,
                           uint32_t sample_count,
                           uint32_t batch_count,
                           uint32_t stream_index);

VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_envelope(vhsdecode_cuda_context* context,
                        const vhsdecode_cuda_buffer* real_samples,
                        const vhsdecode_cuda_buffer* hilbert_samples,
                        vhsdecode_cuda_buffer* output,
                        uint64_t sample_count,
                        uint32_t stream_index);

/* Calculate atan2(imag(conj(previous) * current), real(...)) per sample.
 * previous_per_batch and last_per_batch may be null. Without previous input,
 * the first phase of each batch is zero. */
VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_conjugate_product_phase(
    vhsdecode_cuda_context* context,
    const vhsdecode_cuda_buffer* analytic_samples,
    vhsdecode_cuda_buffer* phase_output,
    uint32_t samples_per_batch,
    uint32_t batch_count,
    const vhsdecode_cuda_buffer* previous_per_batch,
    vhsdecode_cuda_buffer* last_per_batch,
    uint32_t stream_index);

/* Exact algorithmic shape of PortedMath's VHS Rust demodulator: FP64 analytic
 * inputs are converted to FP32, the polynomial atan approximation and wrapped
 * difference run in FP32, and results are widened to FP64. Each batch starts
 * with output[0] == 0. This primitive does not imply support for the complete
 * VHS RF graph (SOS envelope/high-boost/diff-repair remain capability-gated). */
VHSDECODE_CUDA_API vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_vhs_rust_demod(vhsdecode_cuda_context* context,
                              const vhsdecode_cuda_buffer* real_samples,
                              const vhsdecode_cuda_buffer* imaginary_samples,
                              vhsdecode_cuda_buffer* output,
                              uint32_t samples_per_batch,
                              uint32_t batch_count,
                              float frequency_hz,
                              uint32_t stream_index);

#ifdef __cplusplus
}
#endif

#endif
