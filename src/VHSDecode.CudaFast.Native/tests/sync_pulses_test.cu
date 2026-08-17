#include "pipeline/sync_pulses.h"

#include <cuda_runtime.h>

#include <cstdio>
#include <limits>
#include <vector>

namespace
{
bool check_cuda(cudaError_t status, const char* operation)
{
    if (status == cudaSuccess)
    {
        return true;
    }

    std::fprintf(
        stderr,
        "%s failed: %s\n",
        operation,
        cudaGetErrorString(status));
    return false;
}

void add_pulse(std::vector<float>& signal, size_t offset, int start, int length)
{
    for (int sample = start; sample < start + length; ++sample)
    {
        signal[offset + static_cast<size_t>(sample)] = -1.0F;
    }
}

bool verify_regular_and_boundary_pulses()
{
    constexpr int num_fields = 2;
    constexpr int samples_per_field = 128;
    const std::vector<size_t> offsets = {5, 155};
    std::vector<float> signal(offsets.back() + samples_per_field, 1.0F);

    add_pulse(signal, offsets[0], 0, 6);       // Starts at zero: ignored.
    add_pulse(signal, offsets[0], 12, 4);      // Minimum inclusive.
    add_pulse(signal, offsets[0], 30, 7);
    add_pulse(signal, offsets[0], 50, 20);     // Maximum inclusive.
    add_pulse(signal, offsets[0], 80, 21);     // Too long: ignored.
    add_pulse(signal, offsets[0], 110, 18);    // Open at end: ignored.

    add_pulse(signal, offsets[1], 9, 5);
    signal[offsets[1] + 10] = std::numeric_limits<float>::quiet_NaN();
    add_pulse(signal, offsets[1], 40, 3);      // Too short: ignored.
    signal[offsets[1] + 58] = std::numeric_limits<float>::quiet_NaN();
    signal[offsets[1] + 59] = std::numeric_limits<float>::quiet_NaN();
    add_pulse(signal, offsets[1], 60, 8);

    float* d_signal = nullptr;
    size_t* d_offsets = nullptr;
    int* d_starts = nullptr;
    int* d_lengths = nullptr;
    int* d_counts = nullptr;
    bool success =
        check_cuda(
            cudaMalloc(&d_signal, signal.size() * sizeof(float)),
            "cudaMalloc signal") &&
        check_cuda(
            cudaMalloc(&d_offsets, offsets.size() * sizeof(size_t)),
            "cudaMalloc offsets") &&
        check_cuda(
            cudaMalloc(&d_starts, num_fields * MAX_PULSES * sizeof(int)),
            "cudaMalloc starts") &&
        check_cuda(
            cudaMalloc(&d_lengths, num_fields * MAX_PULSES * sizeof(int)),
            "cudaMalloc lengths") &&
        check_cuda(
            cudaMalloc(&d_counts, num_fields * sizeof(int)),
            "cudaMalloc counts");

    if (success)
    {
        success =
            check_cuda(
                cudaMemcpy(
                    d_signal,
                    signal.data(),
                    signal.size() * sizeof(float),
                    cudaMemcpyHostToDevice),
                "copy signal") &&
            check_cuda(
                cudaMemcpy(
                    d_offsets,
                    offsets.data(),
                    offsets.size() * sizeof(size_t),
                    cudaMemcpyHostToDevice),
                "copy offsets");
    }

    VideoFormat format(VideoProfile::PAL_625_50_VHS, 40.0);
    format.pulse_threshold_hz = 0.0;
    format.eq_pulse_width = 32.0; // min_synclen = 4
    format.samples_per_line = 4;  // max_synclen = 20

    if (success)
    {
        sync_pulses(
            d_signal,
            d_starts,
            d_lengths,
            d_counts,
            d_offsets,
            num_fields,
            samples_per_field,
            format);
        success = check_cuda(cudaDeviceSynchronize(), "regular pulse kernels");
    }

    std::vector<int> counts(num_fields);
    std::vector<int> starts(num_fields * MAX_PULSES);
    std::vector<int> lengths(num_fields * MAX_PULSES);
    if (success)
    {
        success =
            check_cuda(
                cudaMemcpy(
                    counts.data(),
                    d_counts,
                    counts.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy counts") &&
            check_cuda(
                cudaMemcpy(
                    starts.data(),
                    d_starts,
                    starts.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy starts") &&
            check_cuda(
                cudaMemcpy(
                    lengths.data(),
                    d_lengths,
                    lengths.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy lengths");
    }

    cudaFree(d_counts);
    cudaFree(d_lengths);
    cudaFree(d_starts);
    cudaFree(d_offsets);
    cudaFree(d_signal);

    const int expected_starts[] = {
        static_cast<int>(offsets[0]) + 12,
        static_cast<int>(offsets[0]) + 30,
        static_cast<int>(offsets[0]) + 50,
        static_cast<int>(offsets[1]) + 9,
        static_cast<int>(offsets[1]) + 60};
    const int expected_lengths[] = {4, 7, 20, 5, 8};

    success = success && counts[0] == 3 && counts[1] == 2;
    for (int index = 0; success && index < 3; ++index)
    {
        success =
            starts[index] == expected_starts[index] &&
            lengths[index] == expected_lengths[index];
    }
    for (int index = 0; success && index < 2; ++index)
    {
        const int output = MAX_PULSES + index;
        success =
            starts[output] == expected_starts[index + 3] &&
            lengths[output] == expected_lengths[index + 3];
    }

    if (!success)
    {
        std::fprintf(stderr, "Regular/edge pulse contract mismatch.\n");
    }
    return success;
}

bool verify_overflow_fallback()
{
    constexpr int pulse_total = MAX_PULSES + 100;
    constexpr int samples_per_field = pulse_total * 2 + 2;
    std::vector<float> signal(samples_per_field, 1.0F);
    for (int pulse = 0; pulse < pulse_total; ++pulse)
    {
        signal[1 + pulse * 2] = -1.0F;
    }

    float* d_signal = nullptr;
    int* d_starts = nullptr;
    int* d_lengths = nullptr;
    int* d_count = nullptr;
    bool success =
        check_cuda(
            cudaMalloc(&d_signal, signal.size() * sizeof(float)),
            "cudaMalloc overflow signal") &&
        check_cuda(
            cudaMalloc(&d_starts, MAX_PULSES * sizeof(int)),
            "cudaMalloc overflow starts") &&
        check_cuda(
            cudaMalloc(&d_lengths, MAX_PULSES * sizeof(int)),
            "cudaMalloc overflow lengths") &&
        check_cuda(
            cudaMalloc(&d_count, sizeof(int)),
            "cudaMalloc overflow count");

    if (success)
    {
        success = check_cuda(
            cudaMemcpy(
                d_signal,
                signal.data(),
                signal.size() * sizeof(float),
                cudaMemcpyHostToDevice),
            "copy overflow signal");
    }

    VideoFormat format(VideoProfile::NTSC_525_60_VHS, 40.0);
    format.pulse_threshold_hz = 0.0;
    format.eq_pulse_width = 8.0; // min_synclen = 1
    format.samples_per_line = 1; // max_synclen = 5

    if (success)
    {
        sync_pulses(
            d_signal,
            d_starts,
            d_lengths,
            d_count,
            nullptr,
            1,
            samples_per_field,
            format);
        success = check_cuda(cudaDeviceSynchronize(), "overflow pulse kernels");
    }

    int count = 0;
    std::vector<int> starts(MAX_PULSES);
    std::vector<int> lengths(MAX_PULSES);
    if (success)
    {
        success =
            check_cuda(
                cudaMemcpy(&count, d_count, sizeof(int), cudaMemcpyDeviceToHost),
                "copy overflow count") &&
            check_cuda(
                cudaMemcpy(
                    starts.data(),
                    d_starts,
                    starts.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy overflow starts") &&
            check_cuda(
                cudaMemcpy(
                    lengths.data(),
                    d_lengths,
                    lengths.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy overflow lengths");
    }

    cudaFree(d_count);
    cudaFree(d_lengths);
    cudaFree(d_starts);
    cudaFree(d_signal);

    success = success && count == MAX_PULSES;
    for (int pulse = 0; success && pulse < MAX_PULSES; ++pulse)
    {
        success = starts[pulse] == 1 + pulse * 2 && lengths[pulse] == 1;
    }

    if (!success)
    {
        std::fprintf(stderr, "Overflow fallback did not retain the first pulses.\n");
    }
    return success;
}
} // namespace

int main()
{
    int device_count = 0;
    const cudaError_t probe = cudaGetDeviceCount(&device_count);
    if (probe != cudaSuccess || device_count == 0)
    {
        std::printf("CUDA pulse tests skipped; no CUDA device is available.\n");
        return 0;
    }

    if (!verify_regular_and_boundary_pulses() || !verify_overflow_fallback())
    {
        return 1;
    }

    std::printf("CUDA parallel sync-pulse tests passed.\n");
    return 0;
}
