#include "cuda_fast_decimator.h"

#include "io/raw_reader.h"

#include <cuda_runtime.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>

namespace {

constexpr int kHalfWidth = 15;

__constant__ float kHalfRateCoefficients[8] = {
    0.5000046374907835f,
    0.3126333216205309f,
   -0.09010692207961689f,
    0.040107417651266825f,
   -0.017917030421899405f,
    0.007100857132041433f,
   -0.0022302855185240434f,
    0.00041032287080936366f
};

__global__ void decimate_s16_half_rate(
    const int16_t* __restrict__ source,
    float* __restrict__ destination,
    size_t output_count)
{
    const size_t output = static_cast<size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
    if (output >= output_count) return;

    const size_t center = static_cast<size_t>(kHalfWidth) + output * 2;
    float value = kHalfRateCoefficients[0] * static_cast<float>(source[center]);
    #pragma unroll
    for (int tap = 1; tap < 8; ++tap) {
        const int offset = (tap * 2) - 1;
        value += kHalfRateCoefficients[tap]
            * (static_cast<float>(source[center - static_cast<size_t>(offset)])
                + static_cast<float>(source[center + static_cast<size_t>(offset)]));
    }
    destination[output] = value;
}

__global__ void decimate_f32_half_rate(
    const float* __restrict__ source,
    float* __restrict__ destination,
    size_t output_count)
{
    const size_t output = static_cast<size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
    if (output >= output_count) return;

    const size_t center = static_cast<size_t>(kHalfWidth) + output * 2;
    float value = kHalfRateCoefficients[0] * source[center];
    #pragma unroll
    for (int tap = 1; tap < 8; ++tap) {
        const int offset = (tap * 2) - 1;
        value += kHalfRateCoefficients[tap]
            * (source[center - static_cast<size_t>(offset)]
                + source[center + static_cast<size_t>(offset)]);
    }
    destination[output] = value;
}

bool checked_source_window(
    size_t logical_offset,
    size_t logical_count,
    size_t source_total,
    size_t* source_start,
    size_t* prefix_zeros,
    size_t* source_buffer_count,
    size_t* readable_count)
{
    if (source_start == nullptr || prefix_zeros == nullptr
        || source_buffer_count == nullptr || readable_count == nullptr) {
        return false;
    }
    if (logical_offset > std::numeric_limits<size_t>::max() / 2
        || logical_count > (std::numeric_limits<size_t>::max() - 2 * kHalfWidth) / 2) {
        return false;
    }

    const size_t first_center = logical_offset * 2;
    *source_start = first_center >= static_cast<size_t>(kHalfWidth)
        ? first_center - static_cast<size_t>(kHalfWidth)
        : 0;
    *prefix_zeros = first_center >= static_cast<size_t>(kHalfWidth)
        ? 0
        : static_cast<size_t>(kHalfWidth) - first_center;
    *source_buffer_count = logical_count * 2 + 2 * static_cast<size_t>(kHalfWidth);
    const size_t capacity_after_prefix = *source_buffer_count - *prefix_zeros;
    *readable_count = *source_start < source_total
        ? std::min(capacity_after_prefix, source_total - *source_start)
        : 0;
    return true;
}

template <typename T>
void prepare_host_buffer(std::vector<T>& buffer, size_t count)
{
    buffer.resize(count);
    std::fill(buffer.begin(), buffer.end(), T{});
}

bool launch_decimator(
    const int16_t* d_source_s16,
    const float* d_source_f32,
    float* d_destination,
    size_t logical_count,
    bool int16_source)
{
    if (logical_count == 0) return true;
    constexpr unsigned int threads = 256;
    const size_t blocks = (logical_count + threads - 1) / threads;
    if (blocks > std::numeric_limits<unsigned int>::max()) return false;

    if (int16_source) {
        decimate_s16_half_rate<<<static_cast<unsigned int>(blocks), threads>>>(
            d_source_s16,
            d_destination,
            logical_count);
    } else {
        decimate_f32_half_rate<<<static_cast<unsigned int>(blocks), threads>>>(
            d_source_f32,
            d_destination,
            logical_count);
    }
    return cudaGetLastError() == cudaSuccess;
}

}  // namespace

bool cuda_fast_read_half_rate_s16(
    RawReader& reader,
    size_t logical_offset,
    size_t logical_sample_count,
    std::vector<int16_t>& h_source_s16,
    size_t* logical_samples_read)
{
    if (logical_samples_read == nullptr
        || reader.device_decimation_factor() != 2
        || !reader.callback_returns_int16()) {
        return false;
    }
    *logical_samples_read = 0;
    if (logical_sample_count == 0 || logical_offset >= reader.total_samples()) {
        h_source_s16.clear();
        return true;
    }

    const size_t logical_available = std::min(
        logical_sample_count,
        reader.total_samples() - logical_offset);
    size_t source_start = 0;
    size_t prefix_zeros = 0;
    size_t source_buffer_count = 0;
    size_t readable_count = 0;
    if (!checked_source_window(
            logical_offset,
            logical_sample_count,
            reader.source_total_samples(),
            &source_start,
            &prefix_zeros,
            &source_buffer_count,
            &readable_count)) {
        return false;
    }

    prepare_host_buffer(h_source_s16, source_buffer_count);
    const size_t read = readable_count == 0
        ? 0
        : reader.read_raw_at(
            h_source_s16.data() + prefix_zeros,
            source_start,
            readable_count);
    if (read != readable_count) return false;
    *logical_samples_read = logical_available;
    return true;
}

