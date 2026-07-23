#ifndef VHSDECODE_IPP_H
#define VHSDECODE_IPP_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(VHSDECODE_IPP_EXPORTS)
#    define VHSDECODE_IPP_API __declspec(dllexport)
#  else
#    define VHSDECODE_IPP_API __declspec(dllimport)
#  endif
#  define VHSDECODE_IPP_CALL __cdecl
#else
#  define VHSDECODE_IPP_API __attribute__((visibility("default")))
#  define VHSDECODE_IPP_CALL
#endif

#define VHSDECODE_IPP_ABI_VERSION 0x00010000u
#define VHSDECODE_IPP_NAME_CAPACITY 64u
#define VHSDECODE_IPP_VERSION_CAPACITY 64u
#define VHSDECODE_IPP_BUILD_DATE_CAPACITY 32u
#define VHSDECODE_IPP_TARGET_CPU_CAPACITY 32u

#ifdef __cplusplus
extern "C" {
#endif

typedef enum vhsdecode_ipp_status {
    VHSDECODE_IPP_STATUS_OK = 0,
    VHSDECODE_IPP_STATUS_NULL_POINTER = -10000,
    VHSDECODE_IPP_STATUS_INVALID_ARGUMENT = -10001,
    VHSDECODE_IPP_STATUS_UNSUPPORTED_LENGTH = -10002,
    VHSDECODE_IPP_STATUS_BUFFER_TOO_SMALL = -10003,
    VHSDECODE_IPP_STATUS_OUT_OF_MEMORY = -10004,
    VHSDECODE_IPP_STATUS_INTERNAL_ERROR = -10005,
    VHSDECODE_IPP_STATUS_UNSUPPORTED_CPU = -10006
} vhsdecode_ipp_status;

typedef struct vhsdecode_ipp_complex64 {
    double real;
    double imag;
} vhsdecode_ipp_complex64;

/* One SOS row in SciPy/.NET order. a0 is normalized by the bridge. */
typedef struct vhsdecode_ipp_sos64_section {
    double b0;
    double b1;
    double b2;
    double a0;
    double a1;
    double a2;
} vhsdecode_ipp_sos64_section;

/*
 * Version 1 runtime information. The caller must set struct_size to
 * sizeof(vhsdecode_ipp_runtime_info_v1) before calling get_runtime_info.
 * All strings are UTF-8/ASCII, NUL-terminated, and stored inline.
 */
typedef struct vhsdecode_ipp_runtime_info_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t ipp_init_status;
    int32_t ipp_major;
    int32_t ipp_minor;
    int32_t ipp_update;
    int32_t ipp_build;
    uint32_t reserved0;
    uint64_t cpu_features;
    uint64_t enabled_cpu_features;
    char ipp_name[VHSDECODE_IPP_NAME_CAPACITY];
    char ipp_version[VHSDECODE_IPP_VERSION_CAPACITY];
    char ipp_build_date[VHSDECODE_IPP_BUILD_DATE_CAPACITY];
    char ipp_target_cpu[VHSDECODE_IPP_TARGET_CPU_CAPACITY];
} vhsdecode_ipp_runtime_info_v1;

typedef struct vhsdecode_ipp_fft64_context vhsdecode_ipp_fft64_context;
typedef struct vhsdecode_ipp_iir64_context vhsdecode_ipp_iir64_context;
typedef struct vhsdecode_ipp_sos64_context vhsdecode_ipp_sos64_context;

VHSDECODE_IPP_API uint32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_get_abi_version(void);

VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_get_runtime_info(vhsdecode_ipp_runtime_info_v1* info);

/* Returns a process-lifetime string owned by the bridge/IPP. */
VHSDECODE_IPP_API const char* VHSDECODE_IPP_CALL
vhsdecode_ipp_status_string(int32_t status);

/*
 * Creates a power-of-two, double-precision real FFT context. The supported
 * length range is 2 through 2^27. The context owns an immutable IPP FFT spec
 * and a private scratch buffer. Calls on the same context are serialized;
 * different contexts can execute concurrently.
 */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_create(int32_t length, vhsdecode_ipp_fft64_context** out_context);

/* Destroying NULL succeeds. A non-NULL handle may be destroyed only once. */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_destroy(vhsdecode_ipp_fft64_context* context);

