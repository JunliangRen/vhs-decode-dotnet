#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

struct RawReader;

bool cuda_fast_read_half_rate_s16(
    RawReader& reader,
    size_t logical_offset,
    size_t logical_sample_count,
    std::vector<int16_t>& h_source_s16,
    size_t* logical_samples_read);

bool cuda_fast_upload_half_rate_s16(
    const std::vector<int16_t>& h_source_s16,
    size_t logical_sample_count,
    int16_t* d_source_s16,
    float* d_destination);

// Reads native-rate RF into a reusable host buffer, uploads it once, and
// performs the preview-only 2:1 anti-alias FIR on the GPU.  The destination is
// the same FP32 signal plane consumed by FM demodulation at 20 MSPS.
bool cuda_fast_read_upload_half_rate(
    RawReader& reader,
    size_t logical_offset,
    size_t logical_sample_count,
    int16_t* d_source_s16,
    float* d_source_f32,
    float* d_destination,
    std::vector<int16_t>& h_source_s16,
    std::vector<float>& h_source_f32,
    size_t* logical_samples_read);
