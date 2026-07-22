#include "vhsdecode_cuda.h"

#include <cuda_runtime.h>
#include <cufft.h>

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <system_error>
#include <thread>
#include <unordered_map>

static_assert(sizeof(vhsdecode_cuda_complex64) == sizeof(cufftDoubleComplex));
static_assert(sizeof(vhsdecode_cuda_device_info) == 328);
static_assert(sizeof(vhsdecode_cuda_self_test_metrics) == 64);
static_assert(sizeof(vhsdecode_cuda_ld_frequency_options) == 144);
static_assert(offsetof(vhsdecode_cuda_ld_frequency_options, efm_filter) == 32);
static_assert(offsetof(vhsdecode_cuda_ld_frequency_options, efm_output) == 56);
static_assert(offsetof(vhsdecode_cuda_ld_frequency_options, reserved) == 80);
static_assert(sizeof(vhsdecode_cuda_rf_batch_job) == 232);
static_assert(sizeof(std::uintptr_t) == sizeof(std::uint64_t));

namespace {

constexpr std::uint32_t kStreamCount = VHSDECODE_CUDA_STREAM_COUNT;
constexpr int kMinimumComputeCapability = 75;
constexpr int kMinimumCudaDriverApi = 13000;
constexpr std::uint32_t kThreadsPerBlock = 256;
constexpr std::uint64_t kAllCapabilities =
    VHSDECODE_CUDA_CAP_RF_BATCH_TWO_STAGE |
    VHSDECODE_CUDA_CAP_RF_HIGH_PASS |
    VHSDECODE_CUDA_CAP_RF_MTF |
    VHSDECODE_CUDA_CAP_RF_ANALYTIC |
    VHSDECODE_CUDA_CAP_RF_ENVELOPE |
    VHSDECODE_CUDA_CAP_RF_CONJUGATE_DEMOD |
    VHSDECODE_CUDA_CAP_RF_VIDEO |
    VHSDECODE_CUDA_CAP_RF_VIDEO_LOW_PASS |
    VHSDECODE_CUDA_CAP_RF_MODE_STANDARD |
    VHSDECODE_CUDA_CAP_RF_MODE_VHS_RUST |
    VHSDECODE_CUDA_CAP_RF_MODE_CVBS |
    VHSDECODE_CUDA_CAP_VHS_RUST_DEMOD_KERNEL |
    VHSDECODE_CUDA_CAP_LD_EFM_FREQUENCY |
    VHSDECODE_CUDA_CAP_LD_ANALOG_AUDIO_FREQUENCY;

thread_local std::string g_last_error;
thread_local bool g_rf_batch_sub_call = false;

struct PlanKey {
    int type;
    int sample_count;
    int batch_count;
    std::uint32_t stream_index;

    bool operator==(const PlanKey& other) const noexcept
    {
        return type == other.type &&
            sample_count == other.sample_count &&
            batch_count == other.batch_count &&
            stream_index == other.stream_index;
    }
};

struct PlanKeyHash {
    std::size_t operator()(const PlanKey& key) const noexcept
    {
        std::size_t hash = static_cast<std::size_t>(key.type);
        hash = (hash * 16777619u) ^ static_cast<std::size_t>(key.sample_count);
        hash = (hash * 16777619u) ^ static_cast<std::size_t>(key.batch_count);
        return (hash * 16777619u) ^ key.stream_index;
    }
};

struct RfWorkspace {
    std::uint64_t sample_capacity{};
    std::uint64_t bin_capacity{};
    std::uint64_t filter_bin_capacity{};
    std::uint32_t batch_capacity{};
    double* host_io{};
    vhsdecode_cuda_complex64* host_complex{};
    double* input{};
    cufftDoubleComplex* base_spectrum{};
    cufftDoubleComplex* work_spectrum{};
    cufftDoubleComplex* alternate_spectrum{};
    cufftDoubleComplex* filters[5]{};
    std::uint64_t filter_counts[5]{};
    bool filter_valid[5]{};
    cufftDoubleComplex* previous{};
    cufftDoubleComplex* last{};
    double* rf_high_pass{};
    double* analytic_real{};
    double* analytic_imag{};
    double* envelope{};
    double* demod{};
    double* video{};
    double* video_low_pass{};
    std::uint64_t ld_sample_capacity{};
    std::uint64_t ld_bin_capacity{};
    std::uint32_t ld_batch_capacity{};
    double* host_ld_efm{};
    vhsdecode_cuda_complex64* host_ld_filters[3]{};
    vhsdecode_cuda_complex64* host_ld_audio[2]{};
    cufftDoubleComplex* ld_filters[3]{};
    std::uint64_t ld_filter_counts[3]{};
    bool ld_filter_valid[3]{};
    double* ld_efm{};
};

struct RfWorkspaceSizes {
    std::uint64_t samples{};
    std::uint64_t bins_per_batch{};
    std::uint64_t bins{};
    std::size_t sample_bytes{};
    std::size_t spectrum_bytes{};
    std::size_t filter_bytes{};
    std::size_t batch_complex_bytes{};
    std::size_t host_io_bytes{};
    std::size_t host_complex_bytes{};
};

} // namespace

struct vhsdecode_cuda_context {
    int device_ordinal{};
    cudaStream_t streams[kStreamCount]{};
    std::unordered_map<PlanKey, cufftHandle, PlanKeyHash> plans;
    std::mutex plan_mutex;
    std::mutex error_mutex;
    std::mutex stream_mutex[kStreamCount];
    std::string last_error;
    RfWorkspace workspaces[kStreamCount];
};

struct vhsdecode_cuda_buffer {
    vhsdecode_cuda_context* owner{};
    int device_ordinal{};
    void* pointer{};
    std::uint64_t byte_count{};
};

namespace {

vhsdecode_cuda_status fail(vhsdecode_cuda_context*,
                           vhsdecode_cuda_status,
                           const std::string&);
vhsdecode_cuda_status map_cuda_error(vhsdecode_cuda_context*, cudaError_t, const char*);
vhsdecode_cuda_status map_cufft_error(vhsdecode_cuda_context*, cufftResult, const char*);
vhsdecode_cuda_status select_device(vhsdecode_cuda_context*);
bool valid_stream(std::uint32_t);
bool multiply_size(std::uint64_t, std::uint64_t, std::uint64_t*);
bool multiply_to_size(std::uint64_t, std::uint64_t, std::size_t*);
bool add_size(std::size_t, std::size_t, std::size_t*);
bool calculate_rf_workspace_sizes(std::uint32_t, std::uint32_t,
                                  RfWorkspaceSizes*);
bool buffer_range_valid(const vhsdecode_cuda_buffer*, std::uint64_t, std::uint64_t);
vhsdecode_cuda_status validate_buffer(vhsdecode_cuda_context*,
                                      const vhsdecode_cuda_buffer*,
                                      std::uint64_t,
                                      const char*);
vhsdecode_cuda_status get_plan(vhsdecode_cuda_context*, int, int, int,
                               std::uint32_t, cufftHandle*);
std::uint32_t launch_block_count(std::uint64_t);
vhsdecode_cuda_status check_kernel(vhsdecode_cuda_context*, const char*);
vhsdecode_cuda_status launch_scale_complex(vhsdecode_cuda_context*,
                                           cufftDoubleComplex*,
                                           std::uint64_t,
                                           double,
                                           std::uint32_t);
vhsdecode_cuda_status execute_r2c(vhsdecode_cuda_context*, double*,
                                  cufftDoubleComplex*, int, int, std::uint32_t);
vhsdecode_cuda_status execute_c2r(vhsdecode_cuda_context*, cufftDoubleComplex*,
                                  double*, int, int, bool, std::uint32_t);
vhsdecode_cuda_status execute_c2c(vhsdecode_cuda_context*, cufftDoubleComplex*,
                                  cufftDoubleComplex*, int, int, int, bool,
                                  std::uint32_t);
void reset_ld_workspace(RfWorkspace&);
void reset_workspace(RfWorkspace&);
vhsdecode_cuda_status ensure_workspace(vhsdecode_cuda_context*, std::uint32_t,
                                       std::uint32_t, std::uint32_t);
vhsdecode_cuda_status ensure_ld_workspace(vhsdecode_cuda_context*, std::uint32_t,
                                           std::uint32_t, std::uint32_t);
vhsdecode_cuda_status execute_rf_batch_split(
    vhsdecode_cuda_context*, const vhsdecode_cuda_rf_batch_job*,
    const vhsdecode_cuda_ld_frequency_options*);
vhsdecode_cuda_status launch_frequency_multiply(vhsdecode_cuda_context*,
                                                cufftDoubleComplex*,
                                                const cufftDoubleComplex*,
                                                std::uint64_t,
                                                std::uint32_t,
                                                std::uint64_t,
                                                std::uint32_t);

__global__ void hilbert_r2c_kernel(cufftDoubleComplex*, std::uint32_t, std::uint32_t);
__global__ void envelope_kernel(const double*, const double*, double*, std::uint64_t);
__global__ void affine_abs_kernel(double*, double*, std::uint64_t, double, double);
__global__ void conjugate_phase_kernel(const cufftDoubleComplex*, double*,
                                       std::uint32_t, std::uint32_t,
                                       const cufftDoubleComplex*,
                                       cufftDoubleComplex*, double);
__global__ void conjugate_phase_split_kernel(const double*, const double*, double*,
                                             std::uint32_t, std::uint32_t,
                                             const cufftDoubleComplex*,
                                             cufftDoubleComplex*, double);
__global__ void vhs_rust_demod_kernel(const double*, const double*, double*,
                                      std::uint32_t, std::uint32_t, float);
__global__ void ld_efm_half_filter_kernel(const cufftDoubleComplex*,
                                          const cufftDoubleComplex*,
                                          cufftDoubleComplex*,
                                          std::uint32_t, std::uint32_t);
__global__ void ld_audio_slice_filter_kernel(const cufftDoubleComplex*,
                                              const cufftDoubleComplex*,
                                              cufftDoubleComplex*,
                                              std::uint32_t, std::uint32_t,
                                              std::uint32_t, std::uint32_t);

} // namespace

