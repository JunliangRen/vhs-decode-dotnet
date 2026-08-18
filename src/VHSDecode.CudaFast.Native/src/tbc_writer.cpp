// UTF-8 and replace-safe Windows adaptation of the pinned cuVHS TBC writer.

#include "io/tbc_writer.h"

#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#else
#include <sys/stat.h>
#endif

namespace {

#if defined(_WIN32)
std::wstring utf8_to_wide(const std::string& value) {
    if (value.empty()) {
        return {};
    }
    const int length = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0);
    if (length <= 0) {
        return {};
    }
    std::wstring result(static_cast<size_t>(length), L'\0');
    if (MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            length) != length) {
        return {};
    }
    return result;
}
#endif

std::FILE* open_utf8(const std::string& path, const char* mode) {
#if defined(_WIN32)
    const std::wstring wide_path = utf8_to_wide(path);
    std::wstring wide_mode;
    while (*mode != '\0') {
        wide_mode.push_back(static_cast<wchar_t>(*mode++));
    }
    return wide_path.empty() ? nullptr : _wfopen(wide_path.c_str(), wide_mode.c_str());
#else
    return std::fopen(path.c_str(), mode);
#endif
}

bool file_exists_utf8(const std::string& path) {
#if defined(_WIN32)
    const std::wstring wide_path = utf8_to_wide(path);
    return !wide_path.empty()
        && GetFileAttributesW(wide_path.c_str()) != INVALID_FILE_ATTRIBUTES;
#else
    struct stat status;
    return stat(path.c_str(), &status) == 0;
#endif
}

bool replace_utf8(const std::string& source, const std::string& destination) {
#if defined(_WIN32)
    const std::wstring wide_source = utf8_to_wide(source);
    const std::wstring wide_destination = utf8_to_wide(destination);
    return !wide_source.empty() && !wide_destination.empty()
        && MoveFileExW(
            wide_source.c_str(),
            wide_destination.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
#else
    return std::rename(source.c_str(), destination.c_str()) == 0;
#endif
}

}  // namespace

TBCWriter::~TBCWriter() {
    close();
}

bool TBCWriter::open(
    const std::string& output_base,
    const VideoFormat& format,
    bool overwrite) {
    close();
    fmt = format;
    field_count = 0;
    field_meta.clear();
    current_field = FieldMeta{};
    luma_path = output_base + ".tbc";
    chroma_path = output_base + "_chroma.tbc";
    json_path = output_base + ".tbc.json";

    if (!overwrite) {
        for (const std::string* path : {&luma_path, &chroma_path, &json_path}) {
            if (file_exists_utf8(*path)) {
                std::fprintf(
                    stderr,
                    "Output exists: %s (use --overwrite)\n",
                    path->c_str());
                return false;
            }
        }
    }

    luma_fp = open_utf8(luma_path, "wb");
    if (luma_fp == nullptr) {
        return false;
    }
    chroma_fp = open_utf8(chroma_path, "wb");
    if (chroma_fp == nullptr) {
        std::fclose(luma_fp);
        luma_fp = nullptr;
        return false;
    }
    const char* default_buffer_env =
        std::getenv("CUVHS_FORCE_DEFAULT_STDIO_BUFFER");
    const bool force_default_buffer = default_buffer_env != nullptr
        && default_buffer_env[0] != '\0'
        && default_buffer_env[0] != '0';
    if (!force_default_buffer) {
        constexpr size_t kOutputBufferBytes = 4U * 1024U * 1024U;
        std::setvbuf(luma_fp, nullptr, _IOFBF, kOutputBufferBytes);
        std::setvbuf(chroma_fp, nullptr, _IOFBF, kOutputBufferBytes);
    }
    return true;
}

bool TBCWriter::open_preview(
    const VideoFormat& format,
    const CudaPreviewOutputSettings& settings) {
    if (luma_fp != nullptr || chroma_fp != nullptr) {
        return false;
    }
    fmt = format;
    field_count = 0;
    field_meta.clear();
    current_field = FieldMeta{};
    if (preview_output == nullptr) {
        preview_output = std::make_unique<CudaPreviewOutput>();
    }
    if (!preview_output->open(format, settings)) {
        return false;
    }
    return true;
}

