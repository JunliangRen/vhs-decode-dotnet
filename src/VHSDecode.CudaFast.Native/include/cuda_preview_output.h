#pragma once

#include "format/video_format.h"
#include "vhsdecode_cuda_fast.h"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

struct CudaPreviewOutputSettings {
    int device_id = 0;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t frame_rate_numerator = 0;
    uint32_t frame_rate_denominator = 1;
    uint32_t constant_qp = 31;
    uint32_t gop_length = 100;
    size_t target_sample = 0;
    uint32_t requested_frames = 0;
    vhsdecode_cuda_fast_bitstream_callback bitstream_callback = nullptr;
    vhsdecode_cuda_fast_cancel_callback cancel_callback = nullptr;
    void* user_data = nullptr;
};
class CudaPreviewOutput {
public:
    CudaPreviewOutput();
    ~CudaPreviewOutput();

    CudaPreviewOutput(const CudaPreviewOutput&) = delete;
    CudaPreviewOutput& operator=(const CudaPreviewOutput&) = delete;

    bool open(const VideoFormat& format, const CudaPreviewOutputSettings& settings);
    bool write_device_fields(
        const uint16_t* d_luma,
        const uint16_t* d_chroma,
        const int* d_dropout_lines,
        const int* d_dropout_starts,
        const int* d_dropout_ends,
        const int* d_dropout_counts,
        const int* host_is_first_field,
        const size_t* host_field_offsets,
        const int* host_field_phase_ids,
        size_t raw_offset,
        int field_count);
    bool finalize();
    void close();

    bool complete() const;
    uint32_t frames_encoded() const;
    uint32_t fields_scanned() const;
    uint64_t encoded_bytes() const;
    const std::string& error() const;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