extern "C" {

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_rf_batch_execute(vhsdecode_cuda_context* context,
                                const vhsdecode_cuda_rf_batch_job* job)
{
    if (context == nullptr || job == nullptr || job->struct_size < sizeof(*job)) {
        return fail(context, VHSDECODE_CUDA_ABI_MISMATCH,
                    "RF batch job is missing or smaller than ABI v1");
    }
    if (job->sample_count < 2 || (job->sample_count & 1u) != 0 ||
        job->sample_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        job->batch_count == 0 ||
        job->batch_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        !valid_stream(job->stream_index) ||
        job->input == nullptr ||
        (job->flags & ~VHSDECODE_CUDA_RF_ALL_FLAGS) != 0) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "RF batch dimensions, flags, input, or stream are invalid");
    }

    RfWorkspaceSizes workspace_sizes{};
    if (!calculate_rf_workspace_sizes(job->sample_count, job->batch_count,
                                      &workspace_sizes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "RF batch aggregate workspace size overflow");
    }

    const bool is_standard =
        job->mode == VHSDECODE_CUDA_RF_MODE_STANDARD_CONJUGATE;
    const bool is_vhs_rust =
        job->mode == VHSDECODE_CUDA_RF_MODE_VHS_RUST_APPROXIMATION;
    const bool is_cvbs = job->mode == VHSDECODE_CUDA_RF_MODE_CVBS;
    if (!is_standard && !is_vhs_rust && !is_cvbs) {
        return fail(context, VHSDECODE_CUDA_NOT_SUPPORTED,
                    "Requested RF mode is not defined by ABI v1");
    }
    if (job->flags == 0) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "RF batch does not request any output");
    }

    const bool output_high_pass =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_HIGH_PASS) != 0;
    const bool output_analytic =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_ANALYTIC) != 0;
    const bool output_envelope =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_ENVELOPE) != 0;
    const bool output_demod =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_DEMOD_RAW) != 0;
    const bool output_video =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_VIDEO) != 0;
    const bool output_video_low_pass =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS) != 0;
    const bool apply_mtf = (job->flags & VHSDECODE_CUDA_RF_APPLY_MTF) != 0;
    const bool output_ld_efm =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_LD_EFM) != 0;
    const bool output_ld_analog_audio =
        (job->flags & VHSDECODE_CUDA_RF_OUTPUT_LD_ANALOG_AUDIO) != 0;
    const bool requests_ld_frequency = output_ld_efm || output_ld_analog_audio;
    const bool requests_analytic_graph = output_analytic || output_envelope ||
        output_demod || output_video || output_video_low_pass;

    vhsdecode_cuda_ld_frequency_options ld_options{};
    std::size_t left_audio_bytes = 0;
    std::size_t right_audio_bytes = 0;
    if (requests_ld_frequency) {
        if (!is_standard) {
            return fail(context, VHSDECODE_CUDA_NOT_SUPPORTED,
                        "LD EFM and analog-audio frequency branches require standard RF mode");
        }
        const auto options_address = static_cast<std::uintptr_t>(
            job->reserved[VHSDECODE_CUDA_RF_LD_OPTIONS_RESERVED_INDEX]);
        if (options_address == 0) {
            return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                        "LD frequency output was requested without an options structure");
        }
        const auto* options = reinterpret_cast<
            const vhsdecode_cuda_ld_frequency_options*>(options_address);
        if (options->struct_size < sizeof(*options)) {
            return fail(context, VHSDECODE_CUDA_ABI_MISMATCH,
                        "LD frequency options are smaller than ABI v1");
        }
        ld_options = *options;
        if (ld_options.flags != 0) {
            return fail(context, VHSDECODE_CUDA_NOT_SUPPORTED,
                        "LD frequency option flags are not defined by ABI v1");
        }
        if (output_ld_efm &&
            (ld_options.efm_filter == nullptr || ld_options.efm_output == nullptr)) {
            return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                        "LD EFM output requires its half-spectrum filter and output buffer");
        }
        if (output_ld_analog_audio) {
            const auto valid_audio_slice = [&](std::uint32_t low_bin,
                                               std::uint32_t bin_count) {
                const bool is_power_of_two =
                    bin_count >= 2u && (bin_count & (bin_count - 1u)) == 0;
                const std::uint64_t upper_bin =
                    static_cast<std::uint64_t>(low_bin) + (bin_count / 2u);
                return is_power_of_two && (bin_count & 1u) == 0 &&
                    bin_count <= (job->sample_count / 2u) + 1u &&
                    upper_bin <= job->sample_count / 2u;
            };
            if (!valid_audio_slice(ld_options.audio_left_low_bin,
                                   ld_options.audio_left_bin_count) ||
                !valid_audio_slice(ld_options.audio_right_low_bin,
                                   ld_options.audio_right_bin_count)) {
                return fail(context, VHSDECODE_CUDA_NOT_SUPPORTED,
                            "LD analog-audio slices must be even power-of-two ranges inside the positive RF half-spectrum");
            }
            if (ld_options.audio_left_filter == nullptr ||
                ld_options.audio_right_filter == nullptr ||
                ld_options.audio_left_output == nullptr ||
                ld_options.audio_right_output == nullptr) {
                return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                            "LD analog-audio output requires both channel filters and output buffers");
            }
            std::uint64_t left_audio_elements{};
            std::uint64_t right_audio_elements{};
            if (!multiply_size(ld_options.audio_left_bin_count, job->batch_count,
                               &left_audio_elements) ||
                !multiply_size(ld_options.audio_right_bin_count, job->batch_count,
                               &right_audio_elements) ||
                !multiply_to_size(left_audio_elements,
                                  sizeof(vhsdecode_cuda_complex64),
                                  &left_audio_bytes) ||
                !multiply_to_size(right_audio_elements,
                                  sizeof(vhsdecode_cuda_complex64),
                                  &right_audio_bytes)) {
                return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                            "LD analog-audio aggregate size overflow");
            }
        }
    }

    if (apply_mtf && !requests_analytic_graph) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "RF MTF was requested without an analytic-graph output");
    }

    if ((output_high_pass && job->rf_high_pass_output == nullptr) ||
        (output_analytic &&
         (job->analytic_real_output == nullptr || job->analytic_imag_output == nullptr)) ||
        (output_envelope && job->envelope_output == nullptr) ||
        (output_demod && job->demod_raw_output == nullptr) ||
        (output_video && job->video_output == nullptr) ||
        (output_video_low_pass && job->video_low_pass_output == nullptr)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "A requested RF batch output pointer is null");
    }

    if (is_cvbs) {
        const std::uint32_t allowed =
            VHSDECODE_CUDA_RF_OUTPUT_ENVELOPE |
            VHSDECODE_CUDA_RF_OUTPUT_DEMOD_RAW |
            VHSDECODE_CUDA_RF_OUTPUT_VIDEO |
            VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS;
        if ((job->flags & ~allowed) != 0 ||
            !std::isfinite(job->cvbs_raw_scale) ||
            !std::isfinite(job->cvbs_raw_offset) ||
            (output_video_low_pass && job->demod_video_low_pass_filter == nullptr) ||
            job->previous_analytic_per_batch != nullptr ||
            job->last_analytic_per_batch != nullptr) {
            return fail(context, VHSDECODE_CUDA_NOT_SUPPORTED,
                        "CVBS mode supports reconstruction, affine mapping, envelope, direct video/demod, and video low-pass only");
        }
    } else {
        const bool needs_analytic = output_analytic || output_envelope || output_demod ||
            output_video || output_video_low_pass;
        if ((output_high_pass && job->rf_high_pass_filter == nullptr) ||
            (needs_analytic && job->rf_video_filter == nullptr) ||
            (apply_mtf && job->mtf_filter == nullptr) ||
            (output_video && job->demod_video_filter == nullptr) ||
            (output_video_low_pass && job->demod_video_low_pass_filter == nullptr) ||
            ((output_demod || output_video || output_video_low_pass) &&
             (!std::isfinite(job->demod_phase_scale) || job->demod_phase_scale <= 0.0))) {
            return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                        "RF batch is missing a required filter or demodulation scale");
        }
        if ((job->previous_analytic_per_batch != nullptr ||
             job->last_analytic_per_batch != nullptr) &&
            !(output_demod || output_video || output_video_low_pass)) {
            return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                        "Analytic state buffers require a demodulation output path");
        }
        if (is_vhs_rust &&
            (output_envelope || output_video || output_video_low_pass ||
             requests_ld_frequency ||
             job->previous_analytic_per_batch != nullptr ||
             job->last_analytic_per_batch != nullptr)) {
            return fail(context, VHSDECODE_CUDA_NOT_SUPPORTED,
                        "VHS Rust mode is a first-stage hybrid only; SOS envelope, high-boost/diff repair, and video filtering remain on CPU");
        }
    }

    if (!g_rf_batch_sub_call && job->batch_count >= 2u) {
        return execute_rf_batch_split(
            context, job, requests_ld_frequency ? &ld_options : nullptr);
    }

    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    std::lock_guard stream_lock(context->stream_mutex[job->stream_index]);
    status = ensure_workspace(context, job->sample_count, job->batch_count,
                              job->stream_index);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    if (requests_ld_frequency) {
        status = ensure_ld_workspace(context, job->sample_count, job->batch_count,
                                     job->stream_index);
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }

    RfWorkspace& workspace = context->workspaces[job->stream_index];
    cudaStream_t stream = context->streams[job->stream_index];
    const std::uint64_t sample_count = workspace_sizes.samples;
    const std::uint64_t bins_per_batch = workspace_sizes.bins_per_batch;
    const std::uint64_t bin_count = workspace_sizes.bins;
    const std::size_t sample_bytes = workspace_sizes.sample_bytes;
    const std::size_t spectrum_bytes = workspace_sizes.spectrum_bytes;
    const std::size_t filter_bytes = workspace_sizes.filter_bytes;
    const std::size_t state_bytes = workspace_sizes.batch_complex_bytes;

    double* const host_high_pass = workspace.host_io + sample_count;
    double* const host_analytic_real = workspace.host_io + (sample_count * 2u);
    double* const host_analytic_imag = workspace.host_io + (sample_count * 3u);
    double* const host_envelope = workspace.host_io + (sample_count * 4u);
    double* const host_demod = workspace.host_io + (sample_count * 5u);
    double* const host_video = workspace.host_io + (sample_count * 6u);
    double* const host_video_low_pass = workspace.host_io + (sample_count * 7u);
    auto* const host_previous = reinterpret_cast<cufftDoubleComplex*>(
        workspace.host_complex + (workspace.filter_bin_capacity * 5u));
    auto* const host_last = host_previous + job->batch_count;

    std::memcpy(workspace.host_io, job->input, sample_bytes);
    status = map_cuda_error(
        context,
        cudaMemcpyAsync(workspace.input, workspace.host_io, sample_bytes,
                        cudaMemcpyHostToDevice, stream),
        "RF batch input upload");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;

    auto upload_filter = [&](std::uint32_t slot,
                             const vhsdecode_cuda_complex64* filter,
                             const char* operation) -> vhsdecode_cuda_status {
        if (filter == nullptr) return VHSDECODE_CUDA_SUCCESS;
        auto* host_filter = workspace.host_complex +
            (workspace.filter_bin_capacity * slot);
        if (workspace.filter_valid[slot] &&
            workspace.filter_counts[slot] == bins_per_batch &&
            std::memcmp(host_filter, filter, filter_bytes) == 0) {
            return VHSDECODE_CUDA_SUCCESS;
        }
        std::memcpy(host_filter, filter, filter_bytes);
        const auto upload_status = map_cuda_error(
            context,
            cudaMemcpyAsync(workspace.filters[slot], host_filter, filter_bytes,
                            cudaMemcpyHostToDevice, stream),
            operation);
        if (upload_status == VHSDECODE_CUDA_SUCCESS) {
            workspace.filter_counts[slot] = bins_per_batch;
            workspace.filter_valid[slot] = true;
        }
        return upload_status;
    };

    if (is_cvbs) {
        if (output_video_low_pass) {
            status = upload_filter(4u, job->demod_video_low_pass_filter,
                                   "CVBS low-pass filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
    } else {
        if (job->rf_video_filter != nullptr) {
            status = upload_filter(0u, job->rf_video_filter, "RF video filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (output_high_pass) {
            status = upload_filter(1u, job->rf_high_pass_filter,
                                   "RF high-pass filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (apply_mtf) {
            status = upload_filter(2u, job->mtf_filter, "RF MTF filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (output_video) {
            status = upload_filter(3u, job->demod_video_filter,
                                   "Demod video filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (output_video_low_pass) {
            status = upload_filter(4u, job->demod_video_low_pass_filter,
                                   "Demod video low-pass filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (job->previous_analytic_per_batch != nullptr) {
            std::memcpy(host_previous, job->previous_analytic_per_batch, state_bytes);
            status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.previous, host_previous, state_bytes,
                                cudaMemcpyHostToDevice, stream),
                "Previous analytic state upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
    }

    if (requests_ld_frequency) {
        auto upload_ld_filter = [&](std::uint32_t slot,
                                    const vhsdecode_cuda_complex64* filter,
                                    std::uint64_t element_count,
                                    const char* operation) -> vhsdecode_cuda_status {
            std::size_t bytes{};
            if (!multiply_to_size(element_count,
                                  sizeof(vhsdecode_cuda_complex64), &bytes)) {
                return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                            "LD filter byte size overflow");
            }
            auto* host_filter = workspace.host_ld_filters[slot];
            if (workspace.ld_filter_valid[slot] &&
                workspace.ld_filter_counts[slot] == element_count &&
                std::memcmp(host_filter, filter, bytes) == 0) {
                return VHSDECODE_CUDA_SUCCESS;
            }
            std::memcpy(host_filter, filter, bytes);
            const auto upload_status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.ld_filters[slot], host_filter, bytes,
                                cudaMemcpyHostToDevice, stream),
                operation);
            if (upload_status == VHSDECODE_CUDA_SUCCESS) {
                workspace.ld_filter_counts[slot] = element_count;
                workspace.ld_filter_valid[slot] = true;
            }
            return upload_status;
        };

        if (output_ld_efm) {
            status = upload_ld_filter(0u, ld_options.efm_filter,
                                      bins_per_batch, "LD EFM filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (output_ld_analog_audio) {
            status = upload_ld_filter(1u, ld_options.audio_left_filter,
                                      ld_options.audio_left_bin_count,
                                      "LD analog-audio left filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = upload_ld_filter(2u, ld_options.audio_right_filter,
                                      ld_options.audio_right_bin_count,
                                      "LD analog-audio right filter upload");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
    }

    status = execute_r2c(context, workspace.input, workspace.base_spectrum,
                         static_cast<int>(job->sample_count),
                         static_cast<int>(job->batch_count), job->stream_index);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;

    if (output_ld_efm) {
        ld_efm_half_filter_kernel<<<launch_block_count(bin_count),
                                    kThreadsPerBlock, 0, stream>>>(
            workspace.base_spectrum,
            workspace.ld_filters[0],
            workspace.work_spectrum,
            job->sample_count,
            job->batch_count);
        status = check_kernel(context, "LD EFM half-spectrum filter kernel");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
        status = execute_c2r(context, workspace.work_spectrum, workspace.ld_efm,
                             static_cast<int>(job->sample_count),
                             static_cast<int>(job->batch_count), true,
                             job->stream_index);
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }

    if (output_ld_analog_audio) {
        auto execute_audio_channel = [&](std::uint32_t low_bin,
                                         std::uint32_t audio_bin_count,
                                         std::size_t audio_bytes,
                                         std::uint32_t filter_slot,
                                         vhsdecode_cuda_complex64* host_output,
                                         const char* operation) -> vhsdecode_cuda_status {
            const std::uint64_t audio_element_count =
                static_cast<std::uint64_t>(audio_bin_count) * job->batch_count;
            ld_audio_slice_filter_kernel<<<launch_block_count(audio_element_count),
                                           kThreadsPerBlock, 0, stream>>>(
                workspace.base_spectrum,
                workspace.ld_filters[filter_slot],
                workspace.work_spectrum,
                job->sample_count,
                job->batch_count,
                low_bin,
                audio_bin_count);
            auto audio_status = check_kernel(context, operation);
            if (audio_status != VHSDECODE_CUDA_SUCCESS) return audio_status;
            audio_status = execute_c2c(
                context,
                workspace.work_spectrum,
                workspace.work_spectrum,
                static_cast<int>(audio_bin_count),
                static_cast<int>(job->batch_count),
                CUFFT_INVERSE,
                true,
                job->stream_index);
            if (audio_status != VHSDECODE_CUDA_SUCCESS) return audio_status;
            return map_cuda_error(
                context,
                cudaMemcpyAsync(host_output, workspace.work_spectrum, audio_bytes,
                                cudaMemcpyDeviceToHost, stream),
                operation);
        };

        status = execute_audio_channel(
            ld_options.audio_left_low_bin,
            ld_options.audio_left_bin_count,
            left_audio_bytes,
            1u,
            workspace.host_ld_audio[0],
            "LD analog-audio left slice/filter/iFFT");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
        status = execute_audio_channel(
            ld_options.audio_right_low_bin,
            ld_options.audio_right_bin_count,
            right_audio_bytes,
            2u,
            workspace.host_ld_audio[1],
            "LD analog-audio right slice/filter/iFFT");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }

    if (is_cvbs) {
        status = execute_c2r(context, workspace.base_spectrum, workspace.analytic_real,
                             static_cast<int>(job->sample_count),
                             static_cast<int>(job->batch_count), true,
                             job->stream_index);
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
        affine_abs_kernel<<<launch_block_count(sample_count), kThreadsPerBlock, 0, stream>>>(
            workspace.analytic_real, workspace.envelope, sample_count,
            job->cvbs_raw_scale, job->cvbs_raw_offset);
        status = check_kernel(context, "CVBS affine_abs_kernel");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;

        if (output_video_low_pass) {
            status = execute_r2c(context, workspace.analytic_real,
                                 workspace.base_spectrum,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count),
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.work_spectrum, workspace.base_spectrum,
                                spectrum_bytes, cudaMemcpyDeviceToDevice, stream),
                "CVBS low-pass spectrum copy");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = launch_frequency_multiply(
                context, workspace.work_spectrum, workspace.filters[4],
                bins_per_batch, job->batch_count, 0, job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = execute_c2r(context, workspace.work_spectrum,
                                 workspace.video_low_pass,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count), true,
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
    } else {
        if (output_high_pass) {
            status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.work_spectrum, workspace.base_spectrum,
                                spectrum_bytes, cudaMemcpyDeviceToDevice, stream),
                "RF high-pass spectrum copy");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = launch_frequency_multiply(
                context, workspace.work_spectrum, workspace.filters[1],
                bins_per_batch, job->batch_count, 0, job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = execute_c2r(context, workspace.work_spectrum,
                                 workspace.rf_high_pass,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count), true,
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }

        const bool needs_analytic = requests_analytic_graph;
        if (needs_analytic) {
            status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.work_spectrum, workspace.base_spectrum,
                                spectrum_bytes, cudaMemcpyDeviceToDevice, stream),
                "RF video spectrum copy");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = launch_frequency_multiply(
                context, workspace.work_spectrum, workspace.filters[0],
                bins_per_batch, job->batch_count, 0, job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            if (apply_mtf) {
                status = launch_frequency_multiply(
                    context, workspace.work_spectrum, workspace.filters[2],
                    bins_per_batch, job->batch_count, 0, job->stream_index);
                if (status != VHSDECODE_CUDA_SUCCESS) return status;
            }
            status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.alternate_spectrum, workspace.work_spectrum,
                                spectrum_bytes, cudaMemcpyDeviceToDevice, stream),
                "Hilbert spectrum copy");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = execute_c2r(context, workspace.work_spectrum,
                                 workspace.analytic_real,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count), true,
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            hilbert_r2c_kernel<<<launch_block_count(bin_count), kThreadsPerBlock, 0, stream>>>(
                workspace.alternate_spectrum, job->sample_count, job->batch_count);
            status = check_kernel(context, "RF batch hilbert_r2c_kernel");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = execute_c2r(context, workspace.alternate_spectrum,
                                 workspace.analytic_imag,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count), true,
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }

        if (output_envelope) {
            envelope_kernel<<<launch_block_count(sample_count), kThreadsPerBlock, 0, stream>>>(
                workspace.analytic_real, workspace.analytic_imag,
                workspace.envelope, sample_count);
            status = check_kernel(context, "RF batch envelope_kernel");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }

        const bool needs_demod = output_demod || output_video || output_video_low_pass;
        if (needs_demod) {
            if (is_vhs_rust) {
                constexpr double tau = 6.283185307179586476925286766559;
                const float frequency_hz = static_cast<float>(job->demod_phase_scale * tau);
                vhs_rust_demod_kernel<<<launch_block_count(sample_count),
                                        kThreadsPerBlock, 0, stream>>>(
                    workspace.analytic_real, workspace.analytic_imag,
                    workspace.demod, job->sample_count, job->batch_count,
                    frequency_hz);
                status = check_kernel(context, "RF batch vhs_rust_demod_kernel");
            } else {
                conjugate_phase_split_kernel<<<launch_block_count(sample_count),
                                                kThreadsPerBlock, 0, stream>>>(
                    workspace.analytic_real, workspace.analytic_imag,
                    workspace.demod, job->sample_count, job->batch_count,
                    job->previous_analytic_per_batch == nullptr
                        ? nullptr
                        : workspace.previous,
                    job->last_analytic_per_batch == nullptr ? nullptr : workspace.last,
                    job->demod_phase_scale);
                status = check_kernel(context, "RF batch conjugate_phase_split_kernel");
            }
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }

        if (output_video || output_video_low_pass) {
            status = execute_r2c(context, workspace.demod, workspace.base_spectrum,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count),
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (output_video) {
            status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.work_spectrum, workspace.base_spectrum,
                                spectrum_bytes, cudaMemcpyDeviceToDevice, stream),
                "Video spectrum copy");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = launch_frequency_multiply(
                context, workspace.work_spectrum, workspace.filters[3],
                bins_per_batch, job->batch_count, 0, job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = execute_c2r(context, workspace.work_spectrum, workspace.video,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count), true,
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
        if (output_video_low_pass) {
            status = map_cuda_error(
                context,
                cudaMemcpyAsync(workspace.work_spectrum, workspace.base_spectrum,
                                spectrum_bytes, cudaMemcpyDeviceToDevice, stream),
                "Video low-pass spectrum copy");
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = launch_frequency_multiply(
                context, workspace.work_spectrum, workspace.filters[4],
                bins_per_batch, job->batch_count, 0, job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
            status = execute_c2r(context, workspace.work_spectrum,
                                 workspace.video_low_pass,
                                 static_cast<int>(job->sample_count),
                                 static_cast<int>(job->batch_count), true,
                                 job->stream_index);
            if (status != VHSDECODE_CUDA_SUCCESS) return status;
        }
    }

    auto download = [&](const double* source, double* destination,
                        const char* operation) -> vhsdecode_cuda_status {
        return map_cuda_error(
            context,
            cudaMemcpyAsync(destination, source, sample_bytes,
                            cudaMemcpyDeviceToHost, stream),
            operation);
    };

    if (output_high_pass) {
        status = download(workspace.rf_high_pass, host_high_pass,
                          "RF high-pass output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (output_analytic) {
        status = download(workspace.analytic_real, host_analytic_real,
                          "Analytic real output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
        status = download(workspace.analytic_imag, host_analytic_imag,
                          "Analytic imaginary output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (output_envelope) {
        status = download(workspace.envelope, host_envelope,
                          "Envelope output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (output_demod) {
        const double* source = is_cvbs ? workspace.analytic_real : workspace.demod;
        status = download(source, host_demod, "Demod raw output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (output_video) {
        const double* source = is_cvbs ? workspace.analytic_real : workspace.video;
        status = download(source, host_video, "Video output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (output_video_low_pass) {
        status = download(workspace.video_low_pass, host_video_low_pass,
                          "Video low-pass output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (output_ld_efm) {
        status = map_cuda_error(
            context,
            cudaMemcpyAsync(workspace.host_ld_efm, workspace.ld_efm, sample_bytes,
                            cudaMemcpyDeviceToHost, stream),
            "LD EFM output download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (job->last_analytic_per_batch != nullptr) {
        status = map_cuda_error(
            context,
            cudaMemcpyAsync(host_last, workspace.last, state_bytes,
                            cudaMemcpyDeviceToHost, stream),
            "Last analytic state download");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }

    status = map_cuda_error(context, cudaStreamSynchronize(stream),
                            "RF batch completion synchronize");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;

    if (output_high_pass) std::memcpy(job->rf_high_pass_output, host_high_pass, sample_bytes);
    if (output_analytic) {
        std::memcpy(job->analytic_real_output, host_analytic_real, sample_bytes);
        std::memcpy(job->analytic_imag_output, host_analytic_imag, sample_bytes);
    }
    if (output_envelope) std::memcpy(job->envelope_output, host_envelope, sample_bytes);
    if (output_demod) std::memcpy(job->demod_raw_output, host_demod, sample_bytes);
    if (output_video) std::memcpy(job->video_output, host_video, sample_bytes);
    if (output_video_low_pass) {
        std::memcpy(job->video_low_pass_output, host_video_low_pass, sample_bytes);
    }
    if (output_ld_efm) {
        std::memcpy(ld_options.efm_output, workspace.host_ld_efm, sample_bytes);
    }
    if (output_ld_analog_audio) {
        std::memcpy(ld_options.audio_left_output,
                    workspace.host_ld_audio[0], left_audio_bytes);
        std::memcpy(ld_options.audio_right_output,
                    workspace.host_ld_audio[1], right_audio_bytes);
    }
    if (job->last_analytic_per_batch != nullptr) {
        std::memcpy(job->last_analytic_per_batch, host_last, state_bytes);
    }
    return VHSDECODE_CUDA_SUCCESS;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_self_test(vhsdecode_cuda_context* context,
                         vhsdecode_cuda_self_test_metrics* metrics)
{
    if (context == nullptr || metrics == nullptr ||
        metrics->struct_size < sizeof(*metrics)) {
        return fail(context, VHSDECODE_CUDA_ABI_MISMATCH,
                    "Self-test metrics structure is missing or smaller than ABI v1");
    }

    vhsdecode_cuda_self_test_metrics output{};
    output.struct_size = sizeof(output);
    output.sample_count = 32768;
    output.cuda_status = VHSDECODE_CUDA_SUCCESS;
    *metrics = output;

    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        metrics->cuda_status = status;
        return status;
    }

    constexpr std::uint32_t sample_count = 32768;
    constexpr std::uint64_t bin_count = (sample_count / 2u) + 1u;
    constexpr std::size_t sample_bytes = sample_count * sizeof(double);
    constexpr std::size_t spectrum_bytes = bin_count * sizeof(cufftDoubleComplex);
    double* host_input = nullptr;
    double* host_output = nullptr;
    double* device_samples = nullptr;
    cufftDoubleComplex* device_spectrum = nullptr;

    auto cleanup = [&]() {
        if (device_spectrum != nullptr) cudaFree(device_spectrum);
        if (device_samples != nullptr) cudaFree(device_samples);
        if (host_output != nullptr) cudaFreeHost(host_output);
        if (host_input != nullptr) cudaFreeHost(host_input);
    };
    auto allocation_failure = [&](cudaError_t error, const char* operation) {
        const auto mapped = map_cuda_error(context, error, operation);
        cleanup();
        metrics->cuda_status = mapped;
        return mapped;
    };

    cudaError_t error = cudaHostAlloc(reinterpret_cast<void**>(&host_input),
                                      sample_bytes, cudaHostAllocDefault);
    if (error != cudaSuccess) return allocation_failure(error, "Self-test input allocation");
    error = cudaHostAlloc(reinterpret_cast<void**>(&host_output),
                          sample_bytes, cudaHostAllocDefault);
    if (error != cudaSuccess) return allocation_failure(error, "Self-test output allocation");
    error = cudaMalloc(reinterpret_cast<void**>(&device_samples), sample_bytes);
    if (error != cudaSuccess) return allocation_failure(error, "Self-test sample allocation");
    error = cudaMalloc(reinterpret_cast<void**>(&device_spectrum), spectrum_bytes);
    if (error != cudaSuccess) return allocation_failure(error, "Self-test spectrum allocation");

    constexpr double tau = 6.283185307179586476925286766559;
    for (std::uint32_t index = 0; index < sample_count; ++index) {
        const double phase = tau * index / sample_count;
        host_input[index] =
            (0.75 * std::sin(phase * 37.0)) +
            (0.20 * std::cos(phase * 211.0)) +
            (0.05 * std::sin(phase * 997.0));
    }

    cudaStream_t stream = context->streams[0];
    error = cudaMemcpyAsync(device_samples, host_input, sample_bytes,
                            cudaMemcpyHostToDevice, stream);
    if (error != cudaSuccess) return allocation_failure(error, "Self-test input upload");
    status = execute_r2c(context, device_samples, device_spectrum,
                         sample_count, 1, 0);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        cleanup();
        metrics->cuda_status = status;
        return status;
    }
    status = execute_c2r(context, device_spectrum, device_samples,
                         sample_count, 1, true, 0);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        cleanup();
        metrics->cuda_status = status;
        return status;
    }
    error = cudaMemcpyAsync(host_output, device_samples, sample_bytes,
                            cudaMemcpyDeviceToHost, stream);
    if (error != cudaSuccess) return allocation_failure(error, "Self-test output download");
    error = cudaStreamSynchronize(stream);
    if (error != cudaSuccess) return allocation_failure(error, "Self-test synchronize");

    double maximum_reference = 0.0;
    double maximum_error = 0.0;
    double squared_error = 0.0;
    double squared_reference = 0.0;
    bool finite = true;
    for (std::uint32_t index = 0; index < sample_count; ++index) {
        const double difference = host_output[index] - host_input[index];
        finite = finite && std::isfinite(host_output[index]);
        maximum_reference = std::max(maximum_reference, std::abs(host_input[index]));
        maximum_error = std::max(maximum_error, std::abs(difference));
        squared_error += difference * difference;
        squared_reference += host_input[index] * host_input[index];
    }
    cleanup();

    std::unique_ptr<double[]> rf_input(new (std::nothrow) double[sample_count]);
    std::unique_ptr<vhsdecode_cuda_complex64[]> identity_filter(
        new (std::nothrow) vhsdecode_cuda_complex64[bin_count]{});
    std::unique_ptr<double[]> rf_high_pass(new (std::nothrow) double[sample_count]);
    std::unique_ptr<double[]> analytic_real(new (std::nothrow) double[sample_count]);
    std::unique_ptr<double[]> analytic_imaginary(new (std::nothrow) double[sample_count]);
    std::unique_ptr<double[]> envelope(new (std::nothrow) double[sample_count]);
    std::unique_ptr<double[]> demod(new (std::nothrow) double[sample_count]);
    std::unique_ptr<double[]> video(new (std::nothrow) double[sample_count]);
    std::unique_ptr<double[]> video_low_pass(new (std::nothrow) double[sample_count]);
    std::unique_ptr<vhsdecode_cuda_complex64[]> efm_filter(
        new (std::nothrow) vhsdecode_cuda_complex64[bin_count]{});
    std::unique_ptr<double[]> efm_output(new (std::nothrow) double[sample_count]);
    constexpr std::uint32_t audio_bin_count = 64;
    std::unique_ptr<vhsdecode_cuda_complex64[]> audio_left_filter(
        new (std::nothrow) vhsdecode_cuda_complex64[audio_bin_count]{});
    std::unique_ptr<vhsdecode_cuda_complex64[]> audio_right_filter(
        new (std::nothrow) vhsdecode_cuda_complex64[audio_bin_count]{});
    std::unique_ptr<vhsdecode_cuda_complex64[]> audio_left_output(
        new (std::nothrow) vhsdecode_cuda_complex64[audio_bin_count]);
    std::unique_ptr<vhsdecode_cuda_complex64[]> audio_right_output(
        new (std::nothrow) vhsdecode_cuda_complex64[audio_bin_count]);
    if (!rf_input || !identity_filter || !rf_high_pass || !analytic_real ||
        !analytic_imaginary || !envelope || !demod || !video ||
        !video_low_pass || !efm_filter || !efm_output || !audio_left_filter ||
        !audio_right_filter || !audio_left_output || !audio_right_output) {
        metrics->cuda_status = VHSDECODE_CUDA_ALLOCATION_FAILED;
        return fail(context, VHSDECODE_CUDA_ALLOCATION_FAILED,
                    "Unable to allocate the 32K RF graph self-test vectors");
    }

    constexpr std::uint32_t self_test_tone = 37;
    const double expected_phase = tau * self_test_tone / sample_count;
    for (std::uint32_t index = 0; index < sample_count; ++index) {
        rf_input[index] = std::cos(tau * self_test_tone * index / sample_count);
    }
    for (std::uint64_t bin = 0; bin < bin_count; ++bin) {
        identity_filter[bin].real = 1.0;
    }
    efm_filter[self_test_tone].real = 2.0;
    constexpr std::uint32_t audio_left_low_bin = 32;
    constexpr std::uint32_t audio_right_low_bin = 16;
    constexpr double audio_tone_scale =
        (2.0 * audio_bin_count) / sample_count;
    audio_left_filter[self_test_tone - audio_left_low_bin].real =
        audio_tone_scale;
    audio_right_filter[self_test_tone - audio_right_low_bin].real =
        audio_tone_scale;

    vhsdecode_cuda_ld_frequency_options ld_options{};
    ld_options.struct_size = sizeof(ld_options);
    ld_options.audio_left_low_bin = audio_left_low_bin;
    ld_options.audio_left_bin_count = audio_bin_count;
    ld_options.audio_right_low_bin = audio_right_low_bin;
    ld_options.audio_right_bin_count = audio_bin_count;
    ld_options.efm_filter = efm_filter.get();
    ld_options.audio_left_filter = audio_left_filter.get();
    ld_options.audio_right_filter = audio_right_filter.get();
    ld_options.efm_output = efm_output.get();
    ld_options.audio_left_output = audio_left_output.get();
    ld_options.audio_right_output = audio_right_output.get();

    vhsdecode_cuda_rf_batch_job rf_job{};
    rf_job.struct_size = sizeof(rf_job);
    rf_job.flags = VHSDECODE_CUDA_RF_OUTPUT_HIGH_PASS |
        VHSDECODE_CUDA_RF_OUTPUT_ANALYTIC |
        VHSDECODE_CUDA_RF_OUTPUT_ENVELOPE |
        VHSDECODE_CUDA_RF_OUTPUT_DEMOD_RAW |
        VHSDECODE_CUDA_RF_OUTPUT_VIDEO |
        VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS |
        VHSDECODE_CUDA_RF_APPLY_MTF |
        VHSDECODE_CUDA_RF_OUTPUT_LD_EFM |
        VHSDECODE_CUDA_RF_OUTPUT_LD_ANALOG_AUDIO;
    rf_job.sample_count = sample_count;
    rf_job.batch_count = 1;
    rf_job.mode = VHSDECODE_CUDA_RF_MODE_STANDARD_CONJUGATE;
    rf_job.demod_phase_scale = 1.0;
    rf_job.input = rf_input.get();
    rf_job.rf_video_filter = identity_filter.get();
    rf_job.rf_high_pass_filter = identity_filter.get();
    rf_job.mtf_filter = identity_filter.get();
    rf_job.demod_video_filter = identity_filter.get();
    rf_job.demod_video_low_pass_filter = identity_filter.get();
    rf_job.rf_high_pass_output = rf_high_pass.get();
    rf_job.analytic_real_output = analytic_real.get();
    rf_job.analytic_imag_output = analytic_imaginary.get();
    rf_job.envelope_output = envelope.get();
    rf_job.demod_raw_output = demod.get();
    rf_job.video_output = video.get();
    rf_job.video_low_pass_output = video_low_pass.get();
    rf_job.reserved[VHSDECODE_CUDA_RF_LD_OPTIONS_RESERVED_INDEX] =
        static_cast<std::uint64_t>(
            reinterpret_cast<std::uintptr_t>(&ld_options));
    status = vhsdecode_cuda_rf_batch_execute(context, &rf_job);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        metrics->cuda_status = status;
        return status;
    }

    auto accumulate_error = [&](double actual, double expected) {
        const double difference = actual - expected;
        finite = finite && std::isfinite(actual);
        maximum_reference = std::max(maximum_reference, std::abs(expected));
        maximum_error = std::max(maximum_error, std::abs(difference));
        squared_error += difference * difference;
        squared_reference += expected * expected;
    };
    for (std::uint32_t index = 0; index < sample_count; ++index) {
        const double angle = tau * self_test_tone * index / sample_count;
        const double expected_real = std::cos(angle);
        const double expected_imaginary = std::sin(angle);
        const double expected_demod = index == 0 ? 0.0 : expected_phase;
        accumulate_error(rf_high_pass[index], expected_real);
        accumulate_error(analytic_real[index], expected_real);
        accumulate_error(analytic_imaginary[index], expected_imaginary);
        accumulate_error(envelope[index], 1.0);
        accumulate_error(demod[index], expected_demod);
        accumulate_error(video[index], expected_demod);
        accumulate_error(video_low_pass[index], expected_demod);
        accumulate_error(efm_output[index], expected_real);
    }
    for (std::uint32_t index = 0; index < audio_bin_count; ++index) {
        const double left_angle = tau *
            (self_test_tone - audio_left_low_bin) * index / audio_bin_count;
        const double right_angle = tau *
            (self_test_tone - audio_right_low_bin) * index / audio_bin_count;
        accumulate_error(audio_left_output[index].real, std::cos(left_angle));
        accumulate_error(audio_left_output[index].imag, std::sin(left_angle));
        accumulate_error(audio_right_output[index].real, std::cos(right_angle));
        accumulate_error(audio_right_output[index].imag, std::sin(right_angle));
    }
    // The synthetic LD filters cannot be reused by a real capture. Release
    // this optional workspace so VHS/CVBS tasks do not retain LD-only memory.
    reset_ld_workspace(context->workspaces[0]);

    metrics->max_abs_error = maximum_reference == 0.0
        ? maximum_error
        : maximum_error / maximum_reference;
    metrics->nrmse = squared_reference == 0.0
        ? std::sqrt(squared_error / sample_count)
        : std::sqrt(squared_error / squared_reference);
    metrics->passed = finite && metrics->max_abs_error <= 1e-9 &&
        metrics->nrmse <= 1e-11;
    if (metrics->passed == 0) {
        metrics->cuda_status = VHSDECODE_CUDA_SELF_TEST_FAILED;
        return fail(context, VHSDECODE_CUDA_SELF_TEST_FAILED,
                    "32K FP64 cuFFT/RF graph self-test exceeded the compatibility tolerance");
    }
    return VHSDECODE_CUDA_SUCCESS;
}

} // extern "C"

namespace {

template <typename T>
T* offset_pointer(T* pointer, std::uint64_t element_offset)
{
    return pointer == nullptr ? nullptr : pointer + element_offset;
}

vhsdecode_cuda_status execute_rf_sub_batch(
    vhsdecode_cuda_context* context,
    const vhsdecode_cuda_rf_batch_job* job)
{
    const bool prior_sub_call = g_rf_batch_sub_call;
    g_rf_batch_sub_call = true;
    try {
        const auto status = vhsdecode_cuda_rf_batch_execute(context, job);
        g_rf_batch_sub_call = prior_sub_call;
        return status;
    } catch (const std::exception& error) {
        g_rf_batch_sub_call = prior_sub_call;
        return fail(context, VHSDECODE_CUDA_INTERNAL_ERROR,
                    std::string("RF split sub-batch raised an exception: ") +
                        error.what());
    } catch (...) {
        g_rf_batch_sub_call = prior_sub_call;
        return fail(context, VHSDECODE_CUDA_INTERNAL_ERROR,
                    "RF split sub-batch raised an unknown exception");
    }
}

vhsdecode_cuda_status execute_rf_batch_split(
    vhsdecode_cuda_context* context,
    const vhsdecode_cuda_rf_batch_job* job,
    const vhsdecode_cuda_ld_frequency_options* ld_options)
{
    const std::uint32_t first_batch_count = job->batch_count / 2u;
    const std::uint32_t second_batch_count =
        job->batch_count - first_batch_count;
    const std::uint64_t second_sample_offset =
        static_cast<std::uint64_t>(job->sample_count) * first_batch_count;

    vhsdecode_cuda_rf_batch_job first_job = *job;
    vhsdecode_cuda_rf_batch_job second_job = *job;
    first_job.batch_count = first_batch_count;
    first_job.stream_index = 0u;
    second_job.batch_count = second_batch_count;
    second_job.stream_index = 1u;
    second_job.input = offset_pointer(job->input, second_sample_offset);

    if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_HIGH_PASS) != 0) {
        second_job.rf_high_pass_output = offset_pointer(
            job->rf_high_pass_output, second_sample_offset);
    }
    if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_ANALYTIC) != 0) {
        second_job.analytic_real_output = offset_pointer(
            job->analytic_real_output, second_sample_offset);
        second_job.analytic_imag_output = offset_pointer(
            job->analytic_imag_output, second_sample_offset);
    }
    if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_ENVELOPE) != 0) {
        second_job.envelope_output = offset_pointer(
            job->envelope_output, second_sample_offset);
    }
    if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_DEMOD_RAW) != 0) {
        second_job.demod_raw_output = offset_pointer(
            job->demod_raw_output, second_sample_offset);
    }
    if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_VIDEO) != 0) {
        second_job.video_output = offset_pointer(
            job->video_output, second_sample_offset);
    }
    if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS) != 0) {
        second_job.video_low_pass_output = offset_pointer(
            job->video_low_pass_output, second_sample_offset);
    }
    if (job->previous_analytic_per_batch != nullptr) {
        second_job.previous_analytic_per_batch = offset_pointer(
            job->previous_analytic_per_batch, first_batch_count);
    }
    if (job->last_analytic_per_batch != nullptr) {
        second_job.last_analytic_per_batch = offset_pointer(
            job->last_analytic_per_batch, first_batch_count);
    }

    vhsdecode_cuda_ld_frequency_options first_ld_options{};
    vhsdecode_cuda_ld_frequency_options second_ld_options{};
    if (ld_options != nullptr) {
        first_ld_options = *ld_options;
        second_ld_options = *ld_options;
        if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_LD_EFM) != 0) {
            second_ld_options.efm_output = offset_pointer(
                ld_options->efm_output, second_sample_offset);
        }
        if ((job->flags & VHSDECODE_CUDA_RF_OUTPUT_LD_ANALOG_AUDIO) != 0) {
            const std::uint64_t left_offset =
                static_cast<std::uint64_t>(ld_options->audio_left_bin_count) *
                first_batch_count;
            const std::uint64_t right_offset =
                static_cast<std::uint64_t>(ld_options->audio_right_bin_count) *
                first_batch_count;
            second_ld_options.audio_left_output = offset_pointer(
                ld_options->audio_left_output, left_offset);
            second_ld_options.audio_right_output = offset_pointer(
                ld_options->audio_right_output, right_offset);
        }
        first_job.reserved[VHSDECODE_CUDA_RF_LD_OPTIONS_RESERVED_INDEX] =
            static_cast<std::uint64_t>(
                reinterpret_cast<std::uintptr_t>(&first_ld_options));
        second_job.reserved[VHSDECODE_CUDA_RF_LD_OPTIONS_RESERVED_INDEX] =
            static_cast<std::uint64_t>(
                reinterpret_cast<std::uintptr_t>(&second_ld_options));
    }

    vhsdecode_cuda_status first_status = VHSDECODE_CUDA_INTERNAL_ERROR;
    vhsdecode_cuda_status second_status = VHSDECODE_CUDA_INTERNAL_ERROR;
    std::thread first_thread;
    try {
        first_thread = std::thread([&]() {
            first_status = execute_rf_sub_batch(context, &first_job);
        });
    } catch (const std::system_error&) {
        // A worker-thread resource failure must not change output semantics.
        // No thread was started when construction throws, so run both halves
        // synchronously in their original block order.
        first_status = execute_rf_sub_batch(context, &first_job);
        if (first_status == VHSDECODE_CUDA_SUCCESS) {
            second_status = execute_rf_sub_batch(context, &second_job);
        }
        return first_status == VHSDECODE_CUDA_SUCCESS
            ? second_status
            : first_status;
    }

    second_status = execute_rf_sub_batch(context, &second_job);
    first_thread.join();

    if (first_status != VHSDECODE_CUDA_SUCCESS) {
        return first_status;
    }
    return second_status;
}

} // namespace

