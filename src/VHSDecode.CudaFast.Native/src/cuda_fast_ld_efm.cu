#include "vhsdecode_cuda_fast.h"

#include <cuda_runtime.h>
#include <cufft.h>

#include <cmath>
#include <cstdio>
#include <new>

namespace {

constexpr int kLength = VHSDECODE_CUDA_FAST_LD_EFM_BLOCK_LENGTH;
constexpr int kCapacity = VHSDECODE_CUDA_FAST_LD_EFM_BATCH_CAPACITY;
constexpr int kSamples = kLength * kCapacity;
constexpr int kThreads = 256;
constexpr int kGrid = kSamples / kThreads;

static_assert(kSamples % kThreads == 0 && sizeof(double2) == 2 * sizeof(double));

__global__ void pack_real(const double* raw, double2* spectrum, int active_samples)
{
    const int index = static_cast<int>(blockIdx.x * blockDim.x + threadIdx.x);
    spectrum[index] = make_double2(index < active_samples ? raw[index] : 0.0, 0.0);
}

__global__ void multiply_efm(
    const double2* spectrum, double2* product, const double2* filter, unsigned int* invalid)
{
    const int index = static_cast<int>(blockIdx.x * blockDim.x + threadIdx.x);
    const double2 value = spectrum[index];
    const double2 weight = filter[index & (kLength - 1)];
    const double arbr = __dmul_rn(value.x, weight.x);
    const double aibi = __dmul_rn(value.y, weight.y);
    const double aibr = __dmul_rn(value.y, weight.x);
    const double arbi = __dmul_rn(value.x, weight.y);
    const double real = __dsub_rn(arbr, aibi);
    const double imag = __dadd_rn(aibr, arbi);
    if (!isfinite(real) || !isfinite(imag)) atomicOr(invalid, 1U);
    product[index] = make_double2(real, imag);
}

__global__ void quantize_efm(const double2* inverse, int16_t* output, unsigned int* invalid)
{
    const int index = static_cast<int>(blockIdx.x * blockDim.x + threadIdx.x);
    double value = __dmul_rn(inverse[index].x, 1.0 / kLength);
    if (isnan(value)) {
        atomicOr(invalid, 1U);
        output[index] = 0;
        return;
    }
    if (value < -32768.0) value = -32768.0;
    if (value > 32767.0) value = 32767.0;
    output[index] = static_cast<int16_t>(__double2int_rz(value));
}

struct ErrorBuffer {
    char* text;
    size_t capacity;

    void clear() const
    {
        if (text && capacity != 0) text[0] = '\0';
    }

    void write(const char* message) const
    {
        if (text && capacity != 0) std::snprintf(text, capacity, "%s", message);
    }

    bool check(cudaError_t result, const char* operation) const
    {
        if (result == cudaSuccess) return true;
        if (text && capacity != 0) {
            std::snprintf(text, capacity, "%s failed (%d): %s.",
                operation, static_cast<int>(result), cudaGetErrorString(result));
        }
        return false;
    }

    bool check(cufftResult result, const char* operation) const
    {
        if (result == CUFFT_SUCCESS) return true;
        if (text && capacity != 0) {
            std::snprintf(text, capacity, "%s failed (cuFFT status %d).",
                operation, static_cast<int>(result));
        }
        return false;
    }
};

struct LdEfmContext {
    int device;
    cudaStream_t stream = nullptr;
    cufftHandle plan = 0;
    double* input = nullptr;
    int16_t* output = nullptr;
    unsigned int* host_invalid = nullptr;
    double* raw = nullptr;
    double2* spectrum = nullptr;
    double2* product = nullptr;
    double2* filter = nullptr;
    int16_t* shorts = nullptr;
    unsigned int* invalid = nullptr;
    bool faulted = false;

    explicit LdEfmContext(int device_id) : device(device_id) {}

    ~LdEfmContext()
    {
        if (cudaSetDevice(device) == cudaSuccess) {
            if (stream) cudaStreamSynchronize(stream);
            if (plan) cufftDestroy(plan);
            if (stream) cudaStreamDestroy(stream);
            if (invalid) cudaFree(invalid);
            if (shorts) cudaFree(shorts);
            if (filter) cudaFree(filter);
            if (product) cudaFree(product);
            if (spectrum) cudaFree(spectrum);
            if (raw) cudaFree(raw);
        }
        if (host_invalid) cudaFreeHost(host_invalid);
        if (output) cudaFreeHost(output);
        if (input) cudaFreeHost(input);
    }

    bool initialize(const double* filter_interleaved, ErrorBuffer error)
    {
        int length = kLength;
        return error.check(cudaStreamCreateWithFlags(&stream, cudaStreamNonBlocking), "cudaStreamCreateWithFlags")
            && error.check(cudaMallocHost(&input, kSamples * sizeof(*input)), "cudaMallocHost LD EFM input")
            && error.check(cudaMallocHost(&output, kSamples * sizeof(*output)), "cudaMallocHost LD EFM output")
            && error.check(cudaMallocHost(&host_invalid, sizeof(*host_invalid)), "cudaMallocHost LD EFM status")
            && error.check(cudaMalloc(&raw, kSamples * sizeof(*raw)), "cudaMalloc LD EFM input")
            && error.check(cudaMalloc(&spectrum, kSamples * sizeof(*spectrum)), "cudaMalloc LD EFM spectrum")
            && error.check(cudaMalloc(&product, kSamples * sizeof(*product)), "cudaMalloc LD EFM product")
            && error.check(cudaMalloc(&filter, kLength * sizeof(*filter)), "cudaMalloc LD EFM filter")
            && error.check(cudaMalloc(&shorts, kSamples * sizeof(*shorts)), "cudaMalloc LD EFM output")
            && error.check(cudaMalloc(&invalid, sizeof(*invalid)), "cudaMalloc LD EFM status")
            && error.check(cufftPlanMany(&plan, 1, &length, nullptr, 1, kLength, nullptr, 1, kLength,
                CUFFT_Z2Z, kCapacity), "cufftPlanMany LD EFM")
            && error.check(cufftSetStream(plan, stream), "cufftSetStream LD EFM")
            && error.check(cudaMemcpyAsync(filter, filter_interleaved, kLength * sizeof(*filter),
                cudaMemcpyHostToDevice, stream), "cudaMemcpyAsync LD EFM filter")
            && error.check(cudaStreamSynchronize(stream), "cudaStreamSynchronize LD EFM filter");
    }

