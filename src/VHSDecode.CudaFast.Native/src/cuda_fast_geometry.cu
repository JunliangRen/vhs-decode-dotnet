#include "cuda_fast_geometry.h"

#include <cuda_runtime.h>

#include <cmath>
#include <cstdio>
#include <limits>

namespace {

__global__ void convert_s16_to_float(
    const int16_t* __restrict__ source,
    float* __restrict__ destination,
    size_t sample_count)
{
    const size_t index = static_cast<size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
    if (index < sample_count) {
        destination[index] = static_cast<float>(source[index]);
    }
}

__device__ void insertion_sort(float* values, int* weights, int count)
{
    for (int i = 1; i < count; ++i) {
        const float value = values[i];
        const int weight = weights != nullptr ? weights[i] : 0;
        int j = i - 1;
        while (j >= 0 && values[j] > value) {
            values[j + 1] = values[j];
            if (weights != nullptr) weights[j + 1] = weights[j];
            --j;
        }
        values[j + 1] = value;
        if (weights != nullptr) weights[j + 1] = weight;
    }
}

__device__ float unweighted_median(float* values, int count)
{
    insertion_sort(values, nullptr, count);
    const int mid = count / 2;
    return (count & 1) != 0
        ? values[mid]
        : 0.5f * (values[mid - 1] + values[mid]);
}

__device__ float weighted_value_at(
    const float* values,
    const int* weights,
    int count,
    int target)
{
    int cumulative = 0;
    for (int i = 0; i < count; ++i) {
        cumulative += weights[i];
        if (target < cumulative) return values[i];
    }
    return count > 0 ? values[count - 1] : 1.0f;
}

__device__ float weighted_median(
    float* values,
    int* weights,
    int count,
    int total_weight)
{
    insertion_sort(values, weights, count);
    const int mid = total_weight / 2;
    if ((total_weight & 1) != 0) {
        return weighted_value_at(values, weights, count, mid);
    }

    const float lower = weighted_value_at(values, weights, count, mid - 1);
    const float upper = weighted_value_at(values, weights, count, mid);
    return 0.5f * (lower + upper);
}

__global__ void compute_line_level_adjust(
    const float* __restrict__ linelocs,
    const int* __restrict__ is_first_field,
    float* __restrict__ output_a,
    float* __restrict__ output_b,
    int num_fields,
    int lines_per_frame,
    int output_field_lines,
    float samples_per_line,
    int ntsc_parity_offset)
{
    const int field = blockIdx.x;
    if (field >= num_fields) return;

    extern __shared__ float shared[];
    float* wow = shared;
    float* scratch = shared + output_field_lines + 1;
    __shared__ float median;
    __shared__ float threshold;

    int line_offset = 1;
    if (lines_per_frame == 625 && is_first_field != nullptr) {
        const bool first = is_first_field[field] == 1;
        line_offset = first ? 1 : 2;
    } else if (ntsc_parity_offset != 0 && lines_per_frame == 525) {
        const bool first = is_first_field != nullptr && is_first_field[field] == 1;
        line_offset = first ? 2 : 3;
    }

    const float* field_linelocs =
        linelocs + static_cast<size_t>(field) * lines_per_frame;
    for (int seg = threadIdx.x; seg <= output_field_lines; seg += blockDim.x) {
        const int base = seg + line_offset + 1;
        const int next = base + 1;
        float value = 1.0f;
        if (base >= 0 && next < lines_per_frame) {
            const float line_length = field_linelocs[next] - field_linelocs[base];
            if (isfinite(line_length) && line_length > 0.0f) {
                value = line_length / samples_per_line;
            }
        }
        wow[seg] = value;
    }
    __syncthreads();

    if (threadIdx.x == 0) {
        const int count = output_field_lines + 1;
        for (int i = 0; i < count; ++i) scratch[i] = wow[i];
        median = unweighted_median(scratch, count);
        for (int i = 0; i < count; ++i) scratch[i] = fabsf(wow[i] - median);
        const float mad = unweighted_median(scratch, count);
        threshold = mad > 0.0f ? 15.0f * mad : 0.001f;
    }
    __syncthreads();

    for (int line = threadIdx.x; line < output_field_lines; line += blockDim.x) {
        const float value = wow[line + 1];
        const float adjusted = fabsf(value - median) > threshold ? median : value;
        const size_t index = static_cast<size_t>(field) * output_field_lines + line;
        output_a[index] = adjusted;
        if (output_b != nullptr) output_b[index] = adjusted;
    }
}

__global__ void compute_chroma_source_geometry(
    const float* __restrict__ linelocs,
    const int* __restrict__ is_first_field,
    float* __restrict__ source_coords,
    float* __restrict__ source_level_adjust,
    int num_fields,
    int lines_per_frame,
    int output_field_lines,
    int output_line_len,
    float samples_per_line)
{
    const int field = blockIdx.x;
    if (field >= num_fields) return;

    // Candidate zero represents the explicit boundary fallback value 1.0.
    // Candidates 1..lines_per_frame represent line-spacing wow factors.
    extern __shared__ unsigned char shared_bytes[];
    float* candidates = reinterpret_cast<float*>(shared_bytes);
    int* weights = reinterpret_cast<int*>(candidates + lines_per_frame + 1);
    __shared__ float median;
    __shared__ float threshold;

    const float* field_linelocs =
        linelocs + static_cast<size_t>(field) * lines_per_frame;
    const int candidate_count = lines_per_frame + 1;
    for (int i = threadIdx.x; i < candidate_count; i += blockDim.x) {
        weights[i] = 0;
        if (i == 0 || i >= lines_per_frame) {
            candidates[i] = 1.0f;
        } else {
            candidates[i] =
                (field_linelocs[i] - field_linelocs[i - 1]) / samples_per_line;
        }
    }
    __syncthreads();

    const int output_first_line = lines_per_frame == 625
        ? (is_first_field != nullptr && is_first_field[field] == 0 ? 4 : 3)
        : 1;
    const int outline_offset = output_first_line * output_line_len;
    const int field_samples = output_field_lines * output_line_len;
    const int total_scaled = field_samples + outline_offset;
    const float output_scale = samples_per_line / static_cast<float>(output_line_len);
    const float maximum_x = samples_per_line * static_cast<float>(lines_per_frame - 1);

    // Count the exact FP32 segment selected by the original per-pixel formula.
    // Integer atomics make this deterministic even though the pixels are
    // distributed across the block.
    for (int i = threadIdx.x; i < total_scaled; i += blockDim.x) {
        const float scaled = static_cast<float>(i) * output_scale;
        int candidate = 0;
        if (scaled > 0.0f && scaled < maximum_x) {
            const float u = scaled / samples_per_line;
            int seg = static_cast<int>(floorf(u));
            if (seg < 0) seg = 0;
            if (seg > lines_per_frame - 2) seg = lines_per_frame - 2;
            candidate = seg + 1;
        }
        atomicAdd(weights + candidate, 1);
    }
    __syncthreads();

    if (threadIdx.x == 0) {
        median = weighted_median(candidates, weights, candidate_count, total_scaled);
        for (int i = 0; i < candidate_count; ++i) {
            candidates[i] = fabsf(candidates[i] - median);
        }
        const float mad = weighted_median(candidates, weights, candidate_count, total_scaled);
        threshold = mad > 0.0f ? 15.0f * mad : 0.001f;
    }
    __syncthreads();

    for (int sample = threadIdx.x; sample < field_samples; sample += blockDim.x) {
        const int i = sample + outline_offset;
        const float scaled = static_cast<float>(i) * output_scale;
        float coordinate;
        float wow = 1.0f;
        if (scaled <= 0.0f) {
            coordinate = field_linelocs[0];
        } else if (scaled >= maximum_x) {
            coordinate = field_linelocs[lines_per_frame - 1];
        } else {
            const float u = scaled / samples_per_line;
            int seg = static_cast<int>(floorf(u));
            if (seg < 0) seg = 0;
            if (seg > lines_per_frame - 2) seg = lines_per_frame - 2;
            const float fraction = u - static_cast<float>(seg);
            const float a = field_linelocs[seg];
            const float b = field_linelocs[seg + 1];
            coordinate = a + fraction * (b - a);
            wow = (b - a) / samples_per_line;
        }

        const size_t output_index =
            static_cast<size_t>(field) * field_samples + sample;
        source_coords[output_index] = coordinate;
        source_level_adjust[output_index] =
            fabsf(wow - median) > threshold ? median : wow;
    }
}

bool launch_line_level_adjust(
    const float* d_linelocs,
    const int* d_is_first_field,
    float* d_output_a,
    float* d_output_b,
    int num_fields,
    const VideoFormat& fmt,
    bool ntsc_parity_offset)
{
    if (num_fields <= 0) return true;
    const size_t shared_bytes =
        static_cast<size_t>(fmt.output_field_lines + 1) * 2 * sizeof(float);
    compute_line_level_adjust<<<num_fields, 256, shared_bytes>>>(
        d_linelocs,
        d_is_first_field,
        d_output_a,
        d_output_b,
        num_fields,
        fmt.lines_per_frame,
        fmt.output_field_lines,
        static_cast<float>(fmt.samples_per_line),
        ntsc_parity_offset ? 1 : 0);
    return cudaGetLastError() == cudaSuccess;
}

} // namespace