void TBCWriter::close() {
    if (preview_output != nullptr) {
        preview_output->close();
        preview_output.reset();
    }
    if (luma_fp != nullptr) {
        std::fclose(luma_fp);
        luma_fp = nullptr;
    }
    if (chroma_fp != nullptr) {
        std::fclose(chroma_fp);
        chroma_fp = nullptr;
    }
}

bool TBCWriter::write_luma_field(const uint16_t* data) {
    const size_t count = static_cast<size_t>(fmt.output_line_len)
        * static_cast<size_t>(fmt.output_field_lines);
    return luma_fp != nullptr
        && std::fwrite(data, sizeof(uint16_t), count, luma_fp) == count;
}

bool TBCWriter::write_chroma_field(const uint16_t* data) {
    const size_t count = static_cast<size_t>(fmt.output_line_len)
        * static_cast<size_t>(fmt.output_field_lines);
    return chroma_fp != nullptr
        && std::fwrite(data, sizeof(uint16_t), count, chroma_fp) == count;
}

void TBCWriter::add_dropout(int line, int start_x, int end_x) {
    current_field.dropouts.push_back({line, start_x, end_x});
}

void TBCWriter::set_first_field(bool is_first) {
    current_field.is_first_field = is_first;
}

void TBCWriter::set_field_phase_id(int phase_id) {
    current_field.field_phase_id = phase_id;
}

void TBCWriter::set_file_loc(size_t file_loc) {
    current_field.file_loc = file_loc;
}

void TBCWriter::finish_field() {
    field_meta.push_back(current_field);
    current_field = FieldMeta{};
    ++field_count;
}

