#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <string>

#include "format/video_format.h"

struct InputConditioning {
    bool dc_correct = false;
};

enum class RawReaderCallbackFormat : uint32_t {
    Float32 = 0,
    Int16 = 1
};

using RawReaderCallback = size_t (*)(
    void* user_data,
    void* destination,
    uint64_t sample_offset,
    size_t sample_count);

struct RawReader {
    ~RawReader();

    void set_conditioning(const InputConditioning& cfg) { conditioning = cfg; }
    InputConditioning get_conditioning() const { return conditioning; }

    bool open(const std::string& path, InputFormat format);
    bool open_stream(int descriptor, InputFormat format);
    bool open_stdin(InputFormat format);
    bool open_callback(
        RawReaderCallback callback,
        void* user_data,
        size_t total_sample_count,
        RawReaderCallbackFormat callback_format);
    void close();

    size_t read_at(float* destination, size_t offset, size_t sample_count);
    size_t read_raw_at(void* destination, size_t offset, size_t sample_count);
    size_t read_next(float* destination, size_t sample_count);
    size_t read_next_raw(void* destination, size_t sample_count);

    size_t total_samples() const { return total_sample_count; }
    size_t size_bytes() const { return file_size; }
    InputFormat format() const { return fmt; }
    bool is_stream() const { return streaming; }
    bool is_seekable() const { return !streaming; }
    bool callback_returns_int16() const {
        return callback != nullptr && callback_format == RawReaderCallbackFormat::Int16;
    }

private:
    std::FILE* file = nullptr;
    int stream_descriptor = -1;
    bool owns_file = false;
    bool streaming = false;
    InputFormat fmt = InputFormat::U8;
    InputConditioning conditioning;
    size_t file_size = 0;
    size_t total_sample_count = 0;
    size_t sequential_sample = 0;
    RawReaderCallback callback = nullptr;
    void* callback_user_data = nullptr;
    RawReaderCallbackFormat callback_format = RawReaderCallbackFormat::Float32;

    void condition(float* destination, size_t sample_count) const;
    void convert(const void* source, float* destination, size_t sample_count) const;
};
