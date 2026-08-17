#include "pipeline/dropout_detect.h"

#include <cuda_runtime.h>

#include <cmath>
#include <cstdio>
#include <limits>

namespace
{
constexpr float kThresholdFraction = 0.18f;
constexpr float kHysteresis = 1.25f;
constexpr int kMergeThreshold = 30;
constexpr int kMinimumLength = 10;
constexpr int kEventMaskSamples = 32;

__device__ void compute_scan_bounds(
    const float* field_linelocs,
    int parity,
    int lines_per_frame,
    int output_field_lines,
    size_t envelope_sample_count,
    int* scan_start,
    int* scan_end)
{
    const bool is_pal = lines_per_frame == 625;
    const int line_offset = is_pal ? (parity == 0 ? 3 : 2) : 0;
    const int start_line = line_offset + 1;
    const int current_field_lines = parity == 1
        ? (is_pal ? 312 : 263)
        : parity == 0
            ? (is_pal ? 313 : 262)
            : output_field_lines;
    int end_line = current_field_lines + start_line + 1;
    if (end_line >= lines_per_frame)
    {
        end_line = lines_per_frame - 1;
    }

    int start = static_cast<int>(floorf(field_linelocs[start_line]));
    int end = static_cast<int>(ceilf(field_linelocs[end_line]));
    if (start < 0)
    {
        start = 0;
    }
    if (end < start)
    {
        end = start;
    }
    if (static_cast<size_t>(end) > envelope_sample_count)
    {
        end = static_cast<int>(envelope_sample_count);
    }
    if (static_cast<size_t>(start) > envelope_sample_count)
    {
        start = static_cast<int>(envelope_sample_count);
    }

    *scan_start = start;
    *scan_end = end;
}

__global__ void compute_field_parameters(
    const float* __restrict__ envelope,
    const float* __restrict__ linelocs,
    const size_t* __restrict__ field_offsets,
    const int* __restrict__ is_first_field,
    float* __restrict__ means,
    int* __restrict__ scan_starts,
    int* __restrict__ scan_ends,
    int num_fields,
    size_t field_window_samples,
    size_t envelope_sample_count,
    int lines_per_frame,
    int output_field_lines)
{
    const int field = blockIdx.x;
    if (field >= num_fields)
    {
        return;
    }

    const size_t start = field_offsets[field];
    const size_t available = start < envelope_sample_count
        ? envelope_sample_count - start
        : 0;
    const size_t count = available < field_window_samples
        ? available
        : field_window_samples;

    float sum = 0.0f;
    for (size_t sample = static_cast<size_t>(threadIdx.x);
         sample < count;
         sample += static_cast<size_t>(blockDim.x))
    {
        sum += envelope[start + sample];
    }

    for (int offset = 16; offset > 0; offset >>= 1)
    {
        sum += __shfl_down_sync(0xffffffffU, sum, offset);
    }

    __shared__ float warp_sums[8];
    const int lane = threadIdx.x & 31;
    const int warp = threadIdx.x >> 5;
    if (lane == 0)
    {
        warp_sums[warp] = sum;
    }
    __syncthreads();

    if (threadIdx.x == 0)
    {
        float total = 0.0f;
        for (int index = 0; index < blockDim.x / 32; ++index)
        {
            total += warp_sums[index];
        }
        means[field] = count == 0 ? 0.0f : total / static_cast<float>(count);
        const float* field_linelocs =
            linelocs + static_cast<size_t>(field) * static_cast<size_t>(lines_per_frame);
        const int parity = is_first_field == nullptr ? -1 : is_first_field[field];
        compute_scan_bounds(
            field_linelocs,
            parity,
            lines_per_frame,
            output_field_lines,
            envelope_sample_count,
            &scan_starts[field],
            &scan_ends[field]);
    }
}

__global__ void classify_dropout_events(
    const float* __restrict__ envelope,
    const float* __restrict__ field_mean_envelope,
    const int* __restrict__ scan_starts,
    const int* __restrict__ scan_ends,
    unsigned int* __restrict__ down_masks,
    unsigned int* __restrict__ up_masks,
    int num_fields,
    int masks_per_field)
{
    constexpr int warps_per_block = 256 / 32;
    const int warp_in_block = threadIdx.x >> 5;
    const int lane = threadIdx.x & 31;
    const int mask_index = blockIdx.x * warps_per_block + warp_in_block;
    const int total_masks = num_fields * masks_per_field;
    if (mask_index >= total_masks)
    {
        return;
    }

    const int field = mask_index / masks_per_field;
    const int field_mask = mask_index - field * masks_per_field;
    const int scan_start = scan_starts[field];
    const int scan_end = scan_ends[field];
    const int sample = scan_start + field_mask * kEventMaskSamples + lane;
    const float down_threshold = field_mean_envelope[field] * kThresholdFraction;
    const float up_threshold = down_threshold * kHysteresis;
    const bool in_range = sample < scan_end;
    const float value = in_range ? envelope[sample] : 0.0f;
    const bool is_down = in_range && value <= down_threshold;
    // Exact evaluates the low-threshold branch first. Keep an equality case
    // out of the up mask when a zero threshold makes both comparisons true.
    const bool is_up = in_range && !is_down && value >= up_threshold;
    const unsigned int active = __activemask();
    const unsigned int down = __ballot_sync(active, is_down);
    const unsigned int up = __ballot_sync(active, is_up);
    if (lane == 0)
    {
        down_masks[mask_index] = down;
        up_masks[mask_index] = up;
    }
}

__device__ bool map_dropout_range(
    float dropout_start,
    float dropout_end,
    const float* field_linelocs,
    int output_line_length,
    int start_line,
    int end_line,
    int line_offset,
    int* line_index,
    int* output_lines,
    int* output_starts,
    int* output_ends,
    int* output_count)
{
    bool found_start = false;
    float line_start = field_linelocs[*line_index];
    float line_end = field_linelocs[*line_index + 1];

    while (*line_index < end_line)
    {
        const float line_length = line_end - line_start;
        if ((dropout_start >= line_start || *line_index == start_line)
            && dropout_start < line_end
            && line_length > 0.0f)
        {
            if (*output_count >= MAX_DROPOUTS_PER_FIELD)
            {
                return true;
            }

            int start_pixel = static_cast<int>(
                ((dropout_start - line_start) / line_length)
                * static_cast<float>(output_line_length));
            if (start_pixel < 0)
            {
                start_pixel = 0;
            }

            output_lines[*output_count] = *line_index - line_offset;
            output_starts[*output_count] = start_pixel;
            found_start = true;
            break;
        }

        ++*line_index;
        if (*line_index < end_line)
        {
            line_start = field_linelocs[*line_index];
            line_end = field_linelocs[*line_index + 1];
        }
    }

    if (!found_start)
    {
        return false;
    }

    while (*line_index < end_line && *output_count < MAX_DROPOUTS_PER_FIELD)
    {
        const float line_length = line_end - line_start;
        if (dropout_end < line_end && line_length > 0.0f)
        {
            int end_pixel = static_cast<int>(ceilf(
                ((dropout_end - line_start) / line_length)
                * static_cast<float>(output_line_length)));
            if (end_pixel > output_line_length)
            {
                end_pixel = output_line_length;
            }
            output_ends[*output_count] = end_pixel;
            ++*output_count;
            return true;
        }

        output_ends[*output_count] = output_line_length;
        ++*output_count;
        ++*line_index;
        if (*line_index < end_line && *output_count < MAX_DROPOUTS_PER_FIELD)
        {
            line_start = field_linelocs[*line_index];
            line_end = field_linelocs[*line_index + 1];
            output_lines[*output_count] = *line_index - line_offset;
            output_starts[*output_count] = 0;
        }
    }

    return true;
}

__global__ void detect_and_map_dropouts(
    const float* __restrict__ linelocs,
    const int* __restrict__ is_first_field,
    const int* __restrict__ scan_starts,
    const int* __restrict__ scan_ends,
    const unsigned int* __restrict__ down_masks,
    const unsigned int* __restrict__ up_masks,
    int* __restrict__ dropout_lines,
    int* __restrict__ dropout_starts,
    int* __restrict__ dropout_ends,
    int* __restrict__ dropout_counts,
    int* __restrict__ error_flag,
    int num_fields,
    int masks_per_field,
    int lines_per_frame,
    int output_field_lines,
    int output_line_length)
{
    const int field = blockIdx.x * blockDim.x + threadIdx.x;
    if (field >= num_fields)
    {
        return;
    }

    const float* field_linelocs =
        linelocs + static_cast<size_t>(field) * static_cast<size_t>(lines_per_frame);
    const int parity = is_first_field == nullptr ? -1 : is_first_field[field];
    const bool is_pal = lines_per_frame == 625;
    const int line_offset = is_pal ? (parity == 0 ? 3 : 2) : 0;
    const int start_line = line_offset + 1;
    const int current_field_lines = parity == 1
        ? (is_pal ? 312 : 263)
        : parity == 0
            ? (is_pal ? 313 : 262)
            : output_field_lines;
    int end_line = current_field_lines + start_line + 1;
    if (end_line >= lines_per_frame)
    {
        end_line = lines_per_frame - 1;
    }

    const int scan_start = scan_starts[field];
    const int scan_end = scan_ends[field];
    const int required_masks =
        (scan_end - scan_start + kEventMaskSamples - 1) / kEventMaskSamples;
    if (required_masks > masks_per_field)
    {
        atomicExch(error_flag, 1);
        dropout_counts[field] = 0;
        return;
    }

    int* field_lines =
        dropout_lines + static_cast<size_t>(field) * MAX_DROPOUTS_PER_FIELD;
    int* field_starts =
        dropout_starts + static_cast<size_t>(field) * MAX_DROPOUTS_PER_FIELD;
    int* field_ends =
        dropout_ends + static_cast<size_t>(field) * MAX_DROPOUTS_PER_FIELD;

    int output_count = 0;
    int line_index = start_line;
    int dropout_start = -1;
    int dropout_end = -1;
    bool mapping_active = scan_end > scan_start;

    // The masks were produced by a coalesced parallel pass. This serial pass
    // consumes only state transitions, preserving the exact sample positions,
    // hysteresis ordering, merge distance, and strict minimum-run comparison.
    const int mask_base = field * masks_per_field;
    for (int mask_index = 0; mask_index < required_masks; ++mask_index)
    {
        const int block_start = scan_start + mask_index * kEventMaskSamples;
        int block_end = block_start + kEventMaskSamples;
        if (block_end > scan_end)
        {
            block_end = scan_end;
        }
        int cursor = 0;
        const unsigned int down = down_masks[mask_base + mask_index];
        const unsigned int up = up_masks[mask_base + mask_index];
        while (block_start + cursor < block_end)
        {
            if (dropout_start != -1 && dropout_end == -1)
            {
                const unsigned int candidates = up & (~0U << cursor);
                if (candidates == 0)
                {
                    break;
                }
                const int local = __ffs(candidates) - 1;
                const int sample = block_start + local;
                dropout_end = sample;
                if (mapping_active && dropout_end - dropout_start > kMinimumLength)
                {
                    mapping_active = map_dropout_range(
                        static_cast<float>(dropout_start),
                        static_cast<float>(dropout_end),
                        field_linelocs,
                        output_line_length,
                        start_line,
                        end_line,
                        line_offset,
                        &line_index,
                        field_lines,
                        field_starts,
                        field_ends,
                        &output_count);
                }
                cursor = local + 1;
                continue;
            }

            int eligible_sample = block_start + cursor;
            if (dropout_end != -1)
            {
                const int after_merge = dropout_end + kMergeThreshold + 1;
                if (eligible_sample < after_merge)
                {
                    eligible_sample = after_merge;
                }
            }
            if (eligible_sample >= block_end)
            {
                break;
            }

            const int eligible_local = eligible_sample - block_start;
            const unsigned int candidates = down & (~0U << eligible_local);
            if (candidates == 0)
            {
                break;
            }
            const int local = __ffs(candidates) - 1;
            dropout_start = block_start + local;
            dropout_end = -1;
            cursor = local + 1;
        }
    }

    if (dropout_start != -1 && dropout_end == -1)
    {
        dropout_end = scan_end;
        if (mapping_active && dropout_end - dropout_start > kMinimumLength)
        {
            map_dropout_range(
                static_cast<float>(dropout_start),
                static_cast<float>(dropout_end),
                field_linelocs,
                output_line_length,
                start_line,
                end_line,
                line_offset,
                &line_index,
                field_lines,
                field_starts,
                field_ends,
                &output_count);
        }
    }

    dropout_counts[field] = output_count;
}
} // namespace