extern "C" {

uint32_t VHSDECODE_CUDA_CALL vhsdecode_cuda_get_abi_version(void)
{
    return VHSDECODE_CUDA_ABI_VERSION;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_device_count(int32_t* device_count)
{
    if (device_count == nullptr) {
        return fail(nullptr, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Device count output is null");
    }
    *device_count = 0;
    int count = 0;
    const auto status = map_cuda_error(nullptr, cudaGetDeviceCount(&count),
                                       "cudaGetDeviceCount");
    if (status == VHSDECODE_CUDA_SUCCESS) {
        *device_count = count;
    }
    return status;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_device_info(int32_t device_ordinal,
                               vhsdecode_cuda_device_info* device_info)
{
    if (device_info == nullptr || device_info->struct_size < sizeof(*device_info)) {
        return fail(nullptr, VHSDECODE_CUDA_ABI_MISMATCH,
                    "Device info structure is missing or smaller than ABI v1");
    }

    int count = 0;
    auto status = map_cuda_error(nullptr, cudaGetDeviceCount(&count),
                                 "cudaGetDeviceCount");
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }
    if (device_ordinal < 0 || device_ordinal >= count) {
        return fail(nullptr, VHSDECODE_CUDA_DEVICE_NOT_FOUND,
                    "CUDA device ordinal is outside the visible device range");
    }

    cudaDeviceProp properties{};
    status = map_cuda_error(nullptr,
                            cudaGetDeviceProperties(&properties, device_ordinal),
                            "cudaGetDeviceProperties");
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }

    status = map_cuda_error(nullptr, cudaSetDevice(device_ordinal),
                            "cudaSetDevice for memory information");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    std::size_t free_global_memory = 0;
    std::size_t total_global_memory = 0;
    status = map_cuda_error(nullptr,
                            cudaMemGetInfo(&free_global_memory,
                                           &total_global_memory),
                            "cudaMemGetInfo");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;

    int driver_version = 0;
    int runtime_version = 0;
    int cufft_version = 0;
    status = map_cuda_error(nullptr, cudaDriverGetVersion(&driver_version),
                            "cudaDriverGetVersion");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = map_cuda_error(nullptr, cudaRuntimeGetVersion(&runtime_version),
                            "cudaRuntimeGetVersion");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    const cufftResult cufft_status = cufftGetVersion(&cufft_version);
    if (cufft_status != CUFFT_SUCCESS) {
        return map_cufft_error(nullptr, cufft_status, "cufftGetVersion");
    }

    vhsdecode_cuda_device_info output{};
    output.struct_size = sizeof(output);
    output.ordinal = device_ordinal;
    output.compute_capability_major = properties.major;
    output.compute_capability_minor = properties.minor;
    output.driver_version = driver_version;
    output.runtime_version = runtime_version;
    output.cufft_version = cufft_version;
    output.total_global_memory = static_cast<std::uint64_t>(total_global_memory);
    output.flags = VHSDECODE_CUDA_DEVICE_FLAG_FP64 |
        VHSDECODE_CUDA_DEVICE_FLAG_MEMORY_INFO;
    const std::uint64_t free_memory =
        static_cast<std::uint64_t>(free_global_memory);
    output.reserved[VHSDECODE_CUDA_DEVICE_INFO_FREE_MEMORY_LOW_RESERVED_INDEX] =
        static_cast<std::uint32_t>(free_memory & UINT64_C(0xffffffff));
    output.reserved[VHSDECODE_CUDA_DEVICE_INFO_FREE_MEMORY_HIGH_RESERVED_INDEX] =
        static_cast<std::uint32_t>(free_memory >> 32u);
    if (properties.asyncEngineCount > 0) {
        output.flags |= VHSDECODE_CUDA_DEVICE_FLAG_CONCURRENT_COPY_COMPUTE;
    }
    std::strncpy(output.name, properties.name, sizeof(output.name) - 1u);
    *device_info = output;
    return VHSDECODE_CUDA_SUCCESS;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_create(int32_t device_ordinal, vhsdecode_cuda_context** context)
{
    if (context == nullptr) {
        return fail(nullptr, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "CUDA context output is null");
    }
    *context = nullptr;

    vhsdecode_cuda_device_info info{};
    info.struct_size = sizeof(info);
    auto status = vhsdecode_cuda_get_device_info(device_ordinal, &info);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }
    if ((info.compute_capability_major * 10) + info.compute_capability_minor <
        kMinimumComputeCapability) {
        return fail(nullptr, VHSDECODE_CUDA_DEVICE_UNSUPPORTED,
                    "CUDA device compute capability is below SM 7.5");
    }
    if (info.driver_version < kMinimumCudaDriverApi ||
        info.runtime_version < kMinimumCudaDriverApi) {
        return fail(nullptr, VHSDECODE_CUDA_DEVICE_UNSUPPORTED,
                    "CUDA driver/runtime does not support the CUDA 13.0 sidecar");
    }

    status = map_cuda_error(nullptr, cudaSetDevice(device_ordinal), "cudaSetDevice");
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }

    auto* created = new (std::nothrow) vhsdecode_cuda_context();
    if (created == nullptr) {
        return fail(nullptr, VHSDECODE_CUDA_ALLOCATION_FAILED,
                    "Unable to allocate the native CUDA context");
    }
    created->device_ordinal = device_ordinal;

    for (std::uint32_t stream = 0; stream < kStreamCount; ++stream) {
        const cudaError_t stream_error = cudaStreamCreateWithFlags(
            &created->streams[stream], cudaStreamNonBlocking);
        if (stream_error != cudaSuccess) {
            status = map_cuda_error(created, stream_error, "cudaStreamCreateWithFlags");
            for (std::uint32_t prior = 0; prior < stream; ++prior) {
                cudaStreamDestroy(created->streams[prior]);
            }
            delete created;
            return status;
        }
    }

    *context = created;
    return VHSDECODE_CUDA_SUCCESS;
}

void VHSDECODE_CUDA_CALL vhsdecode_cuda_destroy(vhsdecode_cuda_context* context)
{
    if (context == nullptr) {
        return;
    }
    cudaSetDevice(context->device_ordinal);
    cudaDeviceSynchronize();
    for (auto& workspace : context->workspaces) {
        reset_workspace(workspace);
    }
    for (const auto& entry : context->plans) {
        cufftDestroy(entry.second);
    }
    for (auto& stream : context->streams) {
        if (stream != nullptr) {
            cudaStreamDestroy(stream);
        }
    }
    delete context;
}

const char* VHSDECODE_CUDA_CALL
vhsdecode_cuda_status_string(vhsdecode_cuda_status status)
{
    switch (status) {
    case VHSDECODE_CUDA_SUCCESS: return "success";
    case VHSDECODE_CUDA_INVALID_ARGUMENT: return "invalid argument";
    case VHSDECODE_CUDA_ABI_MISMATCH: return "ABI mismatch";
    case VHSDECODE_CUDA_UNAVAILABLE: return "CUDA unavailable";
    case VHSDECODE_CUDA_DEVICE_NOT_FOUND: return "device not found";
    case VHSDECODE_CUDA_DEVICE_UNSUPPORTED: return "device unsupported";
    case VHSDECODE_CUDA_ALLOCATION_FAILED: return "allocation failed";
    case VHSDECODE_CUDA_CUDA_ERROR: return "CUDA error";
    case VHSDECODE_CUDA_CUFFT_ERROR: return "cuFFT error";
    case VHSDECODE_CUDA_SELF_TEST_FAILED: return "self-test failed";
    case VHSDECODE_CUDA_BUFFER_TOO_SMALL: return "buffer too small";
    case VHSDECODE_CUDA_NOT_SUPPORTED: return "not supported";
    case VHSDECODE_CUDA_INTERNAL_ERROR: return "internal error";
    default: return "unknown status";
    }
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_last_error(vhsdecode_cuda_context* context,
                              char* destination,
                              uint64_t destination_size)
{
    if (destination == nullptr || destination_size == 0) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Error destination is null or empty");
    }

    std::string message;
    if (context != nullptr) {
        std::lock_guard lock(context->error_mutex);
        message = context->last_error;
    } else {
        message = g_last_error;
    }

    const std::uint64_t copy_count = std::min<std::uint64_t>(
        message.size(), destination_size - 1u);
    if (copy_count != 0) {
        std::memcpy(destination, message.data(), static_cast<std::size_t>(copy_count));
    }
    destination[copy_count] = '\0';
    return copy_count == message.size()
        ? VHSDECODE_CUDA_SUCCESS
        : VHSDECODE_CUDA_BUFFER_TOO_SMALL;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_get_capabilities(vhsdecode_cuda_context* context,
                                uint64_t* capabilities)
{
    if (context == nullptr || capabilities == nullptr) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Capability context or output is null");
    }
    *capabilities = kAllCapabilities;
    return VHSDECODE_CUDA_SUCCESS;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_host_alloc(vhsdecode_cuda_context* context,
                          uint64_t byte_count,
                          void** memory)
{
    if (context == nullptr || memory == nullptr || byte_count == 0 ||
        byte_count > std::numeric_limits<std::size_t>::max()) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Pinned host allocation arguments are invalid");
    }
    *memory = nullptr;
    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return map_cuda_error(context,
                          cudaHostAlloc(memory, static_cast<std::size_t>(byte_count),
                                        cudaHostAllocDefault),
                          "cudaHostAlloc");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_host_free(vhsdecode_cuda_context* context, void* memory)
{
    if (context == nullptr || memory == nullptr) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Pinned host free arguments are invalid");
    }
    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return map_cuda_error(context, cudaFreeHost(memory), "cudaFreeHost");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_create(vhsdecode_cuda_context* context,
                             uint64_t byte_count,
                             vhsdecode_cuda_buffer** buffer)
{
    if (context == nullptr || buffer == nullptr || byte_count == 0 ||
        byte_count > std::numeric_limits<std::size_t>::max()) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Device buffer allocation arguments are invalid");
    }
    *buffer = nullptr;
    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;

    auto* created = new (std::nothrow) vhsdecode_cuda_buffer();
    if (created == nullptr) {
        return fail(context, VHSDECODE_CUDA_ALLOCATION_FAILED,
                    "Unable to allocate the device buffer handle");
    }
    const cudaError_t error = cudaMalloc(&created->pointer,
                                          static_cast<std::size_t>(byte_count));
    if (error != cudaSuccess) {
        delete created;
        return map_cuda_error(context, error, "cudaMalloc");
    }
    created->owner = context;
    created->device_ordinal = context->device_ordinal;
    created->byte_count = byte_count;
    *buffer = created;
    return VHSDECODE_CUDA_SUCCESS;
}

void VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_destroy(vhsdecode_cuda_buffer* buffer)
{
    if (buffer == nullptr) {
        return;
    }
    cudaSetDevice(buffer->device_ordinal);
    if (buffer->pointer != nullptr) {
        cudaFree(buffer->pointer);
    }
    delete buffer;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_upload(vhsdecode_cuda_context* context,
                             vhsdecode_cuda_buffer* destination,
                             uint64_t destination_offset,
                             const void* source,
                             uint64_t byte_count,
                             uint32_t stream_index)
{
    if (context == nullptr || source == nullptr || !valid_stream(stream_index) ||
        !buffer_range_valid(destination, destination_offset, byte_count) ||
        destination->owner != context ||
        byte_count > std::numeric_limits<std::size_t>::max()) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Device upload arguments are invalid");
    }
    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    auto* pointer = static_cast<std::byte*>(destination->pointer) + destination_offset;
    return map_cuda_error(context,
                          cudaMemcpyAsync(pointer, source,
                                          static_cast<std::size_t>(byte_count),
                                          cudaMemcpyHostToDevice,
                                          context->streams[stream_index]),
                          "cudaMemcpyAsync host-to-device");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_buffer_download(vhsdecode_cuda_context* context,
                               const vhsdecode_cuda_buffer* source,
                               uint64_t source_offset,
                               void* destination,
                               uint64_t byte_count,
                               uint32_t stream_index)
{
    if (context == nullptr || destination == nullptr || !valid_stream(stream_index) ||
        !buffer_range_valid(source, source_offset, byte_count) ||
        source->owner != context ||
        byte_count > std::numeric_limits<std::size_t>::max()) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Device download arguments are invalid");
    }
    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    const auto* pointer = static_cast<const std::byte*>(source->pointer) + source_offset;
    status = map_cuda_error(context,
                            cudaMemcpyAsync(destination, pointer,
                                            static_cast<std::size_t>(byte_count),
                                            cudaMemcpyDeviceToHost,
                                            context->streams[stream_index]),
                            "cudaMemcpyAsync device-to-host");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return map_cuda_error(context,
                          cudaStreamSynchronize(context->streams[stream_index]),
                          "cudaStreamSynchronize after download");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_stream_synchronize(vhsdecode_cuda_context* context,
                                  uint32_t stream_index)
{
    if (context == nullptr || !valid_stream(stream_index)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "CUDA stream index is invalid");
    }
    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return map_cuda_error(context,
                          cudaStreamSynchronize(context->streams[stream_index]),
                          "cudaStreamSynchronize");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_synchronize(vhsdecode_cuda_context* context)
{
    auto status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return map_cuda_error(context, cudaDeviceSynchronize(), "cudaDeviceSynchronize");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_fft_r2c(vhsdecode_cuda_context* context,
                       const vhsdecode_cuda_buffer* input,
                       vhsdecode_cuda_buffer* output,
                       uint32_t sample_count,
                       uint32_t batch_count,
                       uint32_t stream_index)
{
    if (context == nullptr || sample_count == 0 || batch_count == 0 ||
        !valid_stream(stream_index) ||
        sample_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        batch_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max())) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "R2C transform dimensions or stream are invalid");
    }
    std::uint64_t input_count{};
    std::uint64_t output_count{};
    std::uint64_t input_bytes{};
    std::uint64_t output_bytes{};
    if (!multiply_size(sample_count, batch_count, &input_count) ||
        !multiply_size((sample_count / 2u) + 1u, batch_count, &output_count) ||
        !multiply_size(input_count, sizeof(double), &input_bytes) ||
        !multiply_size(output_count, sizeof(cufftDoubleComplex), &output_bytes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT, "R2C transform size overflow");
    }
    auto status = validate_buffer(context, input, input_bytes, "R2C input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, output, output_bytes, "R2C output");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return execute_r2c(context,
                       static_cast<double*>(input->pointer),
                       static_cast<cufftDoubleComplex*>(output->pointer),
                       static_cast<int>(sample_count),
                       static_cast<int>(batch_count),
                       stream_index);
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_fft_c2r(vhsdecode_cuda_context* context,
                       const vhsdecode_cuda_buffer* input,
                       vhsdecode_cuda_buffer* output,
                       uint32_t sample_count,
                       uint32_t batch_count,
                       uint32_t normalize_inverse,
                       uint32_t stream_index)
{
    if (context == nullptr || sample_count == 0 || batch_count == 0 ||
        !valid_stream(stream_index) ||
        sample_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        batch_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max())) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "C2R transform dimensions or stream are invalid");
    }
    std::uint64_t input_count{};
    std::uint64_t output_count{};
    std::uint64_t input_bytes{};
    std::uint64_t output_bytes{};
    if (!multiply_size((sample_count / 2u) + 1u, batch_count, &input_count) ||
        !multiply_size(sample_count, batch_count, &output_count) ||
        !multiply_size(input_count, sizeof(cufftDoubleComplex), &input_bytes) ||
        !multiply_size(output_count, sizeof(double), &output_bytes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT, "C2R transform size overflow");
    }
    auto status = validate_buffer(context, input, input_bytes, "C2R input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, output, output_bytes, "C2R output");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return execute_c2r(context,
                       static_cast<cufftDoubleComplex*>(input->pointer),
                       static_cast<double*>(output->pointer),
                       static_cast<int>(sample_count),
                       static_cast<int>(batch_count),
                       normalize_inverse != 0,
                       stream_index);
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_fft_c2c(vhsdecode_cuda_context* context,
                       const vhsdecode_cuda_buffer* input,
                       vhsdecode_cuda_buffer* output,
                       uint32_t sample_count,
                       uint32_t batch_count,
                       vhsdecode_cuda_fft_direction direction,
                       uint32_t normalize_inverse,
                       uint32_t stream_index)
{
    if (context == nullptr || sample_count == 0 || batch_count == 0 ||
        !valid_stream(stream_index) ||
        sample_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        batch_count > static_cast<std::uint32_t>(std::numeric_limits<int>::max()) ||
        (direction != VHSDECODE_CUDA_FFT_FORWARD &&
         direction != VHSDECODE_CUDA_FFT_INVERSE)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "C2C transform arguments are invalid");
    }
    std::uint64_t element_count{};
    std::uint64_t byte_count{};
    if (!multiply_size(sample_count, batch_count, &element_count) ||
        !multiply_size(element_count, sizeof(cufftDoubleComplex), &byte_count)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT, "C2C transform size overflow");
    }
    auto status = validate_buffer(context, input, byte_count, "C2C input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, output, byte_count, "C2C output");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;

    cufftHandle plan{};
    status = get_plan(context, CUFFT_Z2Z, static_cast<int>(sample_count),
                      static_cast<int>(batch_count), stream_index, &plan);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    const int cufft_direction = direction == VHSDECODE_CUDA_FFT_FORWARD
        ? CUFFT_FORWARD
        : CUFFT_INVERSE;
    status = map_cufft_error(
        context,
        cufftExecZ2Z(plan,
                     static_cast<cufftDoubleComplex*>(input->pointer),
                     static_cast<cufftDoubleComplex*>(output->pointer),
                     cufft_direction),
        "cufftExecZ2Z");
    if (status == VHSDECODE_CUDA_SUCCESS &&
        direction == VHSDECODE_CUDA_FFT_INVERSE && normalize_inverse != 0) {
        status = launch_scale_complex(context,
                                      static_cast<cufftDoubleComplex*>(output->pointer),
                                      element_count,
                                      1.0 / sample_count,
                                      stream_index);
    }
    return status;
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_frequency_multiply(vhsdecode_cuda_context* context,
                                  vhsdecode_cuda_buffer* spectrum,
                                  const vhsdecode_cuda_buffer* filter,
                                  uint64_t bins_per_batch,
                                  uint32_t batch_count,
                                  uint64_t filter_batch_stride,
                                  uint32_t stream_index)
{
    if (context == nullptr || bins_per_batch == 0 || batch_count == 0 ||
        !valid_stream(stream_index) ||
        (filter_batch_stride != 0 && filter_batch_stride < bins_per_batch)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Frequency multiply dimensions or stream are invalid");
    }
    std::uint64_t spectrum_count{};
    std::uint64_t filter_count = bins_per_batch;
    std::uint64_t spectrum_bytes{};
    std::uint64_t filter_bytes{};
    if (!multiply_size(bins_per_batch, batch_count, &spectrum_count)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Frequency multiply size overflow");
    }
    if (filter_batch_stride != 0) {
        if (!multiply_size(filter_batch_stride, batch_count - 1u, &filter_count) ||
            filter_count > std::numeric_limits<std::uint64_t>::max() - bins_per_batch) {
            return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                        "Frequency filter size overflow");
        }
        filter_count += bins_per_batch;
    }
    if (!multiply_size(spectrum_count, sizeof(cufftDoubleComplex), &spectrum_bytes) ||
        !multiply_size(filter_count, sizeof(cufftDoubleComplex), &filter_bytes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Frequency multiply byte size overflow");
    }
    auto status = validate_buffer(context, spectrum, spectrum_bytes, "Spectrum");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, filter, filter_bytes, "Frequency filter");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    return launch_frequency_multiply(
        context,
        static_cast<cufftDoubleComplex*>(spectrum->pointer),
        static_cast<const cufftDoubleComplex*>(filter->pointer),
        bins_per_batch,
        batch_count,
        filter_batch_stride,
        stream_index);
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_hilbert_r2c(vhsdecode_cuda_context* context,
                           vhsdecode_cuda_buffer* spectrum,
                           uint32_t sample_count,
                           uint32_t batch_count,
                           uint32_t stream_index)
{
    if (context == nullptr || sample_count < 2 || batch_count == 0 ||
        !valid_stream(stream_index)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Hilbert dimensions or stream are invalid");
    }
    const std::uint64_t bins = (sample_count / 2u) + 1u;
    std::uint64_t count{};
    std::uint64_t bytes{};
    if (!multiply_size(bins, batch_count, &count) ||
        !multiply_size(count, sizeof(cufftDoubleComplex), &bytes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Hilbert transform size overflow");
    }
    auto status = validate_buffer(context, spectrum, bytes, "Hilbert spectrum");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    hilbert_r2c_kernel<<<launch_block_count(count), kThreadsPerBlock, 0,
                         context->streams[stream_index]>>>(
        static_cast<cufftDoubleComplex*>(spectrum->pointer), sample_count, batch_count);
    return check_kernel(context, "hilbert_r2c_kernel");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_envelope(vhsdecode_cuda_context* context,
                        const vhsdecode_cuda_buffer* real_samples,
                        const vhsdecode_cuda_buffer* hilbert_samples,
                        vhsdecode_cuda_buffer* output,
                        uint64_t sample_count,
                        uint32_t stream_index)
{
    if (context == nullptr || sample_count == 0 || !valid_stream(stream_index) ||
        sample_count > std::numeric_limits<std::uint64_t>::max() / sizeof(double)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Envelope dimensions or stream are invalid");
    }
    const std::uint64_t bytes = sample_count * sizeof(double);
    auto status = validate_buffer(context, real_samples, bytes, "Envelope real input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, hilbert_samples, bytes, "Envelope imaginary input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, output, bytes, "Envelope output");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    envelope_kernel<<<launch_block_count(sample_count), kThreadsPerBlock, 0,
                      context->streams[stream_index]>>>(
        static_cast<const double*>(real_samples->pointer),
        static_cast<const double*>(hilbert_samples->pointer),
        static_cast<double*>(output->pointer),
        sample_count);
    return check_kernel(context, "envelope_kernel");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_conjugate_product_phase(
    vhsdecode_cuda_context* context,
    const vhsdecode_cuda_buffer* analytic_samples,
    vhsdecode_cuda_buffer* phase_output,
    uint32_t samples_per_batch,
    uint32_t batch_count,
    const vhsdecode_cuda_buffer* previous_per_batch,
    vhsdecode_cuda_buffer* last_per_batch,
    uint32_t stream_index)
{
    if (context == nullptr || samples_per_batch == 0 || batch_count == 0 ||
        !valid_stream(stream_index)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Conjugate-product dimensions or stream are invalid");
    }
    std::uint64_t count{};
    std::uint64_t analytic_bytes{};
    std::uint64_t output_bytes{};
    std::uint64_t state_bytes{};
    if (!multiply_size(samples_per_batch, batch_count, &count) ||
        !multiply_size(count, sizeof(cufftDoubleComplex), &analytic_bytes) ||
        !multiply_size(count, sizeof(double), &output_bytes) ||
        !multiply_size(batch_count, sizeof(cufftDoubleComplex), &state_bytes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Conjugate-product size overflow");
    }
    auto status = validate_buffer(context, analytic_samples, analytic_bytes,
                                  "Conjugate-product input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, phase_output, output_bytes,
                             "Conjugate-product output");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    if (previous_per_batch != nullptr) {
        status = validate_buffer(context, previous_per_batch, state_bytes,
                                 "Conjugate-product previous values");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    if (last_per_batch != nullptr) {
        status = validate_buffer(context, last_per_batch, state_bytes,
                                 "Conjugate-product last values");
        if (status != VHSDECODE_CUDA_SUCCESS) return status;
    }
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    conjugate_phase_kernel<<<launch_block_count(count), kThreadsPerBlock, 0,
                             context->streams[stream_index]>>>(
        static_cast<const cufftDoubleComplex*>(analytic_samples->pointer),
        static_cast<double*>(phase_output->pointer),
        samples_per_batch,
        batch_count,
        previous_per_batch == nullptr
            ? nullptr
            : static_cast<const cufftDoubleComplex*>(previous_per_batch->pointer),
        last_per_batch == nullptr
            ? nullptr
            : static_cast<cufftDoubleComplex*>(last_per_batch->pointer),
        1.0);
    return check_kernel(context, "conjugate_phase_kernel");
}

vhsdecode_cuda_status VHSDECODE_CUDA_CALL
vhsdecode_cuda_vhs_rust_demod(vhsdecode_cuda_context* context,
                              const vhsdecode_cuda_buffer* real_samples,
                              const vhsdecode_cuda_buffer* imaginary_samples,
                              vhsdecode_cuda_buffer* output,
                              uint32_t samples_per_batch,
                              uint32_t batch_count,
                              float frequency_hz,
                              uint32_t stream_index)
{
    if (context == nullptr || samples_per_batch == 0 || batch_count == 0 ||
        !valid_stream(stream_index) || !std::isfinite(frequency_hz) || frequency_hz <= 0.0f) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "VHS Rust demodulator arguments are invalid");
    }
    std::uint64_t count{};
    std::uint64_t bytes{};
    if (!multiply_size(samples_per_batch, batch_count, &count) ||
        !multiply_size(count, sizeof(double), &bytes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "VHS Rust demodulator size overflow");
    }
    auto status = validate_buffer(context, real_samples, bytes, "VHS Rust real input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, imaginary_samples, bytes, "VHS Rust imaginary input");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = validate_buffer(context, output, bytes, "VHS Rust output");
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    status = select_device(context);
    if (status != VHSDECODE_CUDA_SUCCESS) return status;
    vhs_rust_demod_kernel<<<launch_block_count(count), kThreadsPerBlock, 0,
                            context->streams[stream_index]>>>(
        static_cast<const double*>(real_samples->pointer),
        static_cast<const double*>(imaginary_samples->pointer),
        static_cast<double*>(output->pointer),
        samples_per_batch,
        batch_count,
        frequency_hz);
    return check_kernel(context, "vhs_rust_demod_kernel");
}

} // extern "C"

namespace {

vhsdecode_cuda_status fail(vhsdecode_cuda_context* context,
                           vhsdecode_cuda_status status,
                           const std::string& message)
{
    g_last_error = message;
    if (context != nullptr) {
        std::lock_guard lock(context->error_mutex);
        context->last_error = message;
    }
    return status;
}

const char* cuda_error_name(cudaError_t error)
{
    const char* name = cudaGetErrorName(error);
    return name == nullptr ? "unknown CUDA error" : name;
}

vhsdecode_cuda_status map_cuda_error(vhsdecode_cuda_context* context,
                                     cudaError_t error,
                                     const char* operation)
{
    if (error == cudaSuccess) {
        return VHSDECODE_CUDA_SUCCESS;
    }

    vhsdecode_cuda_status status = VHSDECODE_CUDA_CUDA_ERROR;
    switch (error) {
    case cudaErrorMemoryAllocation:
        status = VHSDECODE_CUDA_ALLOCATION_FAILED;
        break;
    case cudaErrorNoDevice:
    case cudaErrorInsufficientDriver:
    case cudaErrorInitializationError:
        status = VHSDECODE_CUDA_UNAVAILABLE;
        break;
    case cudaErrorInvalidDevice:
        status = VHSDECODE_CUDA_DEVICE_NOT_FOUND;
        break;
    default:
        break;
    }

    return fail(context, status,
                std::string(operation) + ": " + cuda_error_name(error) +
                " (" + cudaGetErrorString(error) + ")");
}

const char* cufft_error_name(cufftResult error)
{
    switch (error) {
    case CUFFT_SUCCESS: return "CUFFT_SUCCESS";
    case CUFFT_INVALID_PLAN: return "CUFFT_INVALID_PLAN";
    case CUFFT_ALLOC_FAILED: return "CUFFT_ALLOC_FAILED";
    case CUFFT_INVALID_TYPE: return "CUFFT_INVALID_TYPE";
    case CUFFT_INVALID_VALUE: return "CUFFT_INVALID_VALUE";
    case CUFFT_INTERNAL_ERROR: return "CUFFT_INTERNAL_ERROR";
    case CUFFT_EXEC_FAILED: return "CUFFT_EXEC_FAILED";
    case CUFFT_SETUP_FAILED: return "CUFFT_SETUP_FAILED";
    case CUFFT_INVALID_SIZE: return "CUFFT_INVALID_SIZE";
    case CUFFT_UNALIGNED_DATA: return "CUFFT_UNALIGNED_DATA";
#if defined(CUFFT_INCOMPLETE_PARAMETER_LIST)
    case CUFFT_INCOMPLETE_PARAMETER_LIST: return "CUFFT_INCOMPLETE_PARAMETER_LIST";
#endif
#if defined(CUFFT_INVALID_DEVICE)
    case CUFFT_INVALID_DEVICE: return "CUFFT_INVALID_DEVICE";
#endif
#if defined(CUFFT_PARSE_ERROR)
    case CUFFT_PARSE_ERROR: return "CUFFT_PARSE_ERROR";
#endif
#if defined(CUFFT_NO_WORKSPACE)
    case CUFFT_NO_WORKSPACE: return "CUFFT_NO_WORKSPACE";
#endif
#if defined(CUFFT_NOT_IMPLEMENTED)
    case CUFFT_NOT_IMPLEMENTED: return "CUFFT_NOT_IMPLEMENTED";
#endif
#if defined(CUFFT_NOT_SUPPORTED)
    case CUFFT_NOT_SUPPORTED: return "CUFFT_NOT_SUPPORTED";
#endif
    default: return "unknown cuFFT error";
    }
}

vhsdecode_cuda_status map_cufft_error(vhsdecode_cuda_context* context,
                                      cufftResult error,
                                      const char* operation)
{
    if (error == CUFFT_SUCCESS) {
        return VHSDECODE_CUDA_SUCCESS;
    }

    vhsdecode_cuda_status status = VHSDECODE_CUDA_CUFFT_ERROR;
    if (error == CUFFT_ALLOC_FAILED) {
        status = VHSDECODE_CUDA_ALLOCATION_FAILED;
    }
#if defined(CUFFT_NOT_SUPPORTED)
    else if (error == CUFFT_NOT_SUPPORTED) {
        status = VHSDECODE_CUDA_NOT_SUPPORTED;
    }
#endif
    return fail(context, status,
                std::string(operation) + ": " + cufft_error_name(error));
}

vhsdecode_cuda_status select_device(vhsdecode_cuda_context* context)
{
    if (context == nullptr) {
        return fail(nullptr, VHSDECODE_CUDA_INVALID_ARGUMENT, "CUDA context is null");
    }
    return map_cuda_error(context, cudaSetDevice(context->device_ordinal), "cudaSetDevice");
}

bool valid_stream(std::uint32_t stream_index)
{
    return stream_index < kStreamCount;
}

bool multiply_size(std::uint64_t first,
                   std::uint64_t second,
                   std::uint64_t* product)
{
    if (product == nullptr ||
        (first != 0 && second > std::numeric_limits<std::uint64_t>::max() / first)) {
        return false;
    }
    *product = first * second;
    return true;
}

bool multiply_to_size(std::uint64_t first,
                      std::uint64_t second,
                      std::size_t* product)
{
    std::uint64_t wide_product{};
    if (product == nullptr || !multiply_size(first, second, &wide_product) ||
        wide_product > std::numeric_limits<std::size_t>::max()) {
        return false;
    }
    *product = static_cast<std::size_t>(wide_product);
    return true;
}

bool add_size(std::size_t first,
              std::size_t second,
              std::size_t* sum)
{
    if (sum == nullptr ||
        second > std::numeric_limits<std::size_t>::max() - first) {
        return false;
    }
    *sum = first + second;
    return true;
}

bool calculate_rf_workspace_sizes(std::uint32_t sample_count,
                                  std::uint32_t batch_count,
                                  RfWorkspaceSizes* output)
{
    if (output == nullptr || sample_count == 0 || batch_count == 0) {
        return false;
    }

    RfWorkspaceSizes sizes{};
    sizes.bins_per_batch = (sample_count / 2u) + 1u;
    if (!multiply_size(sample_count, batch_count, &sizes.samples) ||
        !multiply_size(sizes.bins_per_batch, batch_count, &sizes.bins) ||
        !multiply_to_size(sizes.samples, sizeof(double), &sizes.sample_bytes) ||
        !multiply_to_size(sizes.bins, sizeof(cufftDoubleComplex),
                          &sizes.spectrum_bytes) ||
        !multiply_to_size(sizes.bins_per_batch, sizeof(cufftDoubleComplex),
                          &sizes.filter_bytes) ||
        !multiply_to_size(batch_count, sizeof(cufftDoubleComplex),
                          &sizes.batch_complex_bytes) ||
        !multiply_to_size(sizes.samples, sizeof(double) * 8u,
                          &sizes.host_io_bytes)) {
        return false;
    }

    std::size_t filter_cache_bytes{};
    std::size_t state_cache_bytes{};
    if (!multiply_to_size(sizes.bins_per_batch,
                          sizeof(cufftDoubleComplex) * 5u,
                          &filter_cache_bytes) ||
        !multiply_to_size(batch_count,
                          sizeof(cufftDoubleComplex) * 2u,
                          &state_cache_bytes) ||
        !add_size(filter_cache_bytes, state_cache_bytes,
                  &sizes.host_complex_bytes)) {
        return false;
    }

    *output = sizes;
    return true;
}

bool buffer_range_valid(const vhsdecode_cuda_buffer* buffer,
                        std::uint64_t offset,
                        std::uint64_t byte_count)
{
    return buffer != nullptr &&
        offset <= buffer->byte_count &&
        byte_count <= buffer->byte_count - offset;
}

vhsdecode_cuda_status validate_buffer(vhsdecode_cuda_context* context,
                                      const vhsdecode_cuda_buffer* buffer,
                                      std::uint64_t required_bytes,
                                      const char* label)
{
    if (buffer == nullptr || buffer->owner != context || buffer->pointer == nullptr) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    std::string(label) + " is null or belongs to another context");
    }
    if (buffer->byte_count < required_bytes) {
        return fail(context, VHSDECODE_CUDA_BUFFER_TOO_SMALL,
                    std::string(label) + " is smaller than the requested operation");
    }
    return VHSDECODE_CUDA_SUCCESS;
}

vhsdecode_cuda_status get_plan(vhsdecode_cuda_context* context,
                               int type,
                               int sample_count,
                               int batch_count,
                               std::uint32_t stream_index,
                               cufftHandle* output)
{
    if (output == nullptr) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT, "cuFFT plan output is null");
    }

    const PlanKey key{type, sample_count, batch_count, stream_index};
    std::lock_guard lock(context->plan_mutex);
    const auto found = context->plans.find(key);
    if (found != context->plans.end()) {
        *output = found->second;
        return VHSDECODE_CUDA_SUCCESS;
    }

    int dimensions[1]{sample_count};
    const int complex_count = (sample_count / 2) + 1;
    int input_distance = sample_count;
    int output_distance = sample_count;
    if (type == CUFFT_D2Z) {
        output_distance = complex_count;
    } else if (type == CUFFT_Z2D) {
        input_distance = complex_count;
    }

    cufftHandle plan{};
    cufftResult result = cufftPlanMany(
        &plan,
        1,
        dimensions,
        nullptr,
        1,
        input_distance,
        nullptr,
        1,
        output_distance,
        static_cast<cufftType>(type),
        batch_count);
    if (result != CUFFT_SUCCESS) {
        return map_cufft_error(context, result, "cufftPlanMany");
    }

    result = cufftSetStream(plan, context->streams[stream_index]);
    if (result != CUFFT_SUCCESS) {
        cufftDestroy(plan);
        return map_cufft_error(context, result, "cufftSetStream");
    }

    context->plans.emplace(key, plan);
    *output = plan;
    return VHSDECODE_CUDA_SUCCESS;
}

std::uint32_t launch_block_count(std::uint64_t element_count)
{
    const std::uint64_t blocks =
        (element_count + kThreadsPerBlock - 1) / kThreadsPerBlock;
    return static_cast<std::uint32_t>(std::min<std::uint64_t>(blocks, 65535));
}

__global__ void scale_real_kernel(double* values,
                                  std::uint64_t element_count,
                                  double scale)
{
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        values[index] *= scale;
    }
}

