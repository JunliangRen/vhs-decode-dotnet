#pragma once

#include <cstddef>
#include <cstdint>

#include "format/video_format.h"

// Converts signed PCM16 RF samples to the FP32 CUDA signal plane. Every int16
// value is exactly representable in FP32, so this is bit-stable across launches.
bool cuda_fast_convert_s16_to_float(
    const int16_t* d_source,
    float* d_destination,
    size_t sample_count);

// Computes the luma TBC per-line wow/level adjustment directly from device
// line locations.  The output remains on the default CUDA stream so the
// following resampler consumes it without a host synchronization.
bool cuda_fast_compute_k5_level_adjust(
    const float* d_linelocs,
    const int* d_is_first_field,
    float* d_level_adjust,
    int num_fields,
    const VideoFormat& fmt);

// Computes both chroma per-line level adjustment and the final per-pixel
// source coordinate/level maps on the GPU.  output_level_adjust_b may alias
// output_level_adjust_a or be null.
bool cuda_fast_compute_chroma_geometry(
    const float* d_linelocs,
    const int* d_is_first_field,
    float* d_level_adjust_a,
    float* d_level_adjust_b,
    float* d_source_coords,
    float* d_source_level_adjust,
    int num_fields,
    const VideoFormat& fmt);
