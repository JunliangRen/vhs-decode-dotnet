#include "vhsdecode_cuda_fast.h"

#include <cuda_runtime.h>
#include <cufft.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <new>

namespace {

constexpr int kLength = 32768;
constexpr int kCapacity = 22;
constexpr int kChannels = 3;
constexpr int kRolls[kChannels] = {0, 32, 40};
constexpr int kSamples = kLength * kCapacity;
constexpr int kThreads = 256;
constexpr int kGrid = kSamples / kThreads;
constexpr size_t kRawBytes = kSamples * sizeof(double);
constexpr size_t kSpectrumBytes = kSamples * sizeof(double2);
constexpr size_t kOutputBytes = kChannels * kSamples * sizeof(float);
constexpr size_t kFilterBytes = kChannels * kLength * sizeof(double2);

static_assert(kSamples % kThreads == 0 && sizeof(double2) == 2 * sizeof(double));

__global__ void clip_pack(const double* input, double2* spectrum, int active, unsigned int* invalid)
{
    const int index = static_cast<int>(blockIdx.x * blockDim.x + threadIdx.x);
    if (index >= active) {
        spectrum[index] = make_double2(0.0, 0.0);
        return;
    }

    double value = input[index];
    if (!isfinite(value)) {
        atomicOr(invalid, 1U);
        spectrum[index] = make_double2(0.0, 0.0);
        return;
    }
    if (value < 1500000.0) value = 1500000.0;
    if (value > 30000000.0) value = 30000000.0;
    spectrum[index] = make_double2(value, 0.0);
}

__global__ void multiply_numpy(
    const double2* spectrum, const double2* filter, double2* product,
    int active, unsigned int* invalid)
{
    const int index = static_cast<int>(blockIdx.x * blockDim.x + threadIdx.x);
    if (index >= active) {
        product[index] = make_double2(0.0, 0.0);
        return;
    }

    const double2 left = spectrum[index];
    const double2 right = filter[index & (kLength - 1)];
    const double real = __fma_rn(left.x, right.x, -__dmul_rn(left.y, right.y));
    const double imag = __fma_rn(left.x, right.y, __dmul_rn(left.y, right.x));
    if (!isfinite(real) || !isfinite(imag)) atomicOr(invalid, 1U);
    product[index] = make_double2(real, imag);
}

__global__ void project(
    const double2* spectrum, float* output, int active, int roll, unsigned int* invalid)
{
    const int index = static_cast<int>(blockIdx.x * blockDim.x + threadIdx.x);
    if (index >= active) return;

    const int source = (index & ~(kLength - 1)) | ((index + roll) & (kLength - 1));
    const double value = __dmul_rn(spectrum[source].x, 1.0 / kLength);
    const float quantized = __double2float_rn(value);
    if (!isfinite(quantized)) atomicOr(invalid, 1U);
    output[index] = quantized;
}

struct Error {
    char* buffer;
    size_t capacity;
    int32_t status = VHSDECODE_CUDA_FAST_STATUS_OK;

    Error(char* text, size_t size) : buffer(text), capacity(size)
    {
        if (buffer && capacity != 0) buffer[0] = '\0';
    }

    int32_t fail(int32_t code, const char* message)
    {
        status = code;
        if (buffer && capacity != 0) std::snprintf(buffer, capacity, "%s", message);
        return status;
    }

    bool check(cudaError_t value, const char* operation,
               int32_t code = VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR)
    {
        if (value == cudaSuccess) return true;
        status = code;
        if (buffer && capacity != 0) {
            std::snprintf(buffer, capacity, "%s failed (%d): %s.", operation,
                static_cast<int>(value), cudaGetErrorString(value));
        }
        return false;
    }

    bool check(cufftResult value, const char* operation)
    {
        if (value == CUFFT_SUCCESS) return true;
        status = VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR;
        if (buffer && capacity != 0) {
            std::snprintf(buffer, capacity, "%s failed (cuFFT status %d).", operation,
                static_cast<int>(value));
        }
        return false;
    }
};

struct Context {
    explicit Context(int device_id) : device(device_id) {}

    int device;
    cudaStream_t stream = nullptr;
    cufftHandle plan = 0;
    double* input = nullptr;
    float* output = nullptr;
    float* device_output = nullptr;
    double* raw = nullptr;
    double2* spectrum = nullptr;
    double2* branch = nullptr;
    double2* filters = nullptr;
    unsigned int* invalid = nullptr;
    unsigned int* host_invalid = nullptr;
    bool faulted = false;

    ~Context()
    {
        if (cudaSetDevice(device) == cudaSuccess) {
            if (stream) cudaStreamSynchronize(stream);
            if (plan) cufftDestroy(plan);
            if (stream) cudaStreamDestroy(stream);
            if (invalid) cudaFree(invalid);
            if (filters) cudaFree(filters);
            if (branch) cudaFree(branch);
            if (spectrum) cudaFree(spectrum);
            if (raw) cudaFree(raw);
            if (device_output) cudaFree(device_output);
        }
        if (host_invalid) cudaFreeHost(host_invalid);
        if (output) cudaFreeHost(output);
        if (input) cudaFreeHost(input);
    }