__global__ void scale_complex_kernel(cufftDoubleComplex* values,
                                     std::uint64_t element_count,
                                     double scale)
{
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        values[index].x *= scale;
        values[index].y *= scale;
    }
}

__global__ void complex_multiply_kernel(cufftDoubleComplex* spectrum,
                                        const cufftDoubleComplex* filter,
                                        std::uint64_t bins_per_batch,
                                        std::uint32_t batch_count,
                                        std::uint64_t filter_batch_stride)
{
    const std::uint64_t element_count = bins_per_batch * batch_count;
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const std::uint64_t batch = index / bins_per_batch;
        const std::uint64_t bin = index - (batch * bins_per_batch);
        const std::uint64_t filter_index = filter_batch_stride == 0
            ? bin
            : (batch * filter_batch_stride) + bin;
        const cufftDoubleComplex value = spectrum[index];
        const cufftDoubleComplex coefficient = filter[filter_index];
        spectrum[index].x = (value.x * coefficient.x) - (value.y * coefficient.y);
        spectrum[index].y = (value.x * coefficient.y) + (value.y * coefficient.x);
    }
}

__global__ void ld_efm_half_filter_kernel(
    const cufftDoubleComplex* input,
    const cufftDoubleComplex* filter,
    cufftDoubleComplex* output,
    std::uint32_t sample_count,
    std::uint32_t batch_count)
{
    const std::uint64_t bins_per_batch = (sample_count / 2u) + 1u;
    const std::uint64_t element_count = bins_per_batch * batch_count;
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) +
        threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const std::uint64_t bin = index % bins_per_batch;
        const cufftDoubleComplex value = input[index];
        const cufftDoubleComplex coefficient = filter[bin];
        double real = (value.x * coefficient.x) - (value.y * coefficient.y);
        double imaginary = (value.x * coefficient.y) + (value.y * coefficient.x);
        if (bin == 0 || bin == sample_count / 2u) {
            // C2R requires real-only endpoints. This also matches taking the
            // real component of the one-sided full-complex CPU inverse.
            imaginary = 0.0;
        } else {
            // C2R synthesizes a conjugate negative half and therefore doubles
            // the real part of a one-sided complex inverse unless scaled here.
            real *= 0.5;
            imaginary *= 0.5;
        }
        output[index].x = real;
        output[index].y = imaginary;
    }
}

