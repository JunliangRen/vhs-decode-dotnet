#include "pipeline/sync_pulses.h"

#include <climits>
#include <cuda_runtime.h>

namespace
{
constexpr int kDetectionThreads = 256;
constexpr int kSortCapacity = 1024;

static_assert(MAX_PULSES <= kSortCapacity,
              "The deterministic pulse sorter must cover every retained pulse.");

// Each thread examines one possible closing edge. A valid pulse has exactly one
// transition from <= threshold to > threshold, so the full field is scanned in
// parallel while preserving the original detector's inclusive length limits.
__global__ void find_pulse_edges(
    const float* demod_05,
    int* pulse_starts,
    int* pulse_lengths,
    int* pulse_count,
    const size_t* field_offsets,
    int samples_per_field,
    float threshold,
    int min_synclen,
    int max_synclen,
    int max_pulses,
    int num_fields)
{
    const int field = blockIdx.y;
    const int end = blockIdx.x * blockDim.x + threadIdx.x;
    if (field >= num_fields || end <= 0 || end >= samples_per_field)
    {
        return;
    }

    const size_t field_offset = field_offsets != nullptr
        ? field_offsets[field]
        : static_cast<size_t>(field) * static_cast<size_t>(samples_per_field);
    const float* signal = demod_05 + field_offset;

    // The sequential detector closes only on a strict > comparison. NaNs do
    // not start a pulse while outside one, and do not close an active pulse.
    if (!(signal[end] > threshold) || signal[end - 1] > threshold)
    {
        return;
    }

    int segment_start = end - 1;
    while (segment_start > 0 && !(signal[segment_start - 1] > threshold))
    {
        --segment_start;
    }

    int start = segment_start;
    while (start < end && !(signal[start] <= threshold))
    {
        ++start;
    }

    const int length = end - start;
    if (start == end ||
        start == 0 ||
        length < min_synclen ||
        length > max_synclen)
    {
        return;
    }

    const int output_index = atomicAdd(pulse_count + field, 1);
    if (output_index < max_pulses)
    {
        const size_t base = static_cast<size_t>(field) * static_cast<size_t>(max_pulses);
        pulse_starts[base + output_index] =
            static_cast<int>(field_offset + static_cast<size_t>(start));
        pulse_lengths[base + output_index] = length;
    }
}

// A pathological/noisy field can contain more candidates than the compacted
// output can hold. Re-running the original sequential scan in that rare case
// preserves its exact "first MAX_PULSES" overflow contract.
__device__ void find_pulses_sequential(
    const float* signal,
    int* starts,
    int* lengths,
    int* count_out,
    size_t field_offset,
    int samples_per_field,
    float threshold,
    int min_synclen,
    int max_synclen,
    int max_pulses)
{
    bool in_pulse = signal[0] <= threshold;
    int current_start = 0;
    int count = 0;

    for (int sample = 0; sample < samples_per_field; ++sample)
    {
        const float value = signal[sample];
        if (in_pulse)
        {
            if (value > threshold)
            {
                const int length = sample - current_start;
                if (length >= min_synclen &&
                    length <= max_synclen &&
                    current_start != 0 &&
                    count < max_pulses)
                {
                    starts[count] = static_cast<int>(
                        field_offset + static_cast<size_t>(current_start));
                    lengths[count] = length;
                    ++count;
                }

                in_pulse = false;
            }
        }
        else if (value <= threshold)
        {
            current_start = sample;
            in_pulse = true;
        }
    }

    *count_out = count;
}

// Atomic compaction deliberately does not define an order. Sort the retained
// candidates by their absolute start position so downstream geometry sees the
// same deterministic order as the former sequential scan.
__global__ void order_pulses(
    const float* demod_05,
    int* pulse_starts,
    int* pulse_lengths,
    int* pulse_count,
    const size_t* field_offsets,
    int samples_per_field,
    float threshold,
    int min_synclen,
    int max_synclen,
    int max_pulses,
    int num_fields)
{
    const int field = blockIdx.x;
    if (field >= num_fields)
    {
        return;
    }

    const int detected_count = pulse_count[field];
    const size_t field_offset = field_offsets != nullptr
        ? field_offsets[field]
        : static_cast<size_t>(field) * static_cast<size_t>(samples_per_field);
    const size_t base = static_cast<size_t>(field) * static_cast<size_t>(max_pulses);

    if (detected_count > max_pulses)
    {
        if (threadIdx.x == 0)
        {
            find_pulses_sequential(
                demod_05 + field_offset,
                pulse_starts + base,
                pulse_lengths + base,
                pulse_count + field,
                field_offset,
                samples_per_field,
                threshold,
                min_synclen,
                max_synclen,
                max_pulses);
        }

        return;
    }

    __shared__ int ordered_starts[kSortCapacity];
    __shared__ int ordered_lengths[kSortCapacity];

    const int index = threadIdx.x;
    if (index < detected_count)
    {
        ordered_starts[index] = pulse_starts[base + index];
        ordered_lengths[index] = pulse_lengths[base + index];
    }
    else
    {
        ordered_starts[index] = INT_MAX;
        ordered_lengths[index] = 0;
    }
    __syncthreads();

    for (int width = 2; width <= kSortCapacity; width <<= 1)
    {
        for (int stride = width >> 1; stride > 0; stride >>= 1)
        {
            const int other = index ^ stride;
            if (other > index)
            {
                const bool ascending = (index & width) == 0;
                const int own_start = ordered_starts[index];
                const int other_start = ordered_starts[other];
                const int own_length = ordered_lengths[index];
                const int other_length = ordered_lengths[other];
                if ((ascending && own_start > other_start) ||
                    (!ascending && own_start < other_start))
                {
                    ordered_starts[index] = other_start;
                    ordered_lengths[index] = other_length;
                    ordered_starts[other] = own_start;
                    ordered_lengths[other] = own_length;
                }
            }
            __syncthreads();
        }
    }

    if (index < detected_count)
    {
        pulse_starts[base + index] = ordered_starts[index];
        pulse_lengths[base + index] = ordered_lengths[index];
    }
}
} // namespace