bool dropout_detect(
    const float* d_envelope,
    const float* d_linelocs,
    const size_t* d_field_offsets,
    const int* d_is_first_field,
    int* d_do_lines,
    int* d_do_starts,
    int* d_do_ends,
    int* d_do_count,
    int num_fields,
    size_t field_window_samples,
    size_t envelope_sample_count,
    const VideoFormat& fmt)
{
    if (num_fields <= 0)
    {
        return true;
    }

    float* d_field_means = nullptr;
    int* d_scan_starts = nullptr;
    int* d_scan_ends = nullptr;
    unsigned int* d_down_masks = nullptr;
    unsigned int* d_up_masks = nullptr;
    int* d_error_flag = nullptr;
    const size_t masks_per_field_size =
        (field_window_samples + kEventMaskSamples - 1) / kEventMaskSamples + 2;
    if (masks_per_field_size > static_cast<size_t>(std::numeric_limits<int>::max()))
    {
        std::fprintf(stderr, "CUDA-fast dropout event-mask workspace is too large.\n");
        return false;
    }
    const int masks_per_field = static_cast<int>(masks_per_field_size);
    const size_t total_masks =
        static_cast<size_t>(num_fields) * static_cast<size_t>(masks_per_field);
    cudaError_t status = cudaMalloc(
        &d_field_means,
        static_cast<size_t>(num_fields) * sizeof(float));
    if (status == cudaSuccess)
    {
        status = cudaMalloc(
            &d_scan_starts,
            static_cast<size_t>(num_fields) * sizeof(int));
    }
    if (status == cudaSuccess)
    {
        status = cudaMalloc(
            &d_scan_ends,
            static_cast<size_t>(num_fields) * sizeof(int));
    }
    if (status == cudaSuccess)
    {
        status = cudaMalloc(&d_down_masks, total_masks * sizeof(unsigned int));
    }
    if (status == cudaSuccess)
    {
        status = cudaMalloc(&d_up_masks, total_masks * sizeof(unsigned int));
    }
    if (status == cudaSuccess)
    {
        status = cudaMalloc(&d_error_flag, sizeof(int));
    }
    if (status != cudaSuccess)
    {
        std::fprintf(
            stderr,
            "CUDA-fast dropout allocation failed: %s\n",
            cudaGetErrorString(status));
        cudaFree(d_error_flag);
        cudaFree(d_up_masks);
        cudaFree(d_down_masks);
        cudaFree(d_scan_ends);
        cudaFree(d_scan_starts);
        cudaFree(d_field_means);
        return false;
    }

    status = cudaMemset(
        d_do_count,
        0,
        static_cast<size_t>(num_fields) * sizeof(int));
    if (status == cudaSuccess)
    {
        status = cudaMemset(d_error_flag, 0, sizeof(int));
    }
    if (status == cudaSuccess)
    {
        compute_field_parameters<<<num_fields, 256>>>(
            d_envelope,
            d_linelocs,
            d_field_offsets,
            d_is_first_field,
            d_field_means,
            d_scan_starts,
            d_scan_ends,
            num_fields,
            field_window_samples,
            envelope_sample_count,
            fmt.lines_per_frame,
            fmt.output_field_lines);
        status = cudaGetLastError();
    }

    if (status == cudaSuccess)
    {
        constexpr int threads = 256;
        constexpr int warps_per_block = threads / 32;
        const size_t blocks =
            (total_masks + warps_per_block - 1) / warps_per_block;
        classify_dropout_events<<<static_cast<unsigned int>(blocks), threads>>>(
            d_envelope,
            d_field_means,
            d_scan_starts,
            d_scan_ends,
            d_down_masks,
            d_up_masks,
            num_fields,
            masks_per_field);
        status = cudaGetLastError();
    }

    if (status == cudaSuccess)
    {
        constexpr int threads = 64;
        const int blocks = (num_fields + threads - 1) / threads;
        detect_and_map_dropouts<<<blocks, threads>>>(
            d_linelocs,
            d_is_first_field,
            d_scan_starts,
            d_scan_ends,
            d_down_masks,
            d_up_masks,
            d_do_lines,
            d_do_starts,
            d_do_ends,
            d_do_count,
            d_error_flag,
            num_fields,
            masks_per_field,
            fmt.lines_per_frame,
            fmt.output_field_lines,
            fmt.output_line_len);
        status = cudaGetLastError();
    }

    int host_error = 0;
    if (status == cudaSuccess)
    {
        status = cudaMemcpy(
            &host_error,
            d_error_flag,
            sizeof(int),
            cudaMemcpyDeviceToHost);
    }
    cudaFree(d_error_flag);
    cudaFree(d_up_masks);
    cudaFree(d_down_masks);
    cudaFree(d_scan_ends);
    cudaFree(d_scan_starts);
    cudaFree(d_field_means);

    if (status != cudaSuccess || host_error != 0)
    {
        std::fprintf(
            stderr,
            "CUDA-fast dropout detection failed: %s%s\n",
            cudaGetErrorString(status),
            host_error != 0 ? " (scan range exceeded the event-mask workspace)" : "");
        return false;
    }

    return true;
}
