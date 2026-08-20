#include "vhsdecode_cuda_fast.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <regex>
#include <string>
#include <vector>

namespace
{
constexpr double kPi = 3.141592653589793238462643383279502884;
constexpr double kSampleRate = 40'000'000.0;
constexpr double kLineRate = 15'734.264;
constexpr int kSamplesPerLine = 2542;
constexpr int kLinesPerField = 263;
// Cross multiple 16-field GPU batches so colour-frame, head-track, and field
// cadence state cannot accidentally reset at a chunk boundary.
constexpr int kFieldCount = 48;

struct SyntheticRf
{
    std::vector<float> samples;
    std::vector<int16_t> samples_int16;
};

size_t VHSDECODE_CUDA_FAST_CALL read_samples(
    void* user_data,
    void* destination,
    uint64_t sample_offset,
    size_t sample_count)
{
    const auto& source = *static_cast<SyntheticRf*>(user_data);
    if (sample_offset >= source.samples.size())
    {
        return 0;
    }

    const size_t available = source.samples.size() - static_cast<size_t>(sample_offset);
    const size_t count = std::min(sample_count, available);
    std::memcpy(
        destination,
        source.samples.data() + static_cast<size_t>(sample_offset),
        count * sizeof(float));
    return count;
}

size_t VHSDECODE_CUDA_FAST_CALL read_samples_int16(
    void* user_data,
    void* destination,
    uint64_t sample_offset,
    size_t sample_count)
{
    const auto& source = *static_cast<SyntheticRf*>(user_data);
    if (sample_offset >= source.samples_int16.size())
    {
        return 0;
    }

    const size_t available = source.samples_int16.size()
        - static_cast<size_t>(sample_offset);
    const size_t count = std::min(sample_count, available);
    std::memcpy(
        destination,
        source.samples_int16.data() + static_cast<size_t>(sample_offset),
        count * sizeof(int16_t));
    return count;
}

void set_pulse(
    std::vector<double>& frequencies,
    int start,
    int length,
    double sync_frequency)
{
    const int end = std::min(start + length, static_cast<int>(frequencies.size()));
    for (int sample = std::max(start, 0); sample < end; ++sample)
    {
        frequencies[static_cast<size_t>(sample)] = sync_frequency;
    }
}

SyntheticRf make_synthetic_ntsc_rf()
{
    constexpr int samples_per_field = kSamplesPerLine * kLinesPerField;
    constexpr double hz_per_ire = 1'000'000.0 / 140.0;
    constexpr double blank_frequency = 4'400'000.0 - hz_per_ire * 100.0;
    constexpr double sync_frequency = blank_frequency + hz_per_ire * -40.0;
    constexpr double chroma_frequency = (525.0 * (30.0 / 1.001)) * 40.0;
    constexpr int hsync_width = 188;
    constexpr int equalizing_width = 92;
    constexpr int vsync_width = 1084;
    constexpr int half_line = kSamplesPerLine / 2;

    SyntheticRf output;
    output.samples.reserve(static_cast<size_t>(samples_per_field) * kFieldCount);
    output.samples_int16.reserve(
        static_cast<size_t>(samples_per_field) * kFieldCount);
    double phase = 0.0;
    double chroma_phase = 0.0;

    for (int field = 0; field < kFieldCount; ++field)
    {
        std::vector<double> frequencies(samples_per_field, blank_frequency);
        for (int line = 0; line < kLinesPerField; ++line)
        {
            set_pulse(
                frequencies,
                line * kSamplesPerLine,
                hsync_width,
                sync_frequency);
        }

        const int vertical_start = 5 * kSamplesPerLine;
        for (int pulse = 0; pulse < 6; ++pulse)
        {
            set_pulse(
                frequencies,
                vertical_start + pulse * half_line,
                equalizing_width,
                sync_frequency);
        }
        const int serration_start = vertical_start + 3 * kSamplesPerLine;
        for (int pulse = 0; pulse < 6; ++pulse)
        {
            set_pulse(
                frequencies,
                serration_start + pulse * half_line,
                vsync_width,
                sync_frequency);
        }
        const int post_equalizing_start = vertical_start + 6 * kSamplesPerLine;
        for (int pulse = 0; pulse < 6; ++pulse)
        {
            set_pulse(
                frequencies,
                post_equalizing_start + pulse * half_line,
                equalizing_width,
                sync_frequency);
        }

        const double field_chroma_offset = (field & 1) != 0 ? kPi : 0.0;
        for (double frequency : frequencies)
        {
            phase += (2.0 * kPi * frequency) / kSampleRate;
            chroma_phase += (2.0 * kPi * chroma_frequency) / kSampleRate;
            const double rf =
                24'000.0 * std::sin(phase) +
                2'000.0 * std::sin(chroma_phase + field_chroma_offset);
            const int16_t sample = static_cast<int16_t>(
                std::clamp(std::round(rf), -32'768.0, 32'767.0));
            output.samples.push_back(static_cast<float>(sample));
            output.samples_int16.push_back(sample);
        }
        phase = std::remainder(phase, 2.0 * kPi);
        chroma_phase = std::remainder(chroma_phase, 2.0 * kPi);
    }

    return output;
}

bool files_equal(
    const std::filesystem::path& first,
    const std::filesystem::path& second)
{
    std::error_code error;
    const auto first_size = std::filesystem::file_size(first, error);
    if (error)
    {
        return false;
    }
    const auto second_size = std::filesystem::file_size(second, error);
    if (error || first_size != second_size)
    {
        return false;
    }

    std::ifstream first_stream(first, std::ios::binary);
    std::ifstream second_stream(second, std::ios::binary);
    std::vector<char> first_buffer(64 * 1024);
    std::vector<char> second_buffer(first_buffer.size());
    while (first_stream && second_stream)
    {
        first_stream.read(first_buffer.data(), static_cast<std::streamsize>(first_buffer.size()));
        second_stream.read(second_buffer.data(), static_cast<std::streamsize>(second_buffer.size()));
        const std::streamsize first_count = first_stream.gcount();
        if (first_count != second_stream.gcount() ||
            !std::equal(
                first_buffer.begin(),
                first_buffer.begin() + first_count,
                second_buffer.begin()))
        {
            return false;
        }
    }
    return true;
}

bool metadata_sequence_valid(
    const std::filesystem::path& json_path,
    uint32_t expected_fields)
{
    std::ifstream input(json_path, std::ios::binary);
    if (!input)
    {
        return false;
    }

    const std::string json{
        std::istreambuf_iterator<char>{input},
        std::istreambuf_iterator<char>{}};
    const std::regex field_pattern(
        R"json("isFirstField":\s*(true|false),\s*"seqNo":\s*([0-9]+),\s*"fileLoc":\s*([0-9]+),\s*"fieldPhaseID":\s*([0-9]+),)json");
    constexpr int expected_phase_ids[] = {1, 4, 3, 2};
    uint32_t index = 0;
    int phase_offset = -1;
    uint64_t previous_file_location = 0;
    for (std::sregex_iterator match(json.begin(), json.end(), field_pattern), end;
         match != end;
         ++match)
    {
        if (index >= expected_fields)
        {
            return false;
        }

        const bool is_first = (*match)[1].str() == "true";
        const uint32_t sequence = static_cast<uint32_t>(
            std::stoul((*match)[2].str()));
        const uint64_t file_location = std::stoull((*match)[3].str());
        const int phase_id = std::stoi((*match)[4].str());
        if (index == 0)
        {
            for (int candidate = 0; candidate < 4; ++candidate)
            {
                if (expected_phase_ids[candidate] == phase_id)
                {
                    phase_offset = candidate;
                    break;
                }
            }
        }
        if (is_first != ((index & 1U) == 0U) ||
            sequence != index + 1U ||
            (index > 0 && file_location <= previous_file_location) ||
            phase_offset < 0 ||
            ((phase_id == 1 || phase_id == 3) != is_first) ||
            phase_id != expected_phase_ids[(phase_offset + index) & 3U])
        {
            return false;
        }

        previous_file_location = file_location;
        ++index;
    }
    return index == expected_fields;
}

void remove_outputs(const std::filesystem::path& output_base)
{
    std::error_code ignored;
    std::filesystem::remove(output_base.string() + ".tbc", ignored);
    std::filesystem::remove(output_base.string() + "_chroma.tbc", ignored);
    std::filesystem::remove(output_base.string() + ".tbc.json", ignored);
}

bool run_once(
    SyntheticRf& source,
    const std::string& output_base,
    vhsdecode_cuda_fast_input_sample_format input_format,
    vhsdecode_cuda_fast_result_v1& result,
    uint32_t device_decimation_factor = 1,
    uint32_t maximum_output_fields = 0)
{
    vhsdecode_cuda_fast_config_v1 config{};
    config.struct_size = sizeof(config);
    config.profile = VHSDECODE_CUDA_FAST_PROFILE_NTSC;
    config.tape_speed = VHSDECODE_CUDA_FAST_TAPE_SPEED_SP;
    config.device_id = 0;
    config.sample_rate_mhz = device_decimation_factor == 2 ? 20.0 : 40.0;
    config.total_samples = source.samples.size();
    config.output_base_utf8 = output_base.c_str();
    config.overwrite = 1;
    config.input_sample_format = input_format;
    config.maximum_output_fields = maximum_output_fields;
    config.device_decimation_factor = device_decimation_factor;
    config.read_callback = input_format == VHSDECODE_CUDA_FAST_INPUT_INT16
        ? read_samples_int16
        : read_samples;
    config.user_data = &source;

    result = {};
    result.struct_size = sizeof(result);
    const int32_t status = vhsdecode_cuda_fast_run(&config, &result);
    if (status != VHSDECODE_CUDA_FAST_STATUS_OK)
    {
        std::fprintf(
            stderr,
            "Synthetic NTSC decode failed (%d): %s\n",
            status,
            vhsdecode_cuda_fast_get_last_error());
        return false;
    }
    return true;
}
} // namespace

