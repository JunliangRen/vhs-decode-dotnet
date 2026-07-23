#include "vhsdecode_ipp.h"

#include <ipp/ippcore.h>
#include <ipp/ipps.h>

#include <bit>
#include <cstddef>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <utility>
#include <vector>

struct vhsdecode_ipp_fft64_context {
    int32_t length = 0;
    Ipp8u* spec_storage = nullptr;
    const IppsFFTSpec_R_64f* spec = nullptr;
    Ipp8u* work_buffer = nullptr;
    std::mutex work_mutex;

    ~vhsdecode_ipp_fft64_context()
    {
        if (work_buffer != nullptr) {
            ippsFree(work_buffer);
        }
        if (spec_storage != nullptr) {
            ippsFree(spec_storage);
        }
    }
};

struct vhsdecode_ipp_iir64_context {
    int32_t state_length = 0;
    std::vector<Ipp64f> taps;
    Ipp8u* state_storage = nullptr;
    IppsIIRState_64f* state = nullptr;
    Ipp64f* zero_state = nullptr;
    std::mutex state_mutex;

    ~vhsdecode_ipp_iir64_context()
    {
        if (zero_state != nullptr) {
            ippsFree(zero_state);
        }
        if (state_storage != nullptr) {
            ippsFree(state_storage);
        }
    }
};

struct vhsdecode_ipp_sos64_context {
    int32_t state_length = 0;
    std::vector<Ipp64f> taps;
    Ipp8u* state_storage = nullptr;
    IppsIIRState_64f* state = nullptr;
    Ipp64f* zero_state = nullptr;
    std::mutex state_mutex;

    ~vhsdecode_ipp_sos64_context()
    {
        if (zero_state != nullptr) {
            ippsFree(zero_state);
        }
        if (state_storage != nullptr) {
            ippsFree(state_storage);
        }
    }
};