    bool initialize(const double* coefficients, Error& error)
    {
        int length = kLength;
        return error.check(cudaStreamCreateWithFlags(&stream, cudaStreamNonBlocking), "create stream")
            && error.check(cudaMallocHost(&input, kRawBytes), "allocate pinned input")
            && error.check(cudaMallocHost(&output, kOutputBytes), "allocate pinned output")
            && error.check(cudaMallocHost(&host_invalid, sizeof(*host_invalid)), "allocate pinned status")
            && error.check(cudaMalloc(&device_output, kOutputBytes), "allocate device output")
            && error.check(cudaMalloc(&raw, kRawBytes), "allocate device raw")
            && error.check(cudaMalloc(&spectrum, kSpectrumBytes), "allocate device spectrum")
            && error.check(cudaMalloc(&branch, kSpectrumBytes), "allocate device inverse branch")
            && error.check(cudaMalloc(&filters, kFilterBytes), "allocate device filters")
            && error.check(cudaMalloc(&invalid, sizeof(*invalid)), "allocate device status")
            && error.check(cufftPlanMany(&plan, 1, &length, nullptr, 1, kLength, nullptr, 1, kLength,
                CUFFT_Z2Z, kCapacity), "plan B22 Z2Z")
            && error.check(cufftSetStream(plan, stream), "bind B22 Z2Z stream")
            && error.check(cudaMemcpyAsync(filters, coefficients, kFilterBytes,
                cudaMemcpyHostToDevice, stream), "upload filters")
            && error.check(cudaStreamSynchronize(stream), "complete filter upload");
    }

    bool enqueue(int active, Error& error)
    {
        if (!error.check(cudaMemsetAsync(invalid, 0, sizeof(*invalid), stream), "clear numeric status")
            || !error.check(cudaMemcpyAsync(raw, input, active * sizeof(*raw),
                cudaMemcpyHostToDevice, stream), "upload raw FM")) {
            return false;
        }

        clip_pack<<<kGrid, kThreads, 0, stream>>>(raw, spectrum, active, invalid);
        if (!error.check(cudaGetLastError(), "clip and pack raw FM")
            || !error.check(cufftExecZ2Z(plan, spectrum, spectrum, CUFFT_FORWARD), "forward FFT")) {
            return false;
        }

        for (int channel = 0; channel < kChannels; ++channel) {
            multiply_numpy<<<kGrid, kThreads, 0, stream>>>(
                spectrum, filters + channel * kLength, branch, active, invalid);
            if (!error.check(cudaGetLastError(), "multiply NumPy-order filter")
                || !error.check(cufftExecZ2Z(plan, branch, branch, CUFFT_INVERSE), "inverse FFT")) {
                return false;
            }
            project<<<(active + kThreads - 1) / kThreads, kThreads, 0, stream>>>(
                branch, device_output + channel * active, active, kRolls[channel], invalid);
            if (!error.check(cudaGetLastError(), "extract and roll video")) return false;
        }

        return error.check(cudaMemcpyAsync(output, device_output, kChannels * active * sizeof(*output),
                   cudaMemcpyDeviceToHost, stream), "download video result")
            && error.check(cudaMemcpyAsync(host_invalid, invalid, sizeof(*invalid),
                   cudaMemcpyDeviceToHost, stream), "download numeric status");
    }

    int32_t process(int active, Error& error)
    {
        if (faulted) {
            return error.fail(VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR,
                "The context cannot be reused after a device failure.");
        }
        if (!error.check(cudaSetDevice(device), "select CUDA device", VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE)) {
            return error.status;
        }

        const bool enqueued = enqueue(active, error);
        // Finish all earlier enqueues even if a later enqueue failed.
        const cudaError_t completion = cudaStreamSynchronize(stream);
        if (!enqueued || !error.check(completion, "complete video stream")) {
            faulted = true;
            return error.status;
        }
        if (*host_invalid != 0) {
            return error.fail(VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR,
                "Nonfinite FM, product or projected video sample.");
        }
        return VHSDECODE_CUDA_FAST_STATUS_OK;
    }
};

}  // namespace

int32_t VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_ld_video_create_v1(
    int32_t device_id, const double* filters_interleaved, void** context,
    double** pinned_input, float** pinned_output, char* error, size_t error_capacity)
{
    Error status(error, error_capacity);
    if (context) *context = nullptr;
    if (pinned_input) *pinned_input = nullptr;
    if (pinned_output) *pinned_output = nullptr;
    if (!context || !pinned_input || !pinned_output || !filters_interleaved || device_id < 0) {
        return status.fail(VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT, "Invalid device or create pointer.");
    }
    if (!std::all_of(filters_interleaved, filters_interleaved + 2 * kChannels * kLength,
            [](double value) { return std::isfinite(value); })) {
        return status.fail(VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT, "The configured video filter is nonfinite.");
    }
    if (!status.check(cudaSetDevice(device_id), "select CUDA device", VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE)) {
        return status.status;
    }

    auto* created = new (std::nothrow) Context(device_id);
    if (!created) {
        return status.fail(VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR, "Could not allocate the video context.");
    }
    if (!created->initialize(filters_interleaved, status)) {
        delete created;
        return status.status;
    }
    *context = created;
    *pinned_input = created->input;
    *pinned_output = created->output;
    return VHSDECODE_CUDA_FAST_STATUS_OK;
}

int32_t VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_ld_video_process_v1(
    void* context, int32_t block_count, char* error, size_t error_capacity)
{
    Error status(error, error_capacity);
    if (!context || block_count < 1 || block_count > kCapacity) {
        return status.fail(VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT, "Invalid post-FM video batch.");
    }
    return static_cast<Context*>(context)->process(block_count * kLength, status);
}

void VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_ld_video_destroy_v1(void* context)
{
    delete static_cast<Context*>(context);
}