int main()
{
    vhsdecode_cuda_fast_runtime_info_v1 info{};
    info.struct_size = sizeof(info);
    const int32_t probe = vhsdecode_cuda_fast_get_runtime_info(0, &info);
    if (probe == VHSDECODE_CUDA_FAST_STATUS_CUDA_UNAVAILABLE)
    {
        std::printf("Synthetic NTSC CUDA test skipped; no CUDA device is available.\n");
        return 0;
    }
    if (probe != VHSDECODE_CUDA_FAST_STATUS_OK)
    {
        std::fprintf(stderr, "CUDA probe failed before synthetic NTSC test.\n");
        return 1;
    }

    SyntheticRf source = make_synthetic_ntsc_rf();
    const auto suffix = std::to_string(
        std::chrono::steady_clock::now().time_since_epoch().count());
    const std::filesystem::path float_base =
        std::filesystem::temp_directory_path() / ("vhsdecode-cuda-ntsc-f32-" + suffix);
    const std::filesystem::path int16_first_base =
        std::filesystem::temp_directory_path() / ("vhsdecode-cuda-ntsc-s16-a-" + suffix);
    const std::filesystem::path int16_second_base =
        std::filesystem::temp_directory_path() / ("vhsdecode-cuda-ntsc-s16-b-" + suffix);
    const std::filesystem::path half_rate_base =
        std::filesystem::temp_directory_path() / ("vhsdecode-cuda-ntsc-s16-20msps-" + suffix);
    remove_outputs(float_base);
    remove_outputs(int16_first_base);
    remove_outputs(int16_second_base);
    remove_outputs(half_rate_base);

    vhsdecode_cuda_fast_result_v1 float_result{};
    vhsdecode_cuda_fast_result_v1 int16_first_result{};
    vhsdecode_cuda_fast_result_v1 int16_second_result{};
    vhsdecode_cuda_fast_result_v1 half_rate_result{};
    const bool decoded =
        run_once(
            source,
            float_base.string(),
            VHSDECODE_CUDA_FAST_INPUT_FLOAT32,
            float_result) &&
        run_once(
            source,
            int16_first_base.string(),
            VHSDECODE_CUDA_FAST_INPUT_INT16,
            int16_first_result) &&
        run_once(
            source,
            int16_second_base.string(),
            VHSDECODE_CUDA_FAST_INPUT_INT16,
            int16_second_result) &&
        run_once(
            source,
            half_rate_base.string(),
            VHSDECODE_CUDA_FAST_INPUT_INT16,
            half_rate_result,
            2,
            40);

    const uintmax_t bytes_per_field =
        static_cast<uintmax_t>(910) * 263 * sizeof(uint16_t);
    std::error_code size_error;
    const uintmax_t luma_size = std::filesystem::file_size(
        int16_first_base.string() + ".tbc",
        size_error);
    const bool same_luma =
        files_equal(
            float_base.string() + ".tbc",
            int16_first_base.string() + ".tbc") &&
        files_equal(
            int16_first_base.string() + ".tbc",
            int16_second_base.string() + ".tbc");
    const bool same_chroma =
        files_equal(
            float_base.string() + "_chroma.tbc",
            int16_first_base.string() + "_chroma.tbc") &&
        files_equal(
            int16_first_base.string() + "_chroma.tbc",
            int16_second_base.string() + "_chroma.tbc");
    const bool same_json =
        files_equal(
            float_base.string() + ".tbc.json",
            int16_first_base.string() + ".tbc.json") &&
        files_equal(
            int16_first_base.string() + ".tbc.json",
            int16_second_base.string() + ".tbc.json");
    const bool valid_metadata =
        metadata_sequence_valid(
            float_base.string() + ".tbc.json",
            float_result.fields_written) &&
        metadata_sequence_valid(
            int16_first_base.string() + ".tbc.json",
            int16_first_result.fields_written) &&
        metadata_sequence_valid(
            int16_second_base.string() + ".tbc.json",
            int16_second_result.fields_written) &&
        metadata_sequence_valid(
            half_rate_base.string() + ".tbc.json",
            half_rate_result.fields_written);
    const bool valid_contract =
        decoded &&
        !size_error &&
        float_result.fields_written >= 40 &&
        float_result.fields_written == int16_first_result.fields_written &&
        int16_first_result.fields_written == int16_second_result.fields_written &&
        int16_first_result.output_line_length == 910 &&
        int16_first_result.output_field_lines == 263 &&
        half_rate_result.fields_written == 40 &&
        half_rate_result.output_line_length == 910 &&
        half_rate_result.output_field_lines == 263 &&
        luma_size == bytes_per_field * int16_first_result.fields_written &&
        same_luma &&
        same_chroma &&
        same_json &&
        valid_metadata;

    if (!valid_contract)
    {
        std::fprintf(
            stderr,
            "Synthetic NTSC output contract or determinism failed: "
            "decoded=%d fields=%u/%u/%u/half:%u geometry=%ux%u half:%ux%u bytes=%llu/%llu "
            "luma=%d chroma=%d json=%d metadata=%d.\n",
            decoded ? 1 : 0,
            float_result.fields_written,
            int16_first_result.fields_written,
            int16_second_result.fields_written,
            half_rate_result.fields_written,
            int16_first_result.output_line_length,
            int16_first_result.output_field_lines,
            half_rate_result.output_line_length,
            half_rate_result.output_field_lines,
            static_cast<unsigned long long>(luma_size),
            static_cast<unsigned long long>(
                bytes_per_field * int16_first_result.fields_written),
            same_luma ? 1 : 0,
            same_chroma ? 1 : 0,
            same_json ? 1 : 0,
            valid_metadata ? 1 : 0);
        std::fprintf(
            stderr,
            "Synthetic NTSC diagnostic outputs retained at %s, %s, %s, and %s.\n",
            float_base.string().c_str(),
            int16_first_base.string().c_str(),
            int16_second_base.string().c_str(),
            half_rate_base.string().c_str());
        return 2;
    }

    remove_outputs(float_base);
    remove_outputs(int16_first_base);
    remove_outputs(int16_second_base);
    remove_outputs(half_rate_base);
    std::printf(
        "Synthetic NTSC CUDA pipeline passed with %u fields.\n",
        int16_first_result.fields_written);
    return 0;
}
