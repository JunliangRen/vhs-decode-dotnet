#pragma once

#include "cuda_preview_output.h"
#include "format/video_format.h"

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <memory>
#include <string>
#include <vector>

// Writes regular TBC outputs, or owns the preview-only GPU output sink.  The
// two modes are mutually exclusive so Exact/full cuda-fast file semantics are
// unchanged while preview can consume the device TBC planes before download.
struct TBCWriter {
    ~TBCWriter();

    bool open(const std::string& output_base, const VideoFormat& fmt, bool overwrite);
    bool open_preview(
        const VideoFormat& fmt,
        const CudaPreviewOutputSettings& settings);
    void close();

    bool write_luma_field(const uint16_t* data);
    bool write_chroma_field(const uint16_t* data);
    void add_dropout(int line, int start_x, int end_x);
    void set_first_field(bool is_first);
    void set_field_phase_id(int phase_id);
    void set_file_loc(size_t file_loc);
    void finish_field();
    bool write_json();
    bool finalize();

    bool accepts_device_fields() const { return preview_output != nullptr; }
    bool write_preview_device_fields(
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
        int fields);
    bool output_complete() const;
    uint32_t preview_frames_encoded() const;
    uint32_t preview_fields_scanned() const;
    uint64_t preview_encoded_bytes() const;
    const std::string& preview_error() const;

    int fields_written() const {
        return preview_output != nullptr
            ? static_cast<int>(preview_output->frames_encoded())
            : field_count;
    }

private:
    FILE* luma_fp = nullptr;
    FILE* chroma_fp = nullptr;
    std::string json_path;
    std::string luma_path;
    std::string chroma_path;
    VideoFormat fmt = VideoFormat(VideoProfile::NTSC_525_60_VHS, 28.0);
    std::unique_ptr<CudaPreviewOutput> preview_output;
    int field_count = 0;

    struct FieldMeta {
        bool is_first_field = true;
        int field_phase_id = 0;
        size_t file_loc = 0;
        struct Dropout { int line; int start; int end; };
        std::vector<Dropout> dropouts;
    };
    std::vector<FieldMeta> field_meta;
    FieldMeta current_field;
};