__global__ void ld_audio_slice_filter_kernel(
    const cufftDoubleComplex* input,
    const cufftDoubleComplex* filter,
    cufftDoubleComplex* output,
    std::uint32_t source_sample_count,
    std::uint32_t batch_count,
    std::uint32_t low_bin,
    std::uint32_t audio_bin_count)
{
    const std::uint64_t source_bins = (source_sample_count / 2u) + 1u;
    const std::uint64_t element_count =
        static_cast<std::uint64_t>(audio_bin_count) * batch_count;
    const std::uint32_t half = audio_bin_count / 2u;
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) +
        threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const std::uint64_t batch = index / audio_bin_count;
        const std::uint32_t bin = static_cast<std::uint32_t>(
            index - (batch * audio_bin_count));
        cufftDoubleComplex value{};
        if (bin < half) {
            value = input[(batch * source_bins) + low_bin + bin];
        } else {
            const std::uint32_t negative_index = bin - half;
            const std::uint32_t positive_bin = low_bin + half - negative_index;
            value = input[(batch * source_bins) + positive_bin];
            value.y = -value.y;
        }
        const cufftDoubleComplex coefficient = filter[bin];
        output[index].x = (value.x * coefficient.x) -
            (value.y * coefficient.y);
        output[index].y = (value.x * coefficient.y) +
            (value.y * coefficient.x);
    }
}