/*
 * Forward R2C transform. input_length must equal N and output_length must
 * equal N/2+1. The output is ordinary interleaved complex data, including
 * the DC and Nyquist bins. The forward transform is not normalized.
 */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_forward_real(
    vhsdecode_ipp_fft64_context* context,
    const double* input,
    int32_t input_length,
    vhsdecode_ipp_complex64* output,
    int32_t output_length);

/*
 * Inverse C2R transform. input_length must equal N/2+1 and output_length must
 * equal N. The inverse result is normalized by 1/N.
 */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_inverse_real(
    vhsdecode_ipp_fft64_context* context,
    const vhsdecode_ipp_complex64* input,
    int32_t input_length,
    double* output,
    int32_t output_length);

/* Input and output arrays must not overlap. */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_complex64_multiply(
    const vhsdecode_ipp_complex64* lhs,
    const vhsdecode_ipp_complex64* rhs,
    vhsdecode_ipp_complex64* output,
    int32_t length);

/* At least one of magnitude or phase must be non-NULL. */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_complex64_magnitude_phase(
    const vhsdecode_ipp_complex64* input,
    double* magnitude,
    double* phase,
    int32_t length);

/*
 * Creates an arbitrary-order double IIR context. Numerator and denominator may
 * have different positive lengths; the shorter vector is zero-padded to
 * order+1, where order=max(lengths)-1. The bridge divides both vectors by a0
 * before passing [b0..bN,a0..aN] to IPP. order must be at least one.
 *
 * IPP's non-DF1 IIR primitive uses a direct-form-II delay line. Its N values
 * are the same transposed-DF-II accumulators used by the managed recurrence:
 *   y=b0*x+s0; s[i-1]=b[i]*x+s[i]-a[i]*y; s[N-1]=b[N]*x-a[N]*y.
 * initial_state_length=0 requests an all-zero delay line and initial_state may
 * be NULL. Otherwise its length must equal N and it must be non-NULL.
 */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_create(
    const double* numerator,
    int32_t numerator_length,
    const double* denominator,
    int32_t denominator_length,
    const double* initial_state,
    int32_t initial_state_length,
    vhsdecode_ipp_iir64_context** out_context);

VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_destroy(vhsdecode_ipp_iir64_context* context);

/* Replaces the complete N-value delay line with zeros. */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_reset(vhsdecode_ipp_iir64_context* context);

VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_get_state(
    vhsdecode_ipp_iir64_context* context,
    double* state,
    int32_t state_length);

VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_set_state(
    vhsdecode_ipp_iir64_context* context,
    const double* state,
    int32_t state_length);

/*
 * Exact in-place operation (input==output) is supported; partial overlap is
 * not. length=0 is a successful no-op and input/output may then be NULL.
 * Calls on one context are serialized, but the caller controls stream order.
 */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_process(
    vhsdecode_ipp_iir64_context* context,
    const double* input,
    double* output,
    int32_t length);

/*
 * Creates a cascade of double biquads. Sections use
 * [b0,b1,b2,a0,a1,a2] order and are normalized by their individual a0.
 * The 2*section_count DF2 state values are interleaved [z1,z2] per section,
 * exactly matching the managed SOS initial-condition array row order.
 * initial_state_length=0 requests all-zero state and initial_state may be NULL.
 * Otherwise the length must be 2*section_count and the pointer must be non-NULL.
 */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_create(
    const vhsdecode_ipp_sos64_section* sections,
    int32_t section_count,
    const double* initial_state,
    int32_t initial_state_length,
    vhsdecode_ipp_sos64_context** out_context);

VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_destroy(vhsdecode_ipp_sos64_context* context);

/* Replaces all 2*section_count state values with zeros. */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_reset(vhsdecode_ipp_sos64_context* context);

VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_get_state(
    vhsdecode_ipp_sos64_context* context,
    double* state,
    int32_t state_length);

VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_set_state(
    vhsdecode_ipp_sos64_context* context,
    const double* state,
    int32_t state_length);

/*
 * Exact in-place operation (input==output) is supported; partial overlap is
 * not. length=0 is a successful no-op and input/output may then be NULL.
 * Calls on one context are serialized, but the caller controls stream order.
 */
VHSDECODE_IPP_API int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_process(
    vhsdecode_ipp_sos64_context* context,
    const double* input,
    double* output,
    int32_t length);

#ifdef __cplusplus
}
#endif

#endif