    bool enqueue(int samples, ErrorBuffer error)
    {
        if (!error.check(cudaMemcpyAsync(raw, input, samples * sizeof(*raw), cudaMemcpyHostToDevice, stream),
                "cudaMemcpyAsync LD EFM input")
            || !error.check(cudaMemsetAsync(invalid, 0, sizeof(*invalid), stream), "cudaMemsetAsync LD EFM status")) {
            return false;
        }

        pack_real<<<kGrid, kThreads, 0, stream>>>(raw, spectrum, samples);
        if (!error.check(cudaGetLastError(), "LD EFM pack")
            || !error.check(cufftExecZ2Z(plan, spectrum, spectrum, CUFFT_FORWARD), "cufftExecZ2Z LD EFM forward")) {
            return false;
        }

        multiply_efm<<<kGrid, kThreads, 0, stream>>>(spectrum, product, filter, invalid);
        if (!error.check(cudaGetLastError(), "LD EFM filter multiply")
            || !error.check(cufftExecZ2Z(plan, product, product, CUFFT_INVERSE), "cufftExecZ2Z LD EFM inverse")) {
            return false;
        }

        quantize_efm<<<kGrid, kThreads, 0, stream>>>(product, shorts, invalid);
        return error.check(cudaGetLastError(), "LD EFM quantization")
            && error.check(cudaMemcpyAsync(output, shorts, samples * sizeof(*shorts), cudaMemcpyDeviceToHost, stream),
                "cudaMemcpyAsync LD EFM output")
            && error.check(cudaMemcpyAsync(host_invalid, invalid, sizeof(*invalid), cudaMemcpyDeviceToHost, stream),
                "cudaMemcpyAsync LD EFM status");
    }
};

}  // namespace

int32_t VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_ld_efm_create_v1(
    int32_t device_id, const double* filter_interleaved, void** context,
    double** pinned_input, int16_t** pinned_output, char* error, size_t error_capacity)
{
    const ErrorBuffer result{error, error_capacity};
    result.clear();
    if (context) *context = nullptr;
    if (pinned_input) *pinned_input = nullptr;
    if (pinned_output) *pinned_output = nullptr;
    if (!filter_interleaved || !context || !pinned_input || !pinned_output) {
        result.write("LD CUDA EFM requires a filter and all three context outputs.");
        return VHSDECODE_CUDA_FAST_STATUS_NULL_POINTER;
    }
    if (device_id < 0) {
        result.write("LD CUDA EFM requires a nonnegative device index.");
        return VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT;
    }
    if (!result.check(cudaSetDevice(device_id), "cudaSetDevice LD EFM")) {
        return VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE;
    }

    auto* state = new (std::nothrow) LdEfmContext(device_id);
    if (!state) {
        result.write("Could not allocate the LD CUDA EFM context.");
        return VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR;
    }
    if (!state->initialize(filter_interleaved, result)) {
        delete state;
        return VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE;
    }
    *context = state;
    *pinned_input = state->input;
    *pinned_output = state->output;
    return VHSDECODE_CUDA_FAST_STATUS_OK;
}

int32_t VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_ld_efm_process_v1(
    void* context, int32_t block_count, char* error, size_t error_capacity)
{
    const ErrorBuffer result{error, error_capacity};
    result.clear();
    if (!context) {
        result.write("LD CUDA EFM context was null.");
        return VHSDECODE_CUDA_FAST_STATUS_NULL_POINTER;
    }
    if (block_count < 1 || block_count > kCapacity) {
        result.write("LD CUDA EFM block count must be between 1 and 22.");
        return VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT;
    }
    auto& state = *static_cast<LdEfmContext*>(context);
    if (state.faulted) {
        result.write("LD CUDA EFM context cannot be reused after a device failure.");
        return VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR;
    }
    if (!result.check(cudaSetDevice(state.device), "cudaSetDevice LD EFM")) {
        state.faulted = true;
        return VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE;
    }

    const bool enqueued = state.enqueue(block_count * kLength, result);
    // Fence even after an enqueue failure before the pinned buffers can be reused or freed.
    const cudaError_t completion = cudaStreamSynchronize(state.stream);
    if (!enqueued || !result.check(completion, "cudaStreamSynchronize LD EFM")) {
        state.faulted = true;
        return VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR;
    }
    if (*state.host_invalid != 0) {
        result.write("LD CUDA EFM encountered a nonfinite filter product or a NaN inverse sample.");
        state.faulted = true;
        return VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR;
    }
    return VHSDECODE_CUDA_FAST_STATUS_OK;
}

void VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_ld_efm_destroy_v1(void* context)
{
    delete static_cast<LdEfmContext*>(context);
}
