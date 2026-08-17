#include "pipeline/dropout_detect.h"

#include <cuda_runtime.h>

#include <algorithm>
#include <cstdio>
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

void set_range(
    std::vector<float>& envelope,
    int first,
    int last_inclusive,
    float value)
{
    for (int sample = first; sample <= last_inclusive; ++sample)
    {
        envelope[static_cast<size_t>(sample)] = value;
    }
}

bool verify_exact_pal_mapping_with_dynamic_offsets()
{
    constexpr int field_count = 2;
    constexpr int lines_per_frame = 625;
    constexpr size_t field_window_samples = 4000;
    constexpr size_t envelope_sample_count = 10000;
    const std::vector<size_t> field_offsets = {500, 5000};
    const std::vector<int> is_first_field = {1, 0};

    // Samples outside the two real field windows are intentionally extreme.
    // A detector that assumes field * padded_size instead of field_offsets
    // derives the wrong mean and reports almost the entire active field.
    std::vector<float> envelope(envelope_sample_count, 10000.0F);
    std::fill(envelope.begin() + 500, envelope.begin() + 4500, 100.0F);
    std::fill(envelope.begin() + 5000, envelope.begin() + 9000, 100.0F);

    // First field: one two-line dropout, then a low interval inside the
    // 30-sample merge window that must restart only at sample 578.
    set_range(envelope, 535, 544, 0.0F);
    envelope[545] = 20.0F;
    envelope[546] = 20.0F;
    envelope[547] = 100.0F;
    set_range(envelope, 560, 588, 0.0F);
    envelope[589] = 100.0F;

    // Second field: the same valid two-line shape plus a strict ten-sample
    // range, which Exact rejects because the minimum comparison is > 10.
    set_range(envelope, 5045, 5054, 0.0F);
    envelope[5055] = 20.0F;
    envelope[5056] = 20.0F;
    envelope[5057] = 100.0F;
    set_range(envelope, 5145, 5154, 0.0F);
    envelope[5155] = 100.0F;

    std::vector<float> linelocs(
        static_cast<size_t>(field_count) * lines_per_frame);
    for (int field = 0; field < field_count; ++field)
    {
        const float origin = static_cast<float>(field_offsets[field]);
        for (int line = 0; line < lines_per_frame; ++line)
        {
            linelocs[static_cast<size_t>(field) * lines_per_frame + line] =
                origin + static_cast<float>(line * 10);
        }
    }

    float* d_envelope = nullptr;
    float* d_linelocs = nullptr;
    size_t* d_field_offsets = nullptr;
    int* d_is_first_field = nullptr;
    int* d_lines = nullptr;
    int* d_starts = nullptr;
    int* d_ends = nullptr;
    int* d_counts = nullptr;
    const size_t output_entries =
        static_cast<size_t>(field_count) * MAX_DROPOUTS_PER_FIELD;

    bool success =
        check_cuda(
            cudaMalloc(&d_envelope, envelope.size() * sizeof(float)),
            "cudaMalloc envelope") &&
        check_cuda(
            cudaMalloc(&d_linelocs, linelocs.size() * sizeof(float)),
            "cudaMalloc line locations") &&
        check_cuda(
            cudaMalloc(
                &d_field_offsets,
                field_offsets.size() * sizeof(size_t)),
            "cudaMalloc field offsets") &&
        check_cuda(
            cudaMalloc(
                &d_is_first_field,
                is_first_field.size() * sizeof(int)),
            "cudaMalloc field parity") &&
        check_cuda(
            cudaMalloc(&d_lines, output_entries * sizeof(int)),
            "cudaMalloc dropout lines") &&
        check_cuda(
            cudaMalloc(&d_starts, output_entries * sizeof(int)),
            "cudaMalloc dropout starts") &&
        check_cuda(
            cudaMalloc(&d_ends, output_entries * sizeof(int)),
            "cudaMalloc dropout ends") &&
        check_cuda(
            cudaMalloc(&d_counts, field_count * sizeof(int)),
            "cudaMalloc dropout counts");

    if (success)
    {
        success =
            check_cuda(
                cudaMemcpy(
                    d_envelope,
                    envelope.data(),
                    envelope.size() * sizeof(float),
                    cudaMemcpyHostToDevice),
                "copy envelope") &&
            check_cuda(
                cudaMemcpy(
                    d_linelocs,
                    linelocs.data(),
                    linelocs.size() * sizeof(float),
                    cudaMemcpyHostToDevice),
                "copy line locations") &&
            check_cuda(
                cudaMemcpy(
                    d_field_offsets,
                    field_offsets.data(),
                    field_offsets.size() * sizeof(size_t),
                    cudaMemcpyHostToDevice),
                "copy field offsets") &&
            check_cuda(
                cudaMemcpy(
                    d_is_first_field,
                    is_first_field.data(),
                    is_first_field.size() * sizeof(int),
                    cudaMemcpyHostToDevice),
                "copy field parity");
    }

    VideoFormat format(VideoProfile::PAL_625_50_VHS, 40.0);
    format.output_line_len = 100;
    if (success)
    {
        success = dropout_detect(
            d_envelope,
            d_linelocs,
            d_field_offsets,
            d_is_first_field,
            d_lines,
            d_starts,
            d_ends,
            d_counts,
            field_count,
            field_window_samples,
            envelope_sample_count,
            format);
    }

    std::vector<int> counts(field_count);
    std::vector<int> lines(output_entries);
    std::vector<int> starts(output_entries);
    std::vector<int> ends(output_entries);
    if (success)
    {
        success =
            check_cuda(
                cudaMemcpy(
                    counts.data(),
                    d_counts,
                    counts.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy dropout counts") &&
            check_cuda(
                cudaMemcpy(
                    lines.data(),
                    d_lines,
                    lines.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy dropout lines") &&
            check_cuda(
                cudaMemcpy(
                    starts.data(),
                    d_starts,
                    starts.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy dropout starts") &&
            check_cuda(
                cudaMemcpy(
                    ends.data(),
                    d_ends,
                    ends.size() * sizeof(int),
                    cudaMemcpyDeviceToHost),
                "copy dropout ends");
    }

    cudaFree(d_counts);
    cudaFree(d_ends);
    cudaFree(d_starts);
    cudaFree(d_lines);
    cudaFree(d_is_first_field);
    cudaFree(d_field_offsets);
    cudaFree(d_linelocs);
    cudaFree(d_envelope);

    const int expected_counts[] = {4, 2};
    const int expected_lines[] = {1, 2, 5, 6, 1, 2};
    const int expected_starts[] = {50, 0, 80, 0, 50, 0};
    const int expected_ends[] = {100, 70, 100, 90, 100, 70};
    for (int field = 0; success && field < field_count; ++field)
    {
        success = counts[field] == expected_counts[field];
        for (int index = 0; success && index < counts[field]; ++index)
        {
            const int actual = field * MAX_DROPOUTS_PER_FIELD + index;
            const int expected = field == 0 ? index : 4 + index;
            success =
                lines[actual] == expected_lines[expected] &&
                starts[actual] == expected_starts[expected] &&
                ends[actual] == expected_ends[expected];
        }
    }

    if (!success)
    {
        std::fprintf(
            stderr,
            "Exact-style PAL dropout mapping contract mismatch.\n");
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
        std::printf("CUDA dropout tests skipped; no CUDA device is available.\n");
        return 0;
    }

    if (!verify_exact_pal_mapping_with_dynamic_offsets())
    {
        return 1;
    }

    std::printf("CUDA Exact-style dropout tests passed.\n");
    return 0;
}