void sync_pulses(
    const float* d_demod_05,
    int* d_pulse_starts,
    int* d_pulse_lengths,
    int* d_pulse_count,
    const size_t* d_field_offsets,
    int num_fields,
    size_t samples_per_field,
    const VideoFormat& fmt)
{
    if (num_fields <= 0 || samples_per_field == 0)
    {
        return;
    }

    const int sample_count = static_cast<int>(samples_per_field);
    const int min_synclen = static_cast<int>(fmt.eq_pulse_width * 0.125);
    const int max_synclen = static_cast<int>(fmt.samples_per_line * 5);

    cudaMemsetAsync(
        d_pulse_count,
        0,
        static_cast<size_t>(num_fields) * sizeof(int));

    const dim3 block(kDetectionThreads, 1, 1);
    const dim3 grid(
        static_cast<unsigned int>((sample_count + kDetectionThreads - 1) / kDetectionThreads),
        static_cast<unsigned int>(num_fields),
        1);
    find_pulse_edges<<<grid, block>>>(
        d_demod_05,
        d_pulse_starts,
        d_pulse_lengths,
        d_pulse_count,
        d_field_offsets,
        sample_count,
        fmt.pulse_threshold_hz,
        min_synclen,
        max_synclen,
        MAX_PULSES,
        num_fields);

    order_pulses<<<num_fields, kSortCapacity>>>(
        d_demod_05,
        d_pulse_starts,
        d_pulse_lengths,
        d_pulse_count,
        d_field_offsets,
        sample_count,
        fmt.pulse_threshold_hz,
        min_synclen,
        max_synclen,
        MAX_PULSES,
        num_fields);
}
