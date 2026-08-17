#pragma once

#include <cstddef>
#include <cstdint>

#include "format/video_format.h"

#define MAX_DROPOUTS_PER_FIELD 512

// Detects RF signal dropouts and records their TBC positions. Like Exact,
// this does not alter decoded samples; optional concealment is export-time.
bool dropout_detect(const float* d_envelope,
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
                    const VideoFormat& fmt);
