#include "vhsdecode_cuda_fast.h"

#include <cuda_runtime.h>
#include <cufft.h>

#include <cstdio>
#include <memory>
#include <new>

namespace {

struct ErrorBuffer
{
    char* data;
    size_t capacity;

    int32_t fail(int32_t status, const char* operation, int code = 0) const
    {
        if (data && capacity) {
            std::snprintf(data, capacity, "%s (code %d)", operation, code);
        }
        return status;
    }

    bool check(cudaError_t result, const char* operation) const
    {
        if (result == cudaSuccess) return true;
        fail(VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR, operation, static_cast<int>(result));
        return false;
    }

    bool check(cufftResult result, const char* operation) const
    {
        if (result == CUFFT_SUCCESS) return true;
        fail(VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR, operation, static_cast<int>(result));
        return false;
    }
};

__global__ void normalize_inverse(double* real, int length, double scale)
{
    const int index = static_cast<int>(blockIdx.x * blockDim.x + threadIdx.x);
    if (index < length) real[index] *= scale;
}

struct RealFft
{
    const int device;
    const int length;
    cudaStream_t stream{};
    cufftHandle forward{};
    cufftHandle inverse{};
    double* host_real{};
    double* device_real{};
    cufftDoubleComplex* host_spectrum{};
    cufftDoubleComplex* device_spectrum{};
    bool faulted{};

    ~RealFft()
    {
        cudaSetDevice(device);
        if (inverse) cufftDestroy(inverse);
        if (forward) cufftDestroy(forward);
        if (device_spectrum) cudaFree(device_spectrum);
        if (device_real) cudaFree(device_real);
        if (host_spectrum) cudaFreeHost(host_spectrum);
        if (host_real) cudaFreeHost(host_real);
        if (stream) cudaStreamDestroy(stream);
    }

    size_t real_bytes() const { return static_cast<size_t>(length) * sizeof(double); }
    size_t spectrum_bytes() const { return static_cast<size_t>(length / 2 + 1) * sizeof(cufftDoubleComplex); }

    bool initialize(ErrorBuffer error)
    {
        return error.check(cudaStreamCreateWithFlags(&stream, cudaStreamNonBlocking), "create VHS FFT stream")
            && error.check(cudaMallocHost(&host_real, real_bytes()), "allocate pinned real VHS FFT buffer")
            && error.check(cudaMallocHost(&host_spectrum, spectrum_bytes()), "allocate pinned VHS FFT spectrum")
            && error.check(cudaMalloc(&device_real, real_bytes()), "allocate device real VHS FFT buffer")
            && error.check(cudaMalloc(&device_spectrum, spectrum_bytes()), "allocate device VHS FFT spectrum")
            && error.check(cufftPlan1d(&forward, length, CUFFT_D2Z, 1), "plan FP64 VHS forward FFT")
            && error.check(cufftPlan1d(&inverse, length, CUFFT_Z2D, 1), "plan FP64 VHS inverse FFT")
            && error.check(cufftSetStream(forward, stream), "bind VHS forward FFT stream")
            && error.check(cufftSetStream(inverse, stream), "bind VHS inverse FFT stream");
    }

    bool enqueue(bool backward, ErrorBuffer error)
    {
        if (backward) {
            if (!error.check(cudaMemcpyAsync(device_spectrum, host_spectrum, spectrum_bytes(),
                    cudaMemcpyHostToDevice, stream), "upload VHS FFT spectrum")
                || !error.check(cufftExecZ2D(inverse, device_spectrum, device_real), "execute FP64 VHS inverse FFT")) {
                return false;
            }

            constexpr int threads = 256;
            normalize_inverse<<<(length + threads - 1) / threads, threads, 0, stream>>>(
                device_real, length, 1.0 / static_cast<double>(length));
            return error.check(cudaGetLastError(), "normalize VHS inverse FFT")
                && error.check(cudaMemcpyAsync(host_real, device_real, real_bytes(), cudaMemcpyDeviceToHost, stream),
                    "download inverse VHS FFT output");
        }

        return error.check(cudaMemcpyAsync(device_real, host_real, real_bytes(), cudaMemcpyHostToDevice, stream),
                "upload VHS FFT input")
            && error.check(cufftExecD2Z(forward, device_real, device_spectrum), "execute FP64 VHS forward FFT")
            && error.check(cudaMemcpyAsync(host_spectrum, device_spectrum, spectrum_bytes(),
                cudaMemcpyDeviceToHost, stream), "download forward VHS FFT spectrum");
    }
};

} // namespace

int32_t VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_vhs_fft64_create_v1(
    int32_t device, int32_t length, void** context, double** real, double** interleaved_spectrum,
    char* message, size_t capacity)
{
    const ErrorBuffer error{message, capacity};
    if (context) *context = nullptr;
    if (real) *real = nullptr;
    if (interleaved_spectrum) *interleaved_spectrum = nullptr;
    if (!context || !real || !interleaved_spectrum || device < 0 || length < 16 || length > 65536
        || (length & (length - 1)) != 0) {
        return error.fail(VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT, "invalid VHS FFT shape");
    }

    if (!error.check(cudaSetDevice(device), "select VHS FFT CUDA device")) {
        return VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE;
    }

    std::unique_ptr<RealFft> fft(new (std::nothrow) RealFft{device, length});
    if (!fft) {
        return error.fail(VHSDECODE_CUDA_FAST_STATUS_INTERNAL_ERROR, "allocate VHS FFT context");
    }
    if (!fft->initialize(error)) {
        return VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE;
    }

    *real = fft->host_real;
    *interleaved_spectrum = reinterpret_cast<double*>(fft->host_spectrum);
    *context = fft.release();
    return VHSDECODE_CUDA_FAST_STATUS_OK;
}

int32_t VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_vhs_fft64_transform_v1(
    void* context, int32_t inverse, char* message, size_t capacity)
{
    const ErrorBuffer error{message, capacity};
    if (!context || (inverse != 0 && inverse != 1)) {
        return error.fail(VHSDECODE_CUDA_FAST_STATUS_INVALID_ARGUMENT, "invalid VHS FFT request");
    }

    auto& fft = *static_cast<RealFft*>(context);
    if (fft.faulted) {
        return error.fail(VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR, "VHS FFT context is faulted");
    }
    if (!error.check(cudaSetDevice(fft.device), "select VHS FFT CUDA device")) {
        fft.faulted = true;
        return VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE;
    }

    const bool enqueued = fft.enqueue(inverse == 1, error);
    // Drain even on enqueue failure before caller-owned publication or pinned buffer reuse.
    const cudaError_t completion = cudaStreamSynchronize(fft.stream);
    if (!enqueued || !error.check(completion, "complete VHS CUDA FFT")) {
        fft.faulted = true;
        return VHSDECODE_CUDA_FAST_STATUS_DECODE_ERROR;
    }
    return VHSDECODE_CUDA_FAST_STATUS_OK;
}

void VHSDECODE_CUDA_FAST_CALL vhsdecode_cuda_fast_vhs_fft64_destroy_v1(void* context)
{
    delete static_cast<RealFft*>(context);
}