__global__ void hilbert_r2c_kernel(cufftDoubleComplex* spectrum,
                                   std::uint32_t sample_count,
                                   std::uint32_t batch_count)
{
    const std::uint64_t bins = (sample_count / 2u) + 1u;
    const std::uint64_t element_count = bins * batch_count;
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const std::uint64_t bin = index % bins;
        if (bin == 0 || ((sample_count & 1u) == 0 && bin == sample_count / 2u)) {
            spectrum[index].x = 0.0;
            spectrum[index].y = 0.0;
        } else {
            const double real = spectrum[index].x;
            spectrum[index].x = spectrum[index].y;
            spectrum[index].y = -real;
        }
    }
}

__global__ void envelope_kernel(const double* real_samples,
                                const double* imaginary_samples,
                                double* output,
                                std::uint64_t element_count)
{
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        output[index] = sqrt((real_samples[index] * real_samples[index]) +
                             (imaginary_samples[index] * imaginary_samples[index]));
    }
}

__global__ void affine_abs_kernel(double* samples,
                                  double* envelope,
                                  std::uint64_t element_count,
                                  double scale,
                                  double offset)
{
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const double mapped = (samples[index] * scale) + offset;
        samples[index] = mapped;
        envelope[index] = fabs(mapped);
    }
}