namespace {

std::once_flag g_ipp_init_once;
int32_t g_ipp_init_status = ippStsNoErr;
int32_t g_bridge_init_status = VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
uint64_t g_cpu_features = 0;
uint64_t g_enabled_cpu_features = 0;

static_assert(sizeof(vhsdecode_ipp_complex64) == sizeof(Ipp64fc));
static_assert(alignof(vhsdecode_ipp_complex64) == alignof(Ipp64fc));
static_assert(offsetof(vhsdecode_ipp_complex64, real) == offsetof(Ipp64fc, re));
static_assert(offsetof(vhsdecode_ipp_complex64, imag) == offsetof(Ipp64fc, im));
static_assert(sizeof(vhsdecode_ipp_sos64_section) == 6 * sizeof(double));
static_assert(offsetof(vhsdecode_ipp_sos64_section, b0) == 0 * sizeof(double));
static_assert(offsetof(vhsdecode_ipp_sos64_section, b1) == 1 * sizeof(double));
static_assert(offsetof(vhsdecode_ipp_sos64_section, b2) == 2 * sizeof(double));
static_assert(offsetof(vhsdecode_ipp_sos64_section, a0) == 3 * sizeof(double));
static_assert(offsetof(vhsdecode_ipp_sos64_section, a1) == 4 * sizeof(double));
static_assert(offsetof(vhsdecode_ipp_sos64_section, a2) == 5 * sizeof(double));
static_assert(sizeof(vhsdecode_ipp_runtime_info_v1) == 240);

void initialize_ipp() noexcept
{
    g_ipp_init_status = static_cast<int32_t>(ippInit());
    if (g_ipp_init_status == ippStsNonIntelCpu ||
        g_ipp_init_status == ippStsNotSupportedCpu) {
        g_bridge_init_status = VHSDECODE_IPP_STATUS_UNSUPPORTED_CPU;
        return;
    }
    if (g_ipp_init_status != ippStsNoErr) {
        g_bridge_init_status = g_ipp_init_status;
        return;
    }

    Ipp64u features = 0;
    const IppStatus feature_status = ippGetCpuFeatures(&features, nullptr);
    if (feature_status == ippStsNotSupportedCpu || feature_status == ippStsNonIntelCpu) {
        g_bridge_init_status = VHSDECODE_IPP_STATUS_UNSUPPORTED_CPU;
        return;
    }
    if (feature_status != ippStsNoErr) {
        g_bridge_init_status = static_cast<int32_t>(feature_status);
        return;
    }
    if ((features & static_cast<Ipp64u>(ippCPUID_SSE42)) == 0) {
        g_bridge_init_status = VHSDECODE_IPP_STATUS_UNSUPPORTED_CPU;
        return;
    }

    g_cpu_features = static_cast<uint64_t>(features);
    g_enabled_cpu_features = static_cast<uint64_t>(ippGetEnabledCpuFeatures());
    g_bridge_init_status = VHSDECODE_IPP_STATUS_OK;
}

int32_t ensure_ipp_initialized() noexcept
{
    try {
        std::call_once(g_ipp_init_once, initialize_ipp);
        return g_bridge_init_status;
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

template <size_t N>
void copy_string(char (&destination)[N], const char* source) noexcept
{
    destination[0] = '\0';
    if (source == nullptr) {
        return;
    }

    const size_t copy_length = (std::min)(std::strlen(source), N - 1);
    std::memcpy(destination, source, copy_length);
    destination[copy_length] = '\0';
}

bool is_supported_fft_length(int32_t length) noexcept
{
    constexpr int32_t max_fft64_length = 1 << 27;
    return length >= 2 && length <= max_fft64_length &&
        (length & (length - 1)) == 0;
}

const Ipp64fc* as_ipp_complex(const vhsdecode_ipp_complex64* value) noexcept
{
    return reinterpret_cast<const Ipp64fc*>(value);
}

Ipp64fc* as_ipp_complex(vhsdecode_ipp_complex64* value) noexcept
{
    return reinterpret_cast<Ipp64fc*>(value);
}

template <typename Context>
int32_t reset_iir_context(Context* context)
{
    if (context == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }

    try {
        const std::lock_guard lock(context->state_mutex);
        return static_cast<int32_t>(
            ippsIIRSetDlyLine_64f(context->state, context->zero_state));
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

template <typename Context>
int32_t get_iir_context_state(Context* context, double* state, int32_t state_length)
{
    if (context == nullptr || state == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (state_length != context->state_length) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    try {
        const std::lock_guard lock(context->state_mutex);
        return static_cast<int32_t>(ippsIIRGetDlyLine_64f(context->state, state));
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

template <typename Context>
int32_t set_iir_context_state(
    Context* context,
    const double* state,
    int32_t state_length)
{
    if (context == nullptr || state == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (state_length != context->state_length) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    try {
        const std::lock_guard lock(context->state_mutex);
        return static_cast<int32_t>(ippsIIRSetDlyLine_64f(context->state, state));
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

template <typename Context>
int32_t process_iir_context(
    Context* context,
    const double* input,
    double* output,
    int32_t length)
{
    if (context == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (length < 0) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }
    if (length == 0) {
        return VHSDECODE_IPP_STATUS_OK;
    }
    if (input == nullptr || output == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }

    try {
        const std::lock_guard lock(context->state_mutex);
        if (input == output) {
            return static_cast<int32_t>(
                ippsIIR_64f_I(output, length, context->state));
        }
        return static_cast<int32_t>(
            ippsIIR_64f(input, output, length, context->state));
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

} // namespace

uint32_t VHSDECODE_IPP_CALL vhsdecode_ipp_get_abi_version(void)
{
    return VHSDECODE_IPP_ABI_VERSION;
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_get_runtime_info(vhsdecode_ipp_runtime_info_v1* info)
{
    if (info == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (info->struct_size < sizeof(vhsdecode_ipp_runtime_info_v1)) {
        return VHSDECODE_IPP_STATUS_BUFFER_TOO_SMALL;
    }

    const int32_t initialization_status = ensure_ipp_initialized();
    vhsdecode_ipp_runtime_info_v1 result{};
    result.struct_size = sizeof(result);
    result.abi_version = VHSDECODE_IPP_ABI_VERSION;
    result.ipp_init_status = g_ipp_init_status;
    result.cpu_features = g_cpu_features;
    result.enabled_cpu_features = g_enabled_cpu_features;

    if (initialization_status == VHSDECODE_IPP_STATUS_OK) {
        const IppLibraryVersion* version = ippsGetLibVersion();
        if (version != nullptr) {
            result.ipp_major = version->major;
            result.ipp_minor = version->minor;
            result.ipp_update = version->patch;
            result.ipp_build = version->build;
            copy_string(result.ipp_name, version->Name);
            copy_string(result.ipp_version, version->Version);
            copy_string(result.ipp_build_date, version->BuildDate);
            copy_string(result.ipp_target_cpu, version->targetCpu);
        }
    }

    *info = result;
    return initialization_status;
}

const char* VHSDECODE_IPP_CALL vhsdecode_ipp_status_string(int32_t status)
{
    switch (status) {
    case VHSDECODE_IPP_STATUS_OK:
        return "No error";
    case VHSDECODE_IPP_STATUS_NULL_POINTER:
        return "Bridge received a null pointer";
    case VHSDECODE_IPP_STATUS_INVALID_ARGUMENT:
        return "Bridge received an invalid argument";
    case VHSDECODE_IPP_STATUS_UNSUPPORTED_LENGTH:
        return "FFT length is not a supported power of two";
    case VHSDECODE_IPP_STATUS_BUFFER_TOO_SMALL:
        return "Caller-provided buffer or structure is too small";
    case VHSDECODE_IPP_STATUS_OUT_OF_MEMORY:
        return "Native memory allocation failed";
    case VHSDECODE_IPP_STATUS_INTERNAL_ERROR:
        return "Unexpected native bridge failure";
    case VHSDECODE_IPP_STATUS_UNSUPPORTED_CPU:
        return "CPU is not a supported Genuine Intel processor with SSE4.2";
    default: {
        const char* description = ippGetStatusString(static_cast<IppStatus>(status));
        return description != nullptr ? description : "Unknown status code";
    }
    }
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_create(int32_t length, vhsdecode_ipp_fft64_context** out_context)
{
    if (out_context == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    *out_context = nullptr;
    if (!is_supported_fft_length(length)) {
        return VHSDECODE_IPP_STATUS_UNSUPPORTED_LENGTH;
    }

    const int32_t initialization_status = ensure_ipp_initialized();
    if (initialization_status != VHSDECODE_IPP_STATUS_OK) {
        return initialization_status;
    }

    try {
        auto* context = new (std::nothrow) vhsdecode_ipp_fft64_context();
        if (context == nullptr) {
            return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
        }

        context->length = length;
        const int order = std::countr_zero(static_cast<uint32_t>(length));
        int spec_size = 0;
        int init_buffer_size = 0;
        int work_buffer_size = 0;
        IppStatus status = ippsFFTGetSize_R_64f(
            order,
            IPP_FFT_DIV_INV_BY_N,
            ippAlgHintNone,
            &spec_size,
            &init_buffer_size,
            &work_buffer_size);
        if (status != ippStsNoErr) {
            delete context;
            return static_cast<int32_t>(status);
        }

        context->spec_storage = ippsMalloc_8u(spec_size);
        if (context->spec_storage == nullptr) {
            delete context;
            return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
        }

        Ipp8u* init_buffer = nullptr;
        if (init_buffer_size > 0) {
            init_buffer = ippsMalloc_8u(init_buffer_size);
            if (init_buffer == nullptr) {
                delete context;
                return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
            }
        }

        IppsFFTSpec_R_64f* initialized_spec = nullptr;
        status = ippsFFTInit_R_64f(
            &initialized_spec,
            order,
            IPP_FFT_DIV_INV_BY_N,
            ippAlgHintNone,
            context->spec_storage,
            init_buffer);
        if (init_buffer != nullptr) {
            ippsFree(init_buffer);
        }
        if (status != ippStsNoErr) {
            delete context;
            return static_cast<int32_t>(status);
        }
        context->spec = initialized_spec;

        if (work_buffer_size > 0) {
            context->work_buffer = ippsMalloc_8u(work_buffer_size);
            if (context->work_buffer == nullptr) {
                delete context;
                return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
            }
        }

        *out_context = context;
        return VHSDECODE_IPP_STATUS_OK;
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_destroy(vhsdecode_ipp_fft64_context* context)
{
    delete context;
    return VHSDECODE_IPP_STATUS_OK;
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_forward_real(
    vhsdecode_ipp_fft64_context* context,
    const double* input,
    int32_t input_length,
    vhsdecode_ipp_complex64* output,
    int32_t output_length)
{
    if (context == nullptr || input == nullptr || output == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (input_length != context->length || output_length != (context->length / 2) + 1) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    try {
        const std::lock_guard lock(context->work_mutex);
        return static_cast<int32_t>(ippsFFTFwd_RToCCS_64f(
            input,
            reinterpret_cast<Ipp64f*>(as_ipp_complex(output)),
            context->spec,
            context->work_buffer));
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_fft64_inverse_real(
    vhsdecode_ipp_fft64_context* context,
    const vhsdecode_ipp_complex64* input,
    int32_t input_length,
    double* output,
    int32_t output_length)
{
    if (context == nullptr || input == nullptr || output == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (input_length != (context->length / 2) + 1 || output_length != context->length) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    try {
        const std::lock_guard lock(context->work_mutex);
        return static_cast<int32_t>(ippsFFTInv_CCSToR_64f(
            reinterpret_cast<const Ipp64f*>(as_ipp_complex(input)),
            output,
            context->spec,
            context->work_buffer));
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_complex64_multiply(
    const vhsdecode_ipp_complex64* lhs,
    const vhsdecode_ipp_complex64* rhs,
    vhsdecode_ipp_complex64* output,
    int32_t length)
{
    if (lhs == nullptr || rhs == nullptr || output == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (length <= 0) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    const int32_t initialization_status = ensure_ipp_initialized();
    if (initialization_status != VHSDECODE_IPP_STATUS_OK) {
        return initialization_status;
    }
    return static_cast<int32_t>(ippsMul_64fc(
        as_ipp_complex(lhs), as_ipp_complex(rhs), as_ipp_complex(output), length));
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_complex64_magnitude_phase(
    const vhsdecode_ipp_complex64* input,
    double* magnitude,
    double* phase,
    int32_t length)
{
    if (input == nullptr || (magnitude == nullptr && phase == nullptr)) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (length <= 0) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    const int32_t initialization_status = ensure_ipp_initialized();
    if (initialization_status != VHSDECODE_IPP_STATUS_OK) {
        return initialization_status;
    }
    if (magnitude != nullptr) {
        const IppStatus status = ippsMagnitude_64fc(as_ipp_complex(input), magnitude, length);
        if (status != ippStsNoErr) {
            return static_cast<int32_t>(status);
        }
    }
    if (phase != nullptr) {
        return static_cast<int32_t>(ippsPhase_64fc(as_ipp_complex(input), phase, length));
    }
    return VHSDECODE_IPP_STATUS_OK;
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_create(
    const double* numerator,
    int32_t numerator_length,
    const double* denominator,
    int32_t denominator_length,
    const double* initial_state,
    int32_t initial_state_length,
    vhsdecode_ipp_iir64_context** out_context)
{
    if (out_context == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    *out_context = nullptr;
    if (numerator == nullptr || denominator == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (numerator_length <= 0 || denominator_length <= 0) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    const int32_t coefficient_count = (std::max)(numerator_length, denominator_length);
    const int32_t order = coefficient_count - 1;
    if (order <= 0) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }
    if (initial_state_length != 0 && initial_state_length != order) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }
    if (initial_state_length != 0 && initial_state == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (denominator[0] == 0.0) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    const int32_t initialization_status = ensure_ipp_initialized();
    if (initialization_status != VHSDECODE_IPP_STATUS_OK) {
        return initialization_status;
    }

    try {
        std::vector<Ipp64f> taps(static_cast<size_t>(coefficient_count) * 2, 0.0);
        const double a0 = denominator[0];
        for (int32_t i = 0; i < numerator_length; ++i) {
            taps[static_cast<size_t>(i)] = numerator[i] / a0;
        }
        const size_t denominator_offset = static_cast<size_t>(coefficient_count);
        for (int32_t i = 0; i < denominator_length; ++i) {
            taps[denominator_offset + static_cast<size_t>(i)] = denominator[i] / a0;
        }

        int state_size = 0;
        IppStatus status = ippsIIRGetStateSize_64f(order, &state_size);
        if (status != ippStsNoErr) {
            return static_cast<int32_t>(status);
        }

        auto* context = new (std::nothrow) vhsdecode_ipp_iir64_context();
        if (context == nullptr) {
            return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
        }
        context->state_length = order;
        context->taps = std::move(taps);
        context->state_storage = ippsMalloc_8u(state_size);
        context->zero_state = ippsMalloc_64f(order);
        if (context->state_storage == nullptr || context->zero_state == nullptr) {
            delete context;
            return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
        }
        status = ippsZero_64f(context->zero_state, order);
        if (status != ippStsNoErr) {
            delete context;
            return static_cast<int32_t>(status);
        }

        const Ipp64f* effective_state =
            initial_state_length == order ? initial_state : context->zero_state;
        status = ippsIIRInit_64f(
            &context->state,
            context->taps.data(),
            order,
            effective_state,
            context->state_storage);
        if (status != ippStsNoErr) {
            delete context;
            return static_cast<int32_t>(status);
        }

        *out_context = context;
        return VHSDECODE_IPP_STATUS_OK;
    }
    catch (const std::bad_alloc&) {
        return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_destroy(vhsdecode_ipp_iir64_context* context)
{
    delete context;
    return VHSDECODE_IPP_STATUS_OK;
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_reset(vhsdecode_ipp_iir64_context* context)
{
    return reset_iir_context(context);
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_get_state(
    vhsdecode_ipp_iir64_context* context,
    double* state,
    int32_t state_length)
{
    return get_iir_context_state(context, state, state_length);
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_set_state(
    vhsdecode_ipp_iir64_context* context,
    const double* state,
    int32_t state_length)
{
    return set_iir_context_state(context, state, state_length);
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_iir64_process(
    vhsdecode_ipp_iir64_context* context,
    const double* input,
    double* output,
    int32_t length)
{
    return process_iir_context(context, input, output, length);
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_create(
    const vhsdecode_ipp_sos64_section* sections,
    int32_t section_count,
    const double* initial_state,
    int32_t initial_state_length,
    vhsdecode_ipp_sos64_context** out_context)
{
    if (out_context == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    *out_context = nullptr;
    if (sections == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    if (section_count <= 0 ||
        section_count > (std::numeric_limits<int32_t>::max)() / 2) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }

    const int32_t state_length = section_count * 2;
    if (initial_state_length != 0 && initial_state_length != state_length) {
        return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
    }
    if (initial_state_length != 0 && initial_state == nullptr) {
        return VHSDECODE_IPP_STATUS_NULL_POINTER;
    }
    for (int32_t i = 0; i < section_count; ++i) {
        if (sections[i].a0 == 0.0) {
            return VHSDECODE_IPP_STATUS_INVALID_ARGUMENT;
        }
    }

    const int32_t initialization_status = ensure_ipp_initialized();
    if (initialization_status != VHSDECODE_IPP_STATUS_OK) {
        return initialization_status;
    }

    try {
        std::vector<Ipp64f> taps(static_cast<size_t>(section_count) * 6);
        for (int32_t i = 0; i < section_count; ++i) {
            const auto& section = sections[i];
            const size_t offset = static_cast<size_t>(i) * 6;
            taps[offset] = section.b0 / section.a0;
            taps[offset + 1] = section.b1 / section.a0;
            taps[offset + 2] = section.b2 / section.a0;
            taps[offset + 3] = 1.0;
            taps[offset + 4] = section.a1 / section.a0;
            taps[offset + 5] = section.a2 / section.a0;
        }

        int state_size = 0;
        IppStatus status = ippsIIRGetStateSize_BiQuad_64f(section_count, &state_size);
        if (status != ippStsNoErr) {
            return static_cast<int32_t>(status);
        }

        auto* context = new (std::nothrow) vhsdecode_ipp_sos64_context();
        if (context == nullptr) {
            return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
        }
        context->state_length = state_length;
        context->taps = std::move(taps);
        context->state_storage = ippsMalloc_8u(state_size);
        context->zero_state = ippsMalloc_64f(state_length);
        if (context->state_storage == nullptr || context->zero_state == nullptr) {
            delete context;
            return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
        }
        status = ippsZero_64f(context->zero_state, state_length);
        if (status != ippStsNoErr) {
            delete context;
            return static_cast<int32_t>(status);
        }

        const Ipp64f* effective_state =
            initial_state_length == state_length ? initial_state : context->zero_state;
        status = ippsIIRInit_BiQuad_64f(
            &context->state,
            context->taps.data(),
            section_count,
            effective_state,
            context->state_storage);
        if (status != ippStsNoErr) {
            delete context;
            return static_cast<int32_t>(status);
        }

        *out_context = context;
        return VHSDECODE_IPP_STATUS_OK;
    }
    catch (const std::bad_alloc&) {
        return VHSDECODE_IPP_STATUS_OUT_OF_MEMORY;
    }
    catch (...) {
        return VHSDECODE_IPP_STATUS_INTERNAL_ERROR;
    }
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_destroy(vhsdecode_ipp_sos64_context* context)
{
    delete context;
    return VHSDECODE_IPP_STATUS_OK;
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_reset(vhsdecode_ipp_sos64_context* context)
{
    return reset_iir_context(context);
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_get_state(
    vhsdecode_ipp_sos64_context* context,
    double* state,
    int32_t state_length)
{
    return get_iir_context_state(context, state, state_length);
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_set_state(
    vhsdecode_ipp_sos64_context* context,
    const double* state,
    int32_t state_length)
{
    return set_iir_context_state(context, state, state_length);
}

int32_t VHSDECODE_IPP_CALL
vhsdecode_ipp_sos64_process(
    vhsdecode_ipp_sos64_context* context,
    const double* input,
    double* output,
    int32_t length)
{
    return process_iir_context(context, input, output, length);
}
