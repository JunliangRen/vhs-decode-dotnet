// Windows-safe callback reader used by the CUDA-fast bridge. The conversion
// behavior follows cuVHS RawReader at the pinned upstream revision.

#include "io/raw_reader.h"

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <limits>
#include <vector>

#if defined(_WIN32)
#include <io.h>
#include <fcntl.h>
#else
#include <unistd.h>
#endif

namespace {

bool seek_file(std::FILE* file, uint64_t offset) {
#if defined(_WIN32)
    return _fseeki64(file, static_cast<__int64>(offset), SEEK_SET) == 0;
#else
    if (offset > static_cast<uint64_t>(std::numeric_limits<off_t>::max())) {
        return false;
    }
    return fseeko(file, static_cast<off_t>(offset), SEEK_SET) == 0;
#endif
}

uint64_t tell_file(std::FILE* file) {
#if defined(_WIN32)
    const __int64 value = _ftelli64(file);
#else
    const off_t value = ftello(file);
#endif
    return value < 0 ? 0 : static_cast<uint64_t>(value);
}

int read_descriptor(int descriptor, void* destination, size_t byte_count) {
#if defined(_WIN32)
    const unsigned int bounded = static_cast<unsigned int>(
        std::min(byte_count, static_cast<size_t>(std::numeric_limits<int>::max())));
    return _read(descriptor, destination, bounded);
#else
    const ssize_t result = ::read(descriptor, destination, byte_count);
    if (result > std::numeric_limits<int>::max()) {
        return std::numeric_limits<int>::max();
    }
    return static_cast<int>(result);
#endif
}

}  // namespace

RawReader::~RawReader() {
    close();
}