__device__ __forceinline__ float rust_add(float left, float right)
{
    return __fadd_rn(left, right);
}

__device__ __forceinline__ float rust_sub(float left, float right)
{
    return __fsub_rn(left, right);
}

__device__ __forceinline__ float rust_mul(float left, float right)
{
    return __fmul_rn(left, right);
}

__device__ __forceinline__ float rust_div(float left, float right)
{
    return __fdiv_rn(left, right);
}

__device__ float vhs_rust_atan2(float y, float x)
{
    constexpr float minimum_normal = 1.1754943508222875079687365372222e-38f;
    constexpr float pi = 3.1415927410125732421875f;
    x = rust_add(x, copysignf(minimum_normal, x));
    const bool swap = fabsf(x) < fabsf(y);
    const float input = swap ? rust_div(x, y) : rust_div(y, x);
    const float square = rust_mul(input, input);
    float polynomial = rust_add(0.05265332f, rust_mul(square, -0.01172120f));
    polynomial = rust_add(-0.11643287f, rust_mul(square, polynomial));
    polynomial = rust_add(0.19354346f, rust_mul(square, polynomial));
    polynomial = rust_add(-0.33262347f, rust_mul(square, polynomial));
    float result = rust_mul(
        input,
        rust_add(0.99997726f, rust_mul(square, polynomial)));
    const float half_pi = input >= 0.0f ? pi / 2.0f : -pi / 2.0f;
    result = swap ? rust_sub(half_pi, result) : result;
    if (x < 0.0f) {
        result = rust_add(result, y >= 0.0f ? pi : -pi);
    }
    return result;
}

__device__ double vhs_rust_difference(float current,
                                      float previous,
                                      float frequency_hz)
{
    constexpr float tau = 6.283185482025146484375f;
    float difference = rust_sub(current, previous);
    difference = rust_sub(
        difference,
        rust_mul(floorf(rust_div(difference, tau)), tau));
    return static_cast<double>(rust_div(rust_mul(difference, frequency_hz), tau));
}

__global__ void vhs_rust_demod_kernel(const double* real_samples,
                                      const double* imaginary_samples,
                                      double* output,
                                      std::uint32_t samples_per_batch,
                                      std::uint32_t batch_count,
                                      float frequency_hz)
{
    const std::uint64_t element_count =
        static_cast<std::uint64_t>(samples_per_batch) * batch_count;
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const std::uint32_t local = static_cast<std::uint32_t>(index % samples_per_batch);
        if (local == 0) {
            output[index] = 0.0;
            continue;
        }
        const float previous = vhs_rust_atan2(
            static_cast<float>(imaginary_samples[index - 1]),
            static_cast<float>(real_samples[index - 1]));
        const float current = vhs_rust_atan2(
            static_cast<float>(imaginary_samples[index]),
            static_cast<float>(real_samples[index]));
        output[index] = vhs_rust_difference(current, previous, frequency_hz);
    }
}

__global__ void conjugate_phase_kernel(
    const cufftDoubleComplex* analytic,
    double* output,
    std::uint32_t samples_per_batch,
    std::uint32_t batch_count,
    const cufftDoubleComplex* previous_per_batch,
    cufftDoubleComplex* last_per_batch,
    double phase_scale)
{
    const std::uint64_t element_count =
        static_cast<std::uint64_t>(samples_per_batch) * batch_count;
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const std::uint32_t batch = static_cast<std::uint32_t>(index / samples_per_batch);
        const std::uint32_t local = static_cast<std::uint32_t>(index % samples_per_batch);
        const cufftDoubleComplex current = analytic[index];
        if (local == 0 && previous_per_batch == nullptr) {
            output[index] = 0.0;
        } else {
            const cufftDoubleComplex previous = local == 0
                ? previous_per_batch[batch]
                : analytic[index - 1];
            const double real = (current.x * previous.x) + (current.y * previous.y);
            const double imaginary = (current.y * previous.x) - (current.x * previous.y);
            double phase = atan2(imaginary, real);
            if (phase < 0.0) {
                phase += 6.283185307179586476925286766559;
            }
            output[index] = phase * phase_scale;
        }
        if (last_per_batch != nullptr && local + 1u == samples_per_batch) {
            last_per_batch[batch] = current;
        }
    }
}

__global__ void conjugate_phase_split_kernel(
    const double* real_samples,
    const double* imaginary_samples,
    double* output,
    std::uint32_t samples_per_batch,
    std::uint32_t batch_count,
    const cufftDoubleComplex* previous_per_batch,
    cufftDoubleComplex* last_per_batch,
    double phase_scale)
{
    const std::uint64_t element_count =
        static_cast<std::uint64_t>(samples_per_batch) * batch_count;
    std::uint64_t index = blockIdx.x * static_cast<std::uint64_t>(blockDim.x) + threadIdx.x;
    const std::uint64_t stride = gridDim.x * static_cast<std::uint64_t>(blockDim.x);
    for (; index < element_count; index += stride) {
        const std::uint32_t batch = static_cast<std::uint32_t>(index / samples_per_batch);
        const std::uint32_t local = static_cast<std::uint32_t>(index % samples_per_batch);
        const double current_real = real_samples[index];
        const double current_imaginary = imaginary_samples[index];
        if (local == 0 && previous_per_batch == nullptr) {
            output[index] = 0.0;
        } else {
            double previous_real;
            double previous_imaginary;
            if (local == 0) {
                previous_real = previous_per_batch[batch].x;
                previous_imaginary = previous_per_batch[batch].y;
            } else {
                previous_real = real_samples[index - 1];
                previous_imaginary = imaginary_samples[index - 1];
            }
            const double real = (current_real * previous_real) +
                (current_imaginary * previous_imaginary);
            const double imaginary = (current_imaginary * previous_real) -
                (current_real * previous_imaginary);
            double phase = atan2(imaginary, real);
            if (phase < 0.0) {
                phase += 6.283185307179586476925286766559;
            }
            output[index] = phase * phase_scale;
        }
        if (last_per_batch != nullptr && local + 1u == samples_per_batch) {
            last_per_batch[batch].x = current_real;
            last_per_batch[batch].y = current_imaginary;
        }
    }
}