bool TBCWriter::write_json() {
    if (preview_output != nullptr) {
        return true;
    }
    const std::string temporary_path = json_path + ".tmp";
    std::FILE* output = open_utf8(temporary_path, "wb");
    if (output == nullptr) {
        return false;
    }

    const char* system_name = "NTSC";
    if (fmt.profile == VideoProfile::PAL_625_50_VHS) {
        system_name = "PAL";
    } else if (fmt.profile == VideoProfile::MPAL_525_60_VHS) {
        system_name = "PAL-M";
    }

    constexpr double default_level_adjust = 0.1;
    const double black16b =
        ((0.0 - fmt.vsync_ire) * fmt.output_scale + fmt.output_zero)
        * (1.0 - default_level_adjust);
    const double white16b =
        ((100.0 - fmt.vsync_ire) * fmt.output_scale + fmt.output_zero)
        * (1.0 + default_level_adjust);
    const int burst_start = static_cast<int>(
        fmt.burst_start_us * 1e-6 * fmt.output_rate + 0.5);
    const int burst_end = static_cast<int>(
        fmt.burst_end_us * 1e-6 * fmt.output_rate + 0.5);
    const int active_start = fmt.system == VideoSystem::NTSC ? 134 : 185;
    const int active_end = fmt.system == VideoSystem::NTSC ? 894 : 1107;

    std::fprintf(output, "{\n");
    std::fprintf(output, "  \"videoParameters\": {\n");
    std::fprintf(output, "    \"system\": \"%s\",\n", system_name);
    std::fprintf(output, "    \"isSubcarrierLocked\": false,\n");
    std::fprintf(
        output,
        "    \"isSourcePal\": %s,\n",
        fmt.system == VideoSystem::PAL ? "true" : "false");
    std::fprintf(output, "    \"numberOfSequentialFields\": %d,\n", field_count);
    std::fprintf(output, "    \"black16bIre\": %.1f,\n", black16b);
    std::fprintf(output, "    \"white16bIre\": %.1f,\n", white16b);
    std::fprintf(output, "    \"sampleRate\": %.0f,\n", fmt.output_rate);
    std::fprintf(output, "    \"fieldWidth\": %d,\n", fmt.output_line_len);
    std::fprintf(output, "    \"fieldHeight\": %d,\n", fmt.output_field_lines);
    std::fprintf(output, "    \"colourBurstStart\": %d,\n", burst_start);
    std::fprintf(output, "    \"colourBurstEnd\": %d,\n", burst_end);
    std::fprintf(output, "    \"activeVideoStart\": %d,\n", active_start);
    std::fprintf(output, "    \"activeVideoEnd\": %d,\n", active_end);
    std::fprintf(output, "    \"tapeFormat\": \"VHS\",\n");
    std::fprintf(output, "    \"isMapped\": false\n");
    std::fprintf(output, "  },\n");
    std::fprintf(output, "  \"fields\": [\n");

    for (int index = 0; index < field_count; ++index) {
        const FieldMeta& field = field_meta[static_cast<size_t>(index)];
        std::fprintf(output, "    {\n");
        std::fprintf(
            output,
            "      \"isFirstField\": %s,\n",
            field.is_first_field ? "true" : "false");
        std::fprintf(output, "      \"seqNo\": %d,\n", index + 1);
        std::fprintf(output, "      \"fileLoc\": %zu,\n", field.file_loc);
        if (field.field_phase_id > 0) {
            std::fprintf(
                output,
                "      \"fieldPhaseID\": %d,\n",
                field.field_phase_id);
        }
        std::fprintf(output, "      \"dropOuts\": {\n");
        std::fprintf(output, "        \"fieldLine\": [");
        for (size_t dropout = 0; dropout < field.dropouts.size(); ++dropout) {
            std::fprintf(
                output,
                "%s%d",
                dropout == 0 ? "" : ", ",
                field.dropouts[dropout].line);
        }
        std::fprintf(output, "],\n        \"startx\": [");
        for (size_t dropout = 0; dropout < field.dropouts.size(); ++dropout) {
            std::fprintf(
                output,
                "%s%d",
                dropout == 0 ? "" : ", ",
                field.dropouts[dropout].start);
        }
        std::fprintf(output, "],\n        \"endx\": [");
        for (size_t dropout = 0; dropout < field.dropouts.size(); ++dropout) {
            std::fprintf(
                output,
                "%s%d",
                dropout == 0 ? "" : ", ",
                field.dropouts[dropout].end);
        }
        std::fprintf(output, "]\n      }\n");
        std::fprintf(output, "    }%s\n", index + 1 < field_count ? "," : "");
    }

    std::fprintf(output, "  ]\n}\n");
    if (std::fclose(output) != 0) {
        return false;
    }
    return replace_utf8(temporary_path, json_path);
}

bool TBCWriter::finalize() {
    if (preview_output != nullptr) {
        return preview_output->finalize();
    }
    const bool luma_flushed = luma_fp == nullptr || std::fflush(luma_fp) == 0;
    const bool chroma_flushed =
        chroma_fp == nullptr || std::fflush(chroma_fp) == 0;
    if (!luma_flushed || !chroma_flushed) {
        return false;
    }
    return write_json();
}

bool TBCWriter::write_preview_device_fields(
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
    int fields) {
    return preview_output != nullptr
        && preview_output->write_device_fields(
            d_luma,
            d_chroma,
            d_dropout_lines,
            d_dropout_starts,
            d_dropout_ends,
            d_dropout_counts,
            host_is_first_field,
            host_field_offsets,
            host_field_phase_ids,
            raw_offset,
            fields);
}

bool TBCWriter::output_complete() const {
    return preview_output != nullptr && preview_output->complete();
}

uint32_t TBCWriter::preview_frames_encoded() const {
    return preview_output != nullptr ? preview_output->frames_encoded() : 0;
}

uint32_t TBCWriter::preview_fields_scanned() const {
    return preview_output != nullptr ? preview_output->fields_scanned() : 0;
}

uint64_t TBCWriter::preview_encoded_bytes() const {
    return preview_output != nullptr ? preview_output->encoded_bytes() : 0;
}

const std::string& TBCWriter::preview_error() const {
    static const std::string empty;
    return preview_output != nullptr ? preview_output->error() : empty;
}