bool RawReader::open(const std::string& path, InputFormat format) {
    close();
    fmt = format;
    file = std::fopen(path.c_str(), "rb");
    if (file == nullptr) {
        return false;
    }

    owns_file = true;
    if (!seek_file(file, 0)) {
        close();
        return false;
    }
#if defined(_WIN32)
    if (_fseeki64(file, 0, SEEK_END) != 0) {
#else
    if (fseeko(file, 0, SEEK_END) != 0) {
#endif
        close();
        return false;
    }

    file_size = static_cast<size_t>(tell_file(file));
    total_sample_count = file_size / input_format_bytes_per_sample(fmt);
    sequential_sample = 0;
    streaming = false;
    return seek_file(file, 0);
}

bool RawReader::open_stream(int descriptor, InputFormat format) {
    close();
    if (descriptor < 0) {
        return false;
    }

    fmt = format;
    stream_descriptor = descriptor;
    streaming = true;
#if defined(_WIN32)
    _setmode(stream_descriptor, _O_BINARY);
#endif
    return true;
}

bool RawReader::open_stdin(InputFormat format) {
    return open_stream(0, format);
}

bool RawReader::open_callback(
    RawReaderCallback read_callback,
    void* user_data,
    size_t sample_count,
    RawReaderCallbackFormat read_callback_format) {
    close();
    if (read_callback == nullptr || sample_count == 0) {
        return false;
    }

    callback = read_callback;
    callback_user_data = user_data;
    total_sample_count = sample_count;
    fmt = InputFormat::S16;
    callback_format = read_callback_format;
    sequential_sample = 0;
    streaming = false;
    return true;
}

void RawReader::close() {
    if (file != nullptr && owns_file) {
        std::fclose(file);
    }
    file = nullptr;
    stream_descriptor = -1;
    owns_file = false;
    streaming = false;
    file_size = 0;
    total_sample_count = 0;
    sequential_sample = 0;
    callback = nullptr;
    callback_user_data = nullptr;
    callback_format = RawReaderCallbackFormat::Float32;
}

void RawReader::condition(float* destination, size_t sample_count) const {
    if (!conditioning.dc_correct || sample_count == 0) {
        return;
    }

    float sum = 0.0f;
    for (size_t index = 0; index < sample_count; ++index) {
        sum += destination[index];
    }
    const float mean = sum / static_cast<float>(sample_count);
    for (size_t index = 0; index < sample_count; ++index) {
        destination[index] -= mean;
    }
}

void RawReader::convert(
    const void* source,
    float* destination,
    size_t sample_count) const {
    switch (fmt) {
        case InputFormat::U8: {
            const auto* values = static_cast<const uint8_t*>(source);
            for (size_t index = 0; index < sample_count; ++index) {
                destination[index] =
                    (static_cast<float>(values[index]) - 128.0f) * 256.0f;
            }
            break;
        }
        case InputFormat::S16: {
            const auto* values = static_cast<const int16_t*>(source);
            for (size_t index = 0; index < sample_count; ++index) {
                destination[index] = static_cast<float>(values[index]);
            }
            break;
        }
        case InputFormat::U16: {
            const auto* values = static_cast<const uint16_t*>(source);
            for (size_t index = 0; index < sample_count; ++index) {
                destination[index] = static_cast<float>(values[index]) - 32768.0f;
            }
            break;
        }
    }
    condition(destination, sample_count);
}

size_t RawReader::read_at(
    float* destination,
    size_t offset,
    size_t sample_count) {
    if (destination == nullptr || sample_count == 0 || offset >= total_sample_count) {
        return 0;
    }

    const size_t bounded = std::min(sample_count, total_sample_count - offset);
    if (callback != nullptr) {
        if (callback_format == RawReaderCallbackFormat::Float32) {
            const size_t read = callback(
                callback_user_data,
                destination,
                static_cast<uint64_t>(offset),
                bounded);
            const size_t valid = std::min(read, bounded);
            condition(destination, valid);
            return valid;
        }

        std::vector<int16_t> buffer(bounded);
        const size_t read = callback(
            callback_user_data,
            buffer.data(),
            static_cast<uint64_t>(offset),
            bounded);
        const size_t valid = std::min(read, bounded);
        convert(buffer.data(), destination, valid);
        return valid;
    }

    if (file == nullptr) {
        return 0;
    }

    const size_t bytes_per_sample = input_format_bytes_per_sample(fmt);
    const uint64_t byte_offset = static_cast<uint64_t>(offset) * bytes_per_sample;
    if (!seek_file(file, byte_offset)) {
        return 0;
    }

    std::vector<uint8_t> buffer(bounded * bytes_per_sample);
    const size_t bytes_read = std::fread(buffer.data(), 1, buffer.size(), file);
    const size_t samples_read = bytes_read / bytes_per_sample;
    convert(buffer.data(), destination, samples_read);
    return samples_read;
}

size_t RawReader::read_raw_at(
    void* destination,
    size_t offset,
    size_t sample_count) {
    if (destination == nullptr || sample_count == 0 || offset >= total_sample_count) {
        return 0;
    }

    const size_t bounded = std::min(sample_count, total_sample_count - offset);
    if (callback != nullptr) {
        if (callback_format != RawReaderCallbackFormat::Int16) {
            return 0;
        }
        return std::min(
            callback(
                callback_user_data,
                destination,
                static_cast<uint64_t>(offset),
                bounded),
            bounded);
    }
    if (file == nullptr) {
        return 0;
    }

    const size_t bytes_per_sample = input_format_bytes_per_sample(fmt);
    if (!seek_file(file, static_cast<uint64_t>(offset) * bytes_per_sample)) {
        return 0;
    }
    return std::fread(destination, bytes_per_sample, bounded, file);
}

size_t RawReader::read_next(float* destination, size_t sample_count) {
    if (callback != nullptr || file != nullptr) {
        const size_t read = read_at(destination, sequential_sample, sample_count);
        sequential_sample += read;
        return read;
    }
    if (!streaming || stream_descriptor < 0 || destination == nullptr) {
        return 0;
    }

    const size_t bytes_per_sample = input_format_bytes_per_sample(fmt);
    std::vector<uint8_t> buffer(sample_count * bytes_per_sample);
    size_t total = 0;
    while (total < buffer.size()) {
        const int read = read_descriptor(
            stream_descriptor,
            buffer.data() + total,
            buffer.size() - total);
        if (read <= 0) {
            break;
        }
        total += static_cast<size_t>(read);
    }
    const size_t samples_read = total / bytes_per_sample;
    convert(buffer.data(), destination, samples_read);
    sequential_sample += samples_read;
    return samples_read;
}

size_t RawReader::read_next_raw(void* destination, size_t sample_count) {
    if (callback != nullptr || file != nullptr) {
        const size_t read = read_raw_at(destination, sequential_sample, sample_count);
        sequential_sample += read;
        return read;
    }
    if (!streaming || stream_descriptor < 0 || destination == nullptr) {
        return 0;
    }

    const size_t bytes_per_sample = input_format_bytes_per_sample(fmt);
    const int read = read_descriptor(
        stream_descriptor,
        destination,
        sample_count * bytes_per_sample);
    if (read <= 0) {
        return 0;
    }
    const size_t samples_read = static_cast<size_t>(read) / bytes_per_sample;
    sequential_sample += samples_read;
    return samples_read;
}