vhsdecode_cuda_status check_kernel(vhsdecode_cuda_context* context,
                                   const char* operation)
{
    return map_cuda_error(context, cudaPeekAtLastError(), operation);
}

vhsdecode_cuda_status launch_scale_real(vhsdecode_cuda_context* context,
                                        double* values,
                                        std::uint64_t count,
                                        double scale,
                                        std::uint32_t stream_index)
{
    scale_real_kernel<<<launch_block_count(count), kThreadsPerBlock, 0,
                        context->streams[stream_index]>>>(values, count, scale);
    return check_kernel(context, "scale_real_kernel");
}

vhsdecode_cuda_status launch_scale_complex(vhsdecode_cuda_context* context,
                                           cufftDoubleComplex* values,
                                           std::uint64_t count,
                                           double scale,
                                           std::uint32_t stream_index)
{
    scale_complex_kernel<<<launch_block_count(count), kThreadsPerBlock, 0,
                           context->streams[stream_index]>>>(values, count, scale);
    return check_kernel(context, "scale_complex_kernel");
}

vhsdecode_cuda_status execute_r2c(vhsdecode_cuda_context* context,
                                  double* input,
                                  cufftDoubleComplex* output,
                                  int sample_count,
                                  int batch_count,
                                  std::uint32_t stream_index)
{
    cufftHandle plan{};
    auto status = get_plan(context, CUFFT_D2Z, sample_count, batch_count,
                           stream_index, &plan);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }
    return map_cufft_error(context, cufftExecD2Z(plan, input, output), "cufftExecD2Z");
}

vhsdecode_cuda_status execute_c2r(vhsdecode_cuda_context* context,
                                  cufftDoubleComplex* input,
                                  double* output,
                                  int sample_count,
                                  int batch_count,
                                  bool normalize,
                                  std::uint32_t stream_index)
{
    cufftHandle plan{};
    auto status = get_plan(context, CUFFT_Z2D, sample_count, batch_count,
                           stream_index, &plan);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }
    status = map_cufft_error(context, cufftExecZ2D(plan, input, output), "cufftExecZ2D");
    if (status == VHSDECODE_CUDA_SUCCESS && normalize) {
        const std::uint64_t count =
            static_cast<std::uint64_t>(sample_count) * batch_count;
        status = launch_scale_real(context, output, count, 1.0 / sample_count, stream_index);
    }
    return status;
}

vhsdecode_cuda_status execute_c2c(vhsdecode_cuda_context* context,
                                  cufftDoubleComplex* input,
                                  cufftDoubleComplex* output,
                                  int sample_count,
                                  int batch_count,
                                  int direction,
                                  bool normalize,
                                  std::uint32_t stream_index)
{
    cufftHandle plan{};
    auto status = get_plan(context, CUFFT_Z2Z, sample_count, batch_count,
                           stream_index, &plan);
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }
    status = map_cufft_error(
        context,
        cufftExecZ2Z(plan, input, output, direction),
        "cufftExecZ2Z");
    if (status == VHSDECODE_CUDA_SUCCESS && normalize) {
        const std::uint64_t count =
            static_cast<std::uint64_t>(sample_count) * batch_count;
        status = launch_scale_complex(
            context, output, count, 1.0 / sample_count, stream_index);
    }
    return status;
}

void reset_ld_workspace(RfWorkspace& workspace)
{
    if (workspace.host_ld_efm != nullptr) cudaFreeHost(workspace.host_ld_efm);
    for (auto*& filter : workspace.host_ld_filters) {
        if (filter != nullptr) cudaFreeHost(filter);
        filter = nullptr;
    }
    for (auto*& output : workspace.host_ld_audio) {
        if (output != nullptr) cudaFreeHost(output);
        output = nullptr;
    }
    for (auto*& filter : workspace.ld_filters) {
        if (filter != nullptr) cudaFree(filter);
        filter = nullptr;
    }
    if (workspace.ld_efm != nullptr) cudaFree(workspace.ld_efm);
    workspace.host_ld_efm = nullptr;
    workspace.ld_efm = nullptr;
    workspace.ld_sample_capacity = 0;
    workspace.ld_bin_capacity = 0;
    workspace.ld_batch_capacity = 0;
    std::memset(workspace.ld_filter_counts, 0,
                sizeof(workspace.ld_filter_counts));
    std::memset(workspace.ld_filter_valid, 0,
                sizeof(workspace.ld_filter_valid));
}

void reset_workspace(RfWorkspace& workspace)
{
    reset_ld_workspace(workspace);
    if (workspace.host_io != nullptr) cudaFreeHost(workspace.host_io);
    if (workspace.host_complex != nullptr) cudaFreeHost(workspace.host_complex);
    if (workspace.input != nullptr) cudaFree(workspace.input);
    if (workspace.base_spectrum != nullptr) cudaFree(workspace.base_spectrum);
    if (workspace.work_spectrum != nullptr) cudaFree(workspace.work_spectrum);
    if (workspace.alternate_spectrum != nullptr) cudaFree(workspace.alternate_spectrum);
    for (auto*& filter : workspace.filters) {
        if (filter != nullptr) cudaFree(filter);
    }
    if (workspace.previous != nullptr) cudaFree(workspace.previous);
    if (workspace.last != nullptr) cudaFree(workspace.last);
    if (workspace.rf_high_pass != nullptr) cudaFree(workspace.rf_high_pass);
    if (workspace.analytic_real != nullptr) cudaFree(workspace.analytic_real);
    if (workspace.analytic_imag != nullptr) cudaFree(workspace.analytic_imag);
    if (workspace.envelope != nullptr) cudaFree(workspace.envelope);
    if (workspace.demod != nullptr) cudaFree(workspace.demod);
    if (workspace.video != nullptr) cudaFree(workspace.video);
    if (workspace.video_low_pass != nullptr) cudaFree(workspace.video_low_pass);
    workspace = {};
}

vhsdecode_cuda_status ensure_workspace(vhsdecode_cuda_context* context,
                                       std::uint32_t sample_count,
                                       std::uint32_t batch_count,
                                       std::uint32_t stream_index)
{
    RfWorkspace& workspace = context->workspaces[stream_index];
    RfWorkspaceSizes sizes{};
    if (!calculate_rf_workspace_sizes(sample_count, batch_count, &sizes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "RF workspace size overflow");
    }
    const std::uint64_t samples = sizes.samples;
    const std::uint64_t bins_per_batch = sizes.bins_per_batch;
    const std::uint64_t bins = sizes.bins;
    if (workspace.sample_capacity >= samples &&
        workspace.bin_capacity >= bins &&
        workspace.filter_bin_capacity >= bins_per_batch &&
        workspace.batch_capacity >= batch_count) {
        return VHSDECODE_CUDA_SUCCESS;
    }

    auto status = map_cuda_error(
        context,
        cudaStreamSynchronize(context->streams[stream_index]),
        "cudaStreamSynchronize before RF workspace resize");
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }
    reset_workspace(workspace);

    const std::size_t sample_bytes = sizes.sample_bytes;
    const std::size_t spectrum_bytes = sizes.spectrum_bytes;
    const std::size_t filter_bytes = sizes.filter_bytes;
    const std::size_t batch_complex_bytes = sizes.batch_complex_bytes;
    const std::size_t host_io_bytes = sizes.host_io_bytes;
    const std::size_t host_complex_bytes = sizes.host_complex_bytes;

#define VHS_CUDA_ALLOC(member, bytes) \
    do { \
        const cudaError_t allocation_error = cudaMalloc( \
            reinterpret_cast<void**>(&(member)), (bytes)); \
        if (allocation_error != cudaSuccess) { \
            const auto allocation_status = map_cuda_error( \
                context, allocation_error, "cudaMalloc RF workspace"); \
            reset_workspace(workspace); \
            return allocation_status; \
        } \
    } while (false)

    cudaError_t error = cudaHostAlloc(
        reinterpret_cast<void**>(&workspace.host_io), host_io_bytes, cudaHostAllocDefault);
    if (error != cudaSuccess) {
        status = map_cuda_error(context, error, "cudaHostAlloc RF host IO");
        reset_workspace(workspace);
        return status;
    }
    error = cudaHostAlloc(reinterpret_cast<void**>(&workspace.host_complex),
                          host_complex_bytes, cudaHostAllocDefault);
    if (error != cudaSuccess) {
        status = map_cuda_error(context, error, "cudaHostAlloc RF filters");
        reset_workspace(workspace);
        return status;
    }

    VHS_CUDA_ALLOC(workspace.input, sample_bytes);
    VHS_CUDA_ALLOC(workspace.base_spectrum, spectrum_bytes);
    VHS_CUDA_ALLOC(workspace.work_spectrum, spectrum_bytes);
    VHS_CUDA_ALLOC(workspace.alternate_spectrum, spectrum_bytes);
    for (auto*& filter : workspace.filters) {
        VHS_CUDA_ALLOC(filter, filter_bytes);
    }
    VHS_CUDA_ALLOC(workspace.previous, batch_complex_bytes);
    VHS_CUDA_ALLOC(workspace.last, batch_complex_bytes);
    VHS_CUDA_ALLOC(workspace.rf_high_pass, sample_bytes);
    VHS_CUDA_ALLOC(workspace.analytic_real, sample_bytes);
    VHS_CUDA_ALLOC(workspace.analytic_imag, sample_bytes);
    VHS_CUDA_ALLOC(workspace.envelope, sample_bytes);
    VHS_CUDA_ALLOC(workspace.demod, sample_bytes);
    VHS_CUDA_ALLOC(workspace.video, sample_bytes);
    VHS_CUDA_ALLOC(workspace.video_low_pass, sample_bytes);

#undef VHS_CUDA_ALLOC

    workspace.sample_capacity = samples;
    workspace.bin_capacity = bins;
    workspace.filter_bin_capacity = bins_per_batch;
    workspace.batch_capacity = batch_count;
    return VHSDECODE_CUDA_SUCCESS;
}

vhsdecode_cuda_status ensure_ld_workspace(vhsdecode_cuda_context* context,
                                          std::uint32_t sample_count,
                                          std::uint32_t batch_count,
                                          std::uint32_t stream_index)
{
    RfWorkspace& workspace = context->workspaces[stream_index];
    RfWorkspaceSizes sizes{};
    if (!calculate_rf_workspace_sizes(sample_count, batch_count, &sizes)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "LD workspace size overflow");
    }
    const std::uint64_t samples = sizes.samples;
    const std::uint64_t bins_per_batch = sizes.bins_per_batch;
    const std::uint64_t bins = sizes.bins;
    if (workspace.ld_sample_capacity >= samples &&
        workspace.ld_bin_capacity >= bins &&
        workspace.ld_batch_capacity >= batch_count &&
        workspace.filter_bin_capacity >= bins_per_batch) {
        return VHSDECODE_CUDA_SUCCESS;
    }

    auto status = map_cuda_error(
        context,
        cudaStreamSynchronize(context->streams[stream_index]),
        "cudaStreamSynchronize before LD workspace resize");
    if (status != VHSDECODE_CUDA_SUCCESS) {
        return status;
    }
    reset_ld_workspace(workspace);

    const std::size_t sample_bytes = sizes.sample_bytes;
    const std::size_t spectrum_bytes = sizes.spectrum_bytes;
    const std::size_t filter_bytes = sizes.filter_bytes;

    cudaError_t error = cudaHostAlloc(
        reinterpret_cast<void**>(&workspace.host_ld_efm),
        sample_bytes,
        cudaHostAllocDefault);
    if (error != cudaSuccess) {
        status = map_cuda_error(context, error, "cudaHostAlloc LD EFM output");
        reset_ld_workspace(workspace);
        return status;
    }
    for (auto*& filter : workspace.host_ld_filters) {
        error = cudaHostAlloc(reinterpret_cast<void**>(&filter),
                              filter_bytes, cudaHostAllocDefault);
        if (error != cudaSuccess) {
            status = map_cuda_error(context, error, "cudaHostAlloc LD filter cache");
            reset_ld_workspace(workspace);
            return status;
        }
    }
    for (auto*& output : workspace.host_ld_audio) {
        error = cudaHostAlloc(reinterpret_cast<void**>(&output),
                              spectrum_bytes, cudaHostAllocDefault);
        if (error != cudaSuccess) {
            status = map_cuda_error(context, error,
                                    "cudaHostAlloc LD analog-audio output");
            reset_ld_workspace(workspace);
            return status;
        }
    }
    for (auto*& filter : workspace.ld_filters) {
        error = cudaMalloc(reinterpret_cast<void**>(&filter), filter_bytes);
        if (error != cudaSuccess) {
            status = map_cuda_error(context, error, "cudaMalloc LD filter cache");
            reset_ld_workspace(workspace);
            return status;
        }
    }
    error = cudaMalloc(reinterpret_cast<void**>(&workspace.ld_efm), sample_bytes);
    if (error != cudaSuccess) {
        status = map_cuda_error(context, error, "cudaMalloc LD EFM output");
        reset_ld_workspace(workspace);
        return status;
    }

    workspace.ld_sample_capacity = samples;
    workspace.ld_bin_capacity = bins;
    workspace.ld_batch_capacity = batch_count;
    return VHSDECODE_CUDA_SUCCESS;
}

vhsdecode_cuda_status launch_frequency_multiply(
    vhsdecode_cuda_context* context,
    cufftDoubleComplex* spectrum,
    const cufftDoubleComplex* filter,
    std::uint64_t bins_per_batch,
    std::uint32_t batch_count,
    std::uint64_t filter_stride,
    std::uint32_t stream_index)
{
    std::uint64_t count{};
    if (!multiply_size(bins_per_batch, batch_count, &count)) {
        return fail(context, VHSDECODE_CUDA_INVALID_ARGUMENT,
                    "Frequency multiply launch size overflow");
    }
    complex_multiply_kernel<<<launch_block_count(count), kThreadsPerBlock, 0,
                              context->streams[stream_index]>>>(
        spectrum, filter, bins_per_batch, batch_count, filter_stride);
    return check_kernel(context, "complex_multiply_kernel");
}

} // namespace