bool cuda_fast_upload_half_rate_s16(
    const std::vector<int16_t>& h_source_s16,
    size_t logical_sample_count,
    int16_t* d_source_s16,
    float* d_destination)
{
    if (d_source_s16 == nullptr || d_destination == nullptr
        || logical_sample_count > (std::numeric_limits<size_t>::max()
            - 2 * static_cast<size_t>(kHalfWidth)) / 2) {
        return false;
    }
    const size_t source_buffer_count = logical_sample_count * 2
        + 2 * static_cast<size_t>(kHalfWidth);
    if (h_source_s16.size() < source_buffer_count) return false;
    cudaError_t status = cudaMemcpy(
        d_source_s16,
        h_source_s16.data(),
        source_buffer_count * sizeof(int16_t),
        cudaMemcpyHostToDevice);
    if (status == cudaSuccess
        && !launch_decimator(
            d_source_s16,
            nullptr,
            d_destination,
            logical_sample_count,
            true)) {
        status = cudaPeekAtLastError();
        if (status == cudaSuccess) status = cudaErrorInvalidValue;
    }
    if (status != cudaSuccess) {
        std::fprintf(
            stderr,
            "CUDA-fast RF upload/20-MSPS decimation failed: %s\n",
            cudaGetErrorString(status));
        return false;
    }
    return true;
}

bool cuda_fast_read_upload_half_rate(
    RawReader& reader,
    size_t logical_offset,
    size_t logical_sample_count,
    int16_t* d_source_s16,
    float* d_source_f32,
    float* d_destination,
    std::vector<int16_t>& h_source_s16,
    std::vector<float>& h_source_f32,
    size_t* logical_samples_read)
{
    if (logical_samples_read == nullptr || d_destination == nullptr
        || reader.device_decimation_factor() != 2) {
        return false;
    }
    *logical_samples_read = 0;
    if (logical_sample_count == 0 || logical_offset >= reader.total_samples()) {
        return true;
    }

    const size_t logical_available = std::min(
        logical_sample_count,
        reader.total_samples() - logical_offset);
    size_t source_start = 0;
    size_t prefix_zeros = 0;
    size_t source_buffer_count = 0;
    size_t readable_count = 0;
    if (!checked_source_window(
            logical_offset,
            logical_sample_count,
            reader.source_total_samples(),
            &source_start,
            &prefix_zeros,
            &source_buffer_count,
            &readable_count)) {
        return false;
    }

    if (reader.callback_returns_int16()) {
        size_t int16_logical_samples_read = 0;
        if (!cuda_fast_read_half_rate_s16(
                reader,
                logical_offset,
                logical_sample_count,
                h_source_s16,
                &int16_logical_samples_read)
            || !cuda_fast_upload_half_rate_s16(
                h_source_s16,
                logical_sample_count,
                d_source_s16,
                d_destination)) {
            return false;
        }
        *logical_samples_read = int16_logical_samples_read;
        return true;
    } else {
        if (d_source_f32 == nullptr) return false;
        prepare_host_buffer(h_source_f32, source_buffer_count);
        const size_t read = readable_count == 0
            ? 0
            : reader.read_at(
                h_source_f32.data() + prefix_zeros,
                source_start,
                readable_count);
        if (read != readable_count) return false;
        if (read < readable_count) {
            std::fill(
                h_source_f32.begin() + static_cast<std::ptrdiff_t>(prefix_zeros + read),
                h_source_f32.begin() + static_cast<std::ptrdiff_t>(prefix_zeros + readable_count),
                0.0f);
        }
        cudaError_t upload_status = cudaMemcpy(
            d_source_f32,
            h_source_f32.data(),
            source_buffer_count * sizeof(float),
            cudaMemcpyHostToDevice);
        if (upload_status == cudaSuccess
            && !launch_decimator(
                nullptr,
                d_source_f32,
                d_destination,
                logical_sample_count,
                false)) {
            upload_status = cudaPeekAtLastError();
            if (upload_status == cudaSuccess) upload_status = cudaErrorInvalidValue;
        }
        if (upload_status != cudaSuccess) {
            std::fprintf(
                stderr,
                "CUDA-fast RF upload/20-MSPS decimation failed: %s\n",
                cudaGetErrorString(upload_status));
            return false;
        }
    }

    *logical_samples_read = logical_available;
    return true;
}