bool cuda_fast_convert_s16_to_float(
    const int16_t* d_source,
    float* d_destination,
    size_t sample_count)
{
    if (sample_count == 0) return true;
    if (d_source == nullptr || d_destination == nullptr) return false;

    constexpr int threads = 256;
    const size_t block_count = (sample_count + threads - 1) / threads;
    if (block_count > static_cast<size_t>(std::numeric_limits<unsigned int>::max())) {
        return false;
    }
    convert_s16_to_float<<<static_cast<unsigned int>(block_count), threads>>>(
        d_source,
        d_destination,
        sample_count);
    return cudaGetLastError() == cudaSuccess;
}

bool cuda_fast_compute_k5_level_adjust(
    const float* d_linelocs,
    const int* d_is_first_field,
    float* d_level_adjust,
    int num_fields,
    const VideoFormat& fmt)
{
    const bool ok = launch_line_level_adjust(
        d_linelocs,
        d_is_first_field,
        d_level_adjust,
        nullptr,
        num_fields,
        fmt,
        true);
    if (!ok) {
        std::fprintf(stderr, "CUDA-fast K5 geometry launch failed: %s\n",
                     cudaGetErrorString(cudaPeekAtLastError()));
    }
    return ok;
}

bool cuda_fast_compute_chroma_geometry(
    const float* d_linelocs,
    const int* d_is_first_field,
    float* d_level_adjust_a,
    float* d_level_adjust_b,
    float* d_source_coords,
    float* d_source_level_adjust,
    int num_fields,
    const VideoFormat& fmt)
{
        if (!launch_line_level_adjust(
            d_linelocs,
            d_is_first_field,
            d_level_adjust_a,
            d_level_adjust_b,
            num_fields,
            fmt,
            false)) {
        std::fprintf(stderr, "CUDA-fast chroma level geometry launch failed: %s\n",
                     cudaGetErrorString(cudaPeekAtLastError()));
        return false;
    }

    if (num_fields <= 0) return true;
    const size_t shared_bytes = static_cast<size_t>(fmt.lines_per_frame + 1)
        * (sizeof(float) + sizeof(int));
    compute_chroma_source_geometry<<<num_fields, 256, shared_bytes>>>(
        d_linelocs,
        d_is_first_field,
        d_source_coords,
        d_source_level_adjust,
        num_fields,
        fmt.lines_per_frame,
        fmt.output_field_lines,
        fmt.output_line_len,
        static_cast<float>(fmt.samples_per_line));
    const cudaError_t status = cudaGetLastError();
    if (status != cudaSuccess) {
        std::fprintf(stderr, "CUDA-fast chroma source geometry launch failed: %s\n",
                     cudaGetErrorString(status));
        return false;
    }
    return true;
}
