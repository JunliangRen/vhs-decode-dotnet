#include "vhsdecode_cuda.h"

#include <algorithm>
#include <bit>
#include <chrono>
#include <cmath>
#include <complex>
#include <cstdint>
#include <iostream>
#include <limits>
#include <vector>

static_assert(sizeof(vhsdecode_cuda_ld_frequency_options) == 144);
static_assert(sizeof(vhsdecode_cuda_rf_batch_job) == 232);

int main()
{
    if (vhsdecode_cuda_get_abi_version() != VHSDECODE_CUDA_ABI_VERSION) {
        std::cerr << "ABI version mismatch\n";
        return 1;
    }

    std::int32_t device_count = 0;
    const auto count_status = vhsdecode_cuda_get_device_count(&device_count);
    if (count_status != VHSDECODE_CUDA_SUCCESS || device_count == 0) {
        std::cout << "No CUDA device available; smoke test skipped\n";
        return 77;
    }

    vhsdecode_cuda_device_info info{};
    info.struct_size = sizeof(info);
    if (vhsdecode_cuda_get_device_info(0, &info) != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "Unable to query CUDA device 0\n";
        return 1;
    }
    const std::uint64_t free_memory =
        info.reserved[VHSDECODE_CUDA_DEVICE_INFO_FREE_MEMORY_LOW_RESERVED_INDEX] |
        (static_cast<std::uint64_t>(
             info.reserved[VHSDECODE_CUDA_DEVICE_INFO_FREE_MEMORY_HIGH_RESERVED_INDEX])
         << 32u);
    if ((info.flags & VHSDECODE_CUDA_DEVICE_FLAG_MEMORY_INFO) == 0 ||
        free_memory == 0 || free_memory > info.total_global_memory) {
        std::cerr << "CUDA device memory information is invalid\n";
        return 1;
    }

    vhsdecode_cuda_context* context = nullptr;
    const auto create_status = vhsdecode_cuda_create(0, &context);
    if (create_status != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "Unable to create CUDA context: "
                  << vhsdecode_cuda_status_string(create_status) << '\n';
        return 1;
    }

    std::uint64_t capabilities = 0;
    if (vhsdecode_cuda_get_capabilities(context, &capabilities) !=
            VHSDECODE_CUDA_SUCCESS ||
        (capabilities & VHSDECODE_CUDA_CAP_LD_EFM_FREQUENCY) == 0 ||
        (capabilities & VHSDECODE_CUDA_CAP_LD_ANALOG_AUDIO_FREQUENCY) == 0) {
        std::cerr << "LD CUDA frequency capabilities are missing\n";
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    vhsdecode_cuda_self_test_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    const auto test_status = vhsdecode_cuda_self_test(context, &metrics);
    if (test_status != VHSDECODE_CUDA_SUCCESS || metrics.passed == 0) {
        char error[1024]{};
        vhsdecode_cuda_get_last_error(context, error, sizeof(error));
        std::cerr << "CUDA self-test failed: " << error << '\n';
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    constexpr std::uint32_t c2c_length = 16;
    constexpr std::uint32_t c2c_batches = 2;
    constexpr std::uint32_t c2c_count = c2c_length * c2c_batches;
    constexpr std::uint64_t c2c_bytes =
        c2c_count * sizeof(vhsdecode_cuda_complex64);
    vhsdecode_cuda_complex64 c2c_input[c2c_count]{};
    vhsdecode_cuda_complex64 c2c_output[c2c_count]{};
    for (std::uint32_t index = 0; index < c2c_count; ++index) {
        c2c_input[index].real = std::sin(index * 0.37) + (index % c2c_length);
        c2c_input[index].imag = std::cos(index * 0.19) - (index / c2c_length);
    }
    vhsdecode_cuda_buffer* c2c_input_buffer = nullptr;
    vhsdecode_cuda_buffer* c2c_spectrum_buffer = nullptr;
    vhsdecode_cuda_buffer* c2c_output_buffer = nullptr;
    auto destroy_c2c_buffers = [&]() {
        vhsdecode_cuda_buffer_destroy(c2c_output_buffer);
        vhsdecode_cuda_buffer_destroy(c2c_spectrum_buffer);
        vhsdecode_cuda_buffer_destroy(c2c_input_buffer);
    };
    auto c2c_status = vhsdecode_cuda_buffer_create(
        context, c2c_bytes, &c2c_input_buffer);
    if (c2c_status == VHSDECODE_CUDA_SUCCESS) {
        c2c_status = vhsdecode_cuda_buffer_create(
            context, c2c_bytes, &c2c_spectrum_buffer);
    }
    if (c2c_status == VHSDECODE_CUDA_SUCCESS) {
        c2c_status = vhsdecode_cuda_buffer_create(
            context, c2c_bytes, &c2c_output_buffer);
    }
    if (c2c_status == VHSDECODE_CUDA_SUCCESS) {
        constexpr auto maximum_u32 = std::numeric_limits<std::uint32_t>::max();
        constexpr auto maximum_int =
            static_cast<std::uint32_t>(std::numeric_limits<int>::max());
        double overflow_sample = 0.0;
        double overflow_output = 0.0;
        vhsdecode_cuda_rf_batch_job overflow_job{};
        overflow_job.struct_size = sizeof(overflow_job);
        overflow_job.mode = VHSDECODE_CUDA_RF_MODE_CVBS;
        overflow_job.flags = VHSDECODE_CUDA_RF_OUTPUT_VIDEO;
        overflow_job.sample_count = maximum_int - 1u;
        // Individual sample/spectrum byte counts still fit uint64_t here;
        // the eight-way pinned host staging aggregate does not.
        overflow_job.batch_count = 1'000'000'000u;
        overflow_job.input = &overflow_sample;
        overflow_job.video_output = &overflow_output;

        const bool overflow_checks_passed =
            vhsdecode_cuda_rf_batch_execute(context, &overflow_job) ==
                VHSDECODE_CUDA_INVALID_ARGUMENT &&
            vhsdecode_cuda_hilbert_r2c(
                context, c2c_spectrum_buffer, maximum_u32, maximum_u32, 0) ==
                VHSDECODE_CUDA_INVALID_ARGUMENT &&
            vhsdecode_cuda_conjugate_product_phase(
                context,
                c2c_input_buffer,
                c2c_output_buffer,
                maximum_u32,
                maximum_u32,
                nullptr,
                nullptr,
                0) == VHSDECODE_CUDA_INVALID_ARGUMENT &&
            vhsdecode_cuda_vhs_rust_demod(
                context,
                c2c_input_buffer,
                c2c_spectrum_buffer,
                c2c_output_buffer,
                maximum_u32,
                maximum_u32,
                1.0f,
                0) == VHSDECODE_CUDA_INVALID_ARGUMENT;
        if (!overflow_checks_passed) {
            std::cerr << "Overflowing CUDA operation dimensions were not rejected\n";
            destroy_c2c_buffers();
            vhsdecode_cuda_destroy(context);
            return 1;
        }
    }
    if (c2c_status == VHSDECODE_CUDA_SUCCESS) {
        c2c_status = vhsdecode_cuda_buffer_upload(
            context, c2c_input_buffer, 0, c2c_input, c2c_bytes, 1);
    }
    if (c2c_status == VHSDECODE_CUDA_SUCCESS) {
        c2c_status = vhsdecode_cuda_fft_c2c(
            context,
            c2c_input_buffer,
            c2c_spectrum_buffer,
            c2c_length,
            c2c_batches,
            VHSDECODE_CUDA_FFT_FORWARD,
            0,
            1);
    }
    if (c2c_status == VHSDECODE_CUDA_SUCCESS) {
        c2c_status = vhsdecode_cuda_fft_c2c(
            context,
            c2c_spectrum_buffer,
            c2c_output_buffer,
            c2c_length,
            c2c_batches,
            VHSDECODE_CUDA_FFT_INVERSE,
            1,
            1);
    }
    if (c2c_status == VHSDECODE_CUDA_SUCCESS) {
        c2c_status = vhsdecode_cuda_buffer_download(
            context, c2c_output_buffer, 0, c2c_output, c2c_bytes, 1);
    }
    if (c2c_status != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "Batched C2C CUDA smoke call failed: "
                  << vhsdecode_cuda_status_string(c2c_status) << '\n';
        destroy_c2c_buffers();
        vhsdecode_cuda_destroy(context);
        return 1;
    }
    for (std::uint32_t index = 0; index < c2c_count; ++index) {
        if (std::abs(c2c_output[index].real - c2c_input[index].real) > 1e-12 ||
            std::abs(c2c_output[index].imag - c2c_input[index].imag) > 1e-12) {
            std::cerr << "Batched C2C CUDA round-trip differs at element "
                      << index << '\n';
            destroy_c2c_buffers();
            vhsdecode_cuda_destroy(context);
            return 1;
        }
    }
    destroy_c2c_buffers();

    auto run_real_round_trip = [&](std::uint32_t length) {
        const std::uint64_t real_bytes =
            static_cast<std::uint64_t>(length) * sizeof(double);
        const std::uint64_t spectrum_bytes =
            static_cast<std::uint64_t>((length / 2) + 1) *
            sizeof(vhsdecode_cuda_complex64);
        std::vector<double> input(length);
        std::vector<double> output(length);
        for (std::uint32_t index = 0; index < length; ++index) {
            input[index] = (0.75 * std::sin(index * 0.017)) +
                (0.2 * std::cos(index * 0.091));
        }
        vhsdecode_cuda_buffer* real_buffer = nullptr;
        vhsdecode_cuda_buffer* spectrum_buffer = nullptr;
        vhsdecode_cuda_buffer* output_buffer = nullptr;
        auto status = vhsdecode_cuda_buffer_create(
            context, real_bytes, &real_buffer);
        if (status == VHSDECODE_CUDA_SUCCESS) {
            status = vhsdecode_cuda_buffer_create(
                context, spectrum_bytes, &spectrum_buffer);
        }
        if (status == VHSDECODE_CUDA_SUCCESS) {
            status = vhsdecode_cuda_buffer_create(
                context, real_bytes, &output_buffer);
        }
        if (status == VHSDECODE_CUDA_SUCCESS) {
            status = vhsdecode_cuda_buffer_upload(
                context, real_buffer, 0, input.data(), real_bytes, 0);
        }
        if (status == VHSDECODE_CUDA_SUCCESS) {
            status = vhsdecode_cuda_fft_r2c(
                context, real_buffer, spectrum_buffer, length, 1, 0);
        }
        if (status == VHSDECODE_CUDA_SUCCESS) {
            status = vhsdecode_cuda_fft_c2r(
                context, spectrum_buffer, output_buffer, length, 1, 1, 0);
        }
        if (status == VHSDECODE_CUDA_SUCCESS) {
            status = vhsdecode_cuda_buffer_download(
                context, output_buffer, 0, output.data(), real_bytes, 0);
        }
        bool passed = status == VHSDECODE_CUDA_SUCCESS;
        for (std::uint32_t index = 0; passed && index < length; ++index) {
            passed = std::abs(output[index] - input[index]) <= 1e-10;
        }
        vhsdecode_cuda_buffer_destroy(output_buffer);
        vhsdecode_cuda_buffer_destroy(spectrum_buffer);
        vhsdecode_cuda_buffer_destroy(real_buffer);
        return passed;
    };
    if (!run_real_round_trip(7040) || !run_real_round_trip(20'000)) {
        std::cerr << "7040/20K FP64 R2C round-trip smoke failed\n";
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    constexpr std::uint32_t standard_length = 4096;
    constexpr std::uint32_t standard_batches = 2;
    constexpr std::uint32_t standard_count = standard_length * standard_batches;
    constexpr std::uint32_t standard_bins = (standard_length / 2) + 1;
    constexpr double tau = 6.283185307179586476925286766559;
    std::vector<double> standard_input(standard_count);
    std::vector<vhsdecode_cuda_complex64> standard_identity(standard_bins);
    std::vector<double> standard_high_pass(standard_count);
    std::vector<double> standard_real(standard_count);
    std::vector<double> standard_imaginary(standard_count);
    std::vector<double> standard_envelope(standard_count);
    std::vector<double> standard_demod(standard_count);
    std::vector<double> standard_video(standard_count);
    std::vector<double> standard_low_pass(standard_count);
    std::vector<vhsdecode_cuda_complex64> standard_previous(standard_batches);
    std::vector<vhsdecode_cuda_complex64> standard_last(standard_batches);
    for (auto& coefficient : standard_identity) {
        coefficient.real = 1.0;
    }
    constexpr std::uint32_t tones[standard_batches] = {37, 211};
    for (std::uint32_t batch = 0; batch < standard_batches; ++batch) {
        const double phase = tau * tones[batch] / standard_length;
        standard_previous[batch].real = std::cos(-phase);
        standard_previous[batch].imag = std::sin(-phase);
        for (std::uint32_t index = 0; index < standard_length; ++index) {
            standard_input[(batch * standard_length) + index] =
                std::cos(tau * tones[batch] * index / standard_length);
        }
    }
    vhsdecode_cuda_rf_batch_job standard_job{};
    standard_job.struct_size = sizeof(standard_job);
    standard_job.flags = VHSDECODE_CUDA_RF_OUTPUT_HIGH_PASS |
        VHSDECODE_CUDA_RF_OUTPUT_ANALYTIC |
        VHSDECODE_CUDA_RF_OUTPUT_ENVELOPE |
        VHSDECODE_CUDA_RF_OUTPUT_DEMOD_RAW |
        VHSDECODE_CUDA_RF_OUTPUT_VIDEO |
        VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS |
        VHSDECODE_CUDA_RF_APPLY_MTF;
    standard_job.sample_count = standard_length;
    standard_job.batch_count = standard_batches;
    standard_job.mode = VHSDECODE_CUDA_RF_MODE_STANDARD_CONJUGATE;
    standard_job.demod_phase_scale = 1.0;
    standard_job.input = standard_input.data();
    standard_job.rf_video_filter = standard_identity.data();
    standard_job.rf_high_pass_filter = standard_identity.data();
    standard_job.mtf_filter = standard_identity.data();
    standard_job.demod_video_filter = standard_identity.data();
    standard_job.demod_video_low_pass_filter = standard_identity.data();
    standard_job.rf_high_pass_output = standard_high_pass.data();
    standard_job.analytic_real_output = standard_real.data();
    standard_job.analytic_imag_output = standard_imaginary.data();
    standard_job.envelope_output = standard_envelope.data();
    standard_job.demod_raw_output = standard_demod.data();
    standard_job.video_output = standard_video.data();
    standard_job.video_low_pass_output = standard_low_pass.data();
    standard_job.previous_analytic_per_batch = standard_previous.data();
    standard_job.last_analytic_per_batch = standard_last.data();
    const auto standard_status =
        vhsdecode_cuda_rf_batch_execute(context, &standard_job);
    if (standard_status != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "Standard RF CUDA batch call failed: "
                  << vhsdecode_cuda_status_string(standard_status) << '\n';
        vhsdecode_cuda_destroy(context);
        return 1;
    }
    for (std::uint32_t batch = 0; batch < standard_batches; ++batch) {
        const double expected_phase = tau * tones[batch] / standard_length;
        for (std::uint32_t index = 0; index < standard_length; ++index) {
            const std::uint32_t offset = (batch * standard_length) + index;
            const double angle = tau * tones[batch] * index / standard_length;
            const double expected_real = std::cos(angle);
            const double expected_imaginary = std::sin(angle);
            const double expected_demod = expected_phase;
            if (std::abs(standard_high_pass[offset] - expected_real) > 1e-10 ||
                std::abs(standard_real[offset] - expected_real) > 1e-10 ||
                std::abs(standard_imaginary[offset] - expected_imaginary) > 1e-10 ||
                std::abs(standard_envelope[offset] - 1.0) > 1e-10 ||
                std::abs(standard_demod[offset] - expected_demod) > 1e-10 ||
                std::abs(standard_video[offset] - expected_demod) > 1e-10 ||
                std::abs(standard_low_pass[offset] - expected_demod) > 1e-10) {
                std::cerr << "Standard RF CUDA batch differs at batch " << batch
                          << ", sample " << index << '\n';
                vhsdecode_cuda_destroy(context);
                return 1;
            }
        }
        const double last_angle =
            tau * tones[batch] * (standard_length - 1u) / standard_length;
        if (std::abs(standard_last[batch].real - std::cos(last_angle)) > 1e-10 ||
            std::abs(standard_last[batch].imag - std::sin(last_angle)) > 1e-10) {
            std::cerr << "Standard RF CUDA state differs at batch " << batch << '\n';
            vhsdecode_cuda_destroy(context);
            return 1;
        }
    }

    constexpr std::uint32_t benchmark_length = 32'768;
    constexpr std::uint32_t benchmark_batches = 2;
    constexpr std::uint32_t benchmark_count =
        benchmark_length * benchmark_batches;
    constexpr std::uint32_t benchmark_bins = (benchmark_length / 2u) + 1u;
    std::vector<double> benchmark_input(benchmark_count);
    std::vector<double> benchmark_video(benchmark_count);
    std::vector<double> benchmark_low_pass(benchmark_count);
    std::vector<vhsdecode_cuda_complex64> benchmark_identity(benchmark_bins);
    for (auto& coefficient : benchmark_identity) {
        coefficient.real = 1.0;
    }
    for (std::uint32_t batch = 0; batch < benchmark_batches; ++batch) {
        for (std::uint32_t index = 0; index < benchmark_length; ++index) {
            benchmark_input[(batch * benchmark_length) + index] =
                std::cos(tau * (71u + (batch * 102u)) * index /
                         benchmark_length);
        }
    }

    vhsdecode_cuda_rf_batch_job benchmark_split_job{};
    benchmark_split_job.struct_size = sizeof(benchmark_split_job);
    benchmark_split_job.flags = VHSDECODE_CUDA_RF_OUTPUT_VIDEO |
        VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS;
    benchmark_split_job.sample_count = benchmark_length;
    benchmark_split_job.batch_count = benchmark_batches;
    benchmark_split_job.mode = VHSDECODE_CUDA_RF_MODE_STANDARD_CONJUGATE;
    benchmark_split_job.demod_phase_scale = 1.0;
    benchmark_split_job.input = benchmark_input.data();
    benchmark_split_job.rf_video_filter = benchmark_identity.data();
    benchmark_split_job.demod_video_filter = benchmark_identity.data();
    benchmark_split_job.demod_video_low_pass_filter = benchmark_identity.data();
    benchmark_split_job.video_output = benchmark_video.data();
    benchmark_split_job.video_low_pass_output = benchmark_low_pass.data();

    vhsdecode_cuda_rf_batch_job benchmark_first_job = benchmark_split_job;
    benchmark_first_job.batch_count = 1;
    benchmark_first_job.stream_index = 0;
    vhsdecode_cuda_rf_batch_job benchmark_second_job = benchmark_first_job;
    benchmark_second_job.input += benchmark_length;
    benchmark_second_job.video_output += benchmark_length;
    benchmark_second_job.video_low_pass_output += benchmark_length;

    auto run_split_benchmark = [&]() {
        return vhsdecode_cuda_rf_batch_execute(context, &benchmark_split_job);
    };
    auto run_single_stream_benchmark = [&]() {
        auto status = vhsdecode_cuda_rf_batch_execute(context, &benchmark_first_job);
        return status == VHSDECODE_CUDA_SUCCESS
            ? vhsdecode_cuda_rf_batch_execute(context, &benchmark_second_job)
            : status;
    };
    if (run_split_benchmark() != VHSDECODE_CUDA_SUCCESS ||
        run_single_stream_benchmark() != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "Unable to warm the RF split microbenchmark\n";
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    constexpr std::uint32_t benchmark_iterations = 100;
    std::chrono::steady_clock::duration split_duration{};
    std::chrono::steady_clock::duration single_stream_duration{};
    for (std::uint32_t iteration = 0; iteration < benchmark_iterations;
         ++iteration) {
        auto start = std::chrono::steady_clock::now();
        const auto split_status = run_split_benchmark();
        split_duration += std::chrono::steady_clock::now() - start;
        start = std::chrono::steady_clock::now();
        const auto single_stream_status = run_single_stream_benchmark();
        single_stream_duration += std::chrono::steady_clock::now() - start;
        if (split_status != VHSDECODE_CUDA_SUCCESS ||
            single_stream_status != VHSDECODE_CUDA_SUCCESS) {
            std::cerr << "RF split microbenchmark execution failed\n";
            vhsdecode_cuda_destroy(context);
            return 1;
        }
    }
    const double split_microseconds =
        std::chrono::duration<double, std::micro>(split_duration).count() /
        benchmark_iterations;
    const double single_stream_microseconds =
        std::chrono::duration<double, std::micro>(single_stream_duration).count() /
        benchmark_iterations;
    std::cout << "Warm RF 32Kx2 all-video microbenchmark: split="
              << split_microseconds << " us, sequential stream0="
              << single_stream_microseconds << " us, speedup="
              << (single_stream_microseconds / split_microseconds) << "x\n";

    // The existing RF filters use the same exact-content cache as the LD
    // branches. Mutating an already-uploaded host array must invalidate it.
    standard_identity[tones[0]].real = 0.5;
    standard_identity[tones[1]].real = 0.5;
    std::fill(standard_high_pass.begin(), standard_high_pass.end(), 0.0);
    vhsdecode_cuda_rf_batch_job cached_high_pass_job{};
    cached_high_pass_job.struct_size = sizeof(cached_high_pass_job);
    cached_high_pass_job.flags = VHSDECODE_CUDA_RF_OUTPUT_HIGH_PASS;
    cached_high_pass_job.sample_count = standard_length;
    cached_high_pass_job.batch_count = standard_batches;
    cached_high_pass_job.mode = VHSDECODE_CUDA_RF_MODE_STANDARD_CONJUGATE;
    cached_high_pass_job.input = standard_input.data();
    cached_high_pass_job.rf_high_pass_filter = standard_identity.data();
    cached_high_pass_job.rf_high_pass_output = standard_high_pass.data();
    const auto cached_high_pass_status =
        vhsdecode_cuda_rf_batch_execute(context, &cached_high_pass_job);
    if (cached_high_pass_status != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "Cached RF high-pass CUDA batch call failed: "
                  << vhsdecode_cuda_status_string(cached_high_pass_status) << '\n';
        vhsdecode_cuda_destroy(context);
        return 1;
    }
    for (std::uint32_t batch = 0; batch < standard_batches; ++batch) {
        for (std::uint32_t index = 0; index < standard_length; ++index) {
            const std::uint32_t offset = (batch * standard_length) + index;
            const double expected = 0.5 * std::cos(
                tau * tones[batch] * index / standard_length);
            if (std::abs(standard_high_pass[offset] - expected) > 1e-10) {
                std::cerr << "Cached RF filter invalidation differs at batch "
                          << batch << ", sample " << index << '\n';
                vhsdecode_cuda_destroy(context);
                return 1;
            }
        }
    }

    constexpr std::uint32_t rust_count = 16;
    const double rust_real[rust_count] = {
        0.0, 1.0, 0.0, -1.0, 0.0, 1.0, -3.0, -2.0,
        7.0, 0.125, -0.25, 3.25, -4.5, 1e-20, -1e-20, 12345.678};
    const double rust_imaginary[rust_count] = {
        0.0, 0.0, 1.0, 0.0, -1.0, 2.0, 4.0, -5.0,
        -1.0, -0.75, 0.5, 1.5, -2.25, 2e-20, 3e-20, -9876.543};
    constexpr std::uint64_t rust_expected[rust_count] = {
        0ULL, 0ULL, 4706563005637197824ULL, 4706563005637197824ULL,
        4706563006174068736ULL, 4709949726936530944ULL,
        4704027848577384448ULL, 4708235600656859136ULL,
        4707292908485607424ULL, 4713938717541138432ULL,
        4711523152771940352ULL, 4713421323232083968ULL,
        4711114384628252672ULL, 4712050829232701440ULL,
        4702059408694181888ULL, 4711944988353626112ULL};
    double rust_output[rust_count]{};
    constexpr std::uint64_t rust_bytes = sizeof(rust_output);
    vhsdecode_cuda_buffer* rust_real_buffer = nullptr;
    vhsdecode_cuda_buffer* rust_imaginary_buffer = nullptr;
    vhsdecode_cuda_buffer* rust_output_buffer = nullptr;
    auto destroy_rust_buffers = [&]() {
        vhsdecode_cuda_buffer_destroy(rust_output_buffer);
        vhsdecode_cuda_buffer_destroy(rust_imaginary_buffer);
        vhsdecode_cuda_buffer_destroy(rust_real_buffer);
    };
    auto rust_status = vhsdecode_cuda_buffer_create(
        context, rust_bytes, &rust_real_buffer);
    if (rust_status == VHSDECODE_CUDA_SUCCESS) {
        rust_status = vhsdecode_cuda_buffer_create(
            context, rust_bytes, &rust_imaginary_buffer);
    }
    if (rust_status == VHSDECODE_CUDA_SUCCESS) {
        rust_status = vhsdecode_cuda_buffer_create(
            context, rust_bytes, &rust_output_buffer);
    }
    if (rust_status == VHSDECODE_CUDA_SUCCESS) {
        rust_status = vhsdecode_cuda_buffer_upload(
            context, rust_real_buffer, 0, rust_real, rust_bytes, 0);
    }
    if (rust_status == VHSDECODE_CUDA_SUCCESS) {
        rust_status = vhsdecode_cuda_buffer_upload(
            context, rust_imaginary_buffer, 0, rust_imaginary, rust_bytes, 0);
    }
    if (rust_status == VHSDECODE_CUDA_SUCCESS) {
        rust_status = vhsdecode_cuda_vhs_rust_demod(
            context,
            rust_real_buffer,
            rust_imaginary_buffer,
            rust_output_buffer,
            rust_count,
            1,
            17'900'000.0f,
            0);
    }
    if (rust_status == VHSDECODE_CUDA_SUCCESS) {
        rust_status = vhsdecode_cuda_buffer_download(
            context, rust_output_buffer, 0, rust_output, rust_bytes, 0);
    }
    if (rust_status != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "VHS Rust CUDA vector call failed: "
                  << vhsdecode_cuda_status_string(rust_status) << '\n';
        destroy_rust_buffers();
        vhsdecode_cuda_destroy(context);
        return 1;
    }
    for (std::uint32_t index = 0; index < rust_count; ++index) {
        if (std::bit_cast<std::uint64_t>(rust_output[index]) != rust_expected[index]) {
            std::cerr << "VHS Rust CUDA vector differs at sample " << index << '\n';
            destroy_rust_buffers();
            vhsdecode_cuda_destroy(context);
            return 1;
        }
    }
    destroy_rust_buffers();

    constexpr std::uint32_t cvbs_length = 16;
    constexpr std::uint32_t cvbs_batches = 2;
    constexpr std::uint32_t cvbs_count = cvbs_length * cvbs_batches;
    double cvbs_input[cvbs_count]{};
    vhsdecode_cuda_complex64 cvbs_identity[(cvbs_length / 2) + 1]{};
    double cvbs_video[cvbs_count]{};
    double cvbs_demod[cvbs_count]{};
    double cvbs_envelope[cvbs_count]{};
    double cvbs_low_pass[cvbs_count]{};
    for (std::uint32_t batch = 0; batch < cvbs_batches; ++batch) {
        for (std::uint32_t index = 0; index < cvbs_length; ++index) {
            cvbs_input[(batch * cvbs_length) + index] =
                (static_cast<double>(index) - 7.5) * 0.25 + (batch * 0.125);
        }
    }
    for (auto& coefficient : cvbs_identity) {
        coefficient.real = 1.0;
    }
    vhsdecode_cuda_rf_batch_job cvbs_job{};
    cvbs_job.struct_size = sizeof(cvbs_job);
    cvbs_job.flags = VHSDECODE_CUDA_RF_OUTPUT_ENVELOPE |
        VHSDECODE_CUDA_RF_OUTPUT_DEMOD_RAW |
        VHSDECODE_CUDA_RF_OUTPUT_VIDEO |
        VHSDECODE_CUDA_RF_OUTPUT_VIDEO_LOW_PASS;
    cvbs_job.sample_count = cvbs_length;
    cvbs_job.batch_count = cvbs_batches;
    cvbs_job.mode = VHSDECODE_CUDA_RF_MODE_CVBS;
    cvbs_job.cvbs_raw_scale = 2.0;
    cvbs_job.cvbs_raw_offset = 3.0;
    cvbs_job.input = cvbs_input;
    cvbs_job.demod_video_low_pass_filter = cvbs_identity;
    cvbs_job.envelope_output = cvbs_envelope;
    cvbs_job.demod_raw_output = cvbs_demod;
    cvbs_job.video_output = cvbs_video;
    cvbs_job.video_low_pass_output = cvbs_low_pass;
    const auto cvbs_status = vhsdecode_cuda_rf_batch_execute(context, &cvbs_job);
    if (cvbs_status != VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "CVBS RF batch smoke call failed: "
                  << vhsdecode_cuda_status_string(cvbs_status) << '\n';
        vhsdecode_cuda_destroy(context);
        return 1;
    }
    for (std::uint32_t index = 0; index < cvbs_count; ++index) {
        const double expected = (cvbs_input[index] * 2.0) + 3.0;
        if (std::abs(cvbs_video[index] - expected) > 1e-10 ||
            std::abs(cvbs_demod[index] - expected) > 1e-10 ||
            std::abs(cvbs_envelope[index] - std::abs(expected)) > 1e-10 ||
            std::abs(cvbs_low_pass[index] - expected) > 1e-10) {
            std::cerr << "CVBS RF batch differs at sample " << index << '\n';
            vhsdecode_cuda_destroy(context);
            return 1;
        }
    }

    constexpr std::uint32_t ld_length = 64;
    constexpr std::uint32_t ld_batches = 2;
    constexpr std::uint32_t ld_count = ld_length * ld_batches;
    constexpr std::uint32_t ld_bins = (ld_length / 2) + 1;
    constexpr std::uint32_t left_low_bin = 3;
    constexpr std::uint32_t left_bin_count = 16;
    constexpr std::uint32_t right_low_bin = 11;
    constexpr std::uint32_t right_bin_count = 8;
    using TestComplex = std::complex<double>;

    std::vector<double> ld_input(ld_count);
    for (std::uint32_t batch = 0; batch < ld_batches; ++batch) {
        for (std::uint32_t index = 0; index < ld_length; ++index) {
            const double first = static_cast<double>(5u + (batch * 2u));
            const double second = static_cast<double>(12u + batch);
            ld_input[(batch * ld_length) + index] =
                (0.7 * std::cos(tau * first * index / ld_length)) +
                (0.3 * std::sin(tau * second * index / ld_length)) +
                (0.0025 * (index + (batch * 3u)));
        }
    }

    std::vector<vhsdecode_cuda_complex64> efm_filter(ld_bins);
    std::vector<vhsdecode_cuda_complex64> ld_identity(ld_bins);
    std::vector<vhsdecode_cuda_complex64> left_filter(left_bin_count);
    std::vector<vhsdecode_cuda_complex64> right_filter(right_bin_count);
    for (std::uint32_t bin = 0; bin < ld_bins; ++bin) {
        efm_filter[bin].real = 0.2 + (0.01 * bin);
        efm_filter[bin].imag = -0.15 + (0.004 * bin);
        ld_identity[bin].real = 1.0;
    }
    for (std::uint32_t bin = 0; bin < left_bin_count; ++bin) {
        left_filter[bin].real = 0.5 + (0.02 * bin);
        left_filter[bin].imag = -0.1 + (0.007 * bin);
    }
    for (std::uint32_t bin = 0; bin < right_bin_count; ++bin) {
        right_filter[bin].real = 0.8 - (0.015 * bin);
        right_filter[bin].imag = 0.05 + (0.011 * bin);
    }

    std::vector<double> efm_output(ld_count);
    std::vector<double> ld_high_pass(ld_count);
    std::vector<vhsdecode_cuda_complex64> left_output(
        left_bin_count * ld_batches);
    std::vector<vhsdecode_cuda_complex64> right_output(
        right_bin_count * ld_batches);
    vhsdecode_cuda_ld_frequency_options ld_options{};
    ld_options.struct_size = sizeof(ld_options);
    ld_options.audio_left_low_bin = left_low_bin;
    ld_options.audio_left_bin_count = left_bin_count;
    ld_options.audio_right_low_bin = right_low_bin;
    ld_options.audio_right_bin_count = right_bin_count;
    ld_options.efm_filter = efm_filter.data();
    ld_options.audio_left_filter = left_filter.data();
    ld_options.audio_right_filter = right_filter.data();
    ld_options.efm_output = efm_output.data();
    ld_options.audio_left_output = left_output.data();
    ld_options.audio_right_output = right_output.data();

    vhsdecode_cuda_rf_batch_job ld_job{};
    ld_job.struct_size = sizeof(ld_job);
    ld_job.flags = VHSDECODE_CUDA_RF_OUTPUT_HIGH_PASS |
        VHSDECODE_CUDA_RF_OUTPUT_LD_EFM |
        VHSDECODE_CUDA_RF_OUTPUT_LD_ANALOG_AUDIO;
    ld_job.sample_count = ld_length;
    ld_job.batch_count = ld_batches;
    ld_job.stream_index = 1;
    ld_job.mode = VHSDECODE_CUDA_RF_MODE_STANDARD_CONJUGATE;
    ld_job.input = ld_input.data();
    ld_job.rf_high_pass_filter = ld_identity.data();
    ld_job.rf_high_pass_output = ld_high_pass.data();
    ld_job.reserved[VHSDECODE_CUDA_RF_LD_OPTIONS_RESERVED_INDEX] =
        static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(&ld_options));

    auto forward_dft = [&](const double* input) {
        std::vector<TestComplex> spectrum(ld_length);
        for (std::uint32_t bin = 0; bin < ld_length; ++bin) {
            TestComplex sum{};
            for (std::uint32_t index = 0; index < ld_length; ++index) {
                const double angle = -tau * bin * index / ld_length;
                sum += input[index] * std::polar(1.0, angle);
            }
            spectrum[bin] = sum;
        }
        return spectrum;
    };
    auto inverse_dft = [&](const std::vector<TestComplex>& spectrum) {
        std::vector<TestComplex> output(spectrum.size());
        for (std::size_t index = 0; index < spectrum.size(); ++index) {
            TestComplex sum{};
            for (std::size_t bin = 0; bin < spectrum.size(); ++bin) {
                const double angle = tau * static_cast<double>(bin * index) /
                    static_cast<double>(spectrum.size());
                sum += spectrum[bin] * std::polar(1.0, angle);
            }
            output[index] = sum / static_cast<double>(spectrum.size());
        }
        return output;
    };

    std::vector<double> expected_efm(ld_count);
    std::vector<TestComplex> expected_left(left_bin_count * ld_batches);
    std::vector<TestComplex> expected_right(right_bin_count * ld_batches);
    auto build_ld_reference = [&]() {
        for (std::uint32_t batch = 0; batch < ld_batches; ++batch) {
            const std::vector<TestComplex> spectrum = forward_dft(
                ld_input.data() + (batch * ld_length));
            std::vector<TestComplex> efm_spectrum(ld_length);
            for (std::uint32_t bin = 0; bin < ld_bins; ++bin) {
                efm_spectrum[bin] = spectrum[bin] * TestComplex(
                    efm_filter[bin].real, efm_filter[bin].imag);
            }
            const std::vector<TestComplex> efm = inverse_dft(efm_spectrum);
            for (std::uint32_t index = 0; index < ld_length; ++index) {
                expected_efm[(batch * ld_length) + index] = efm[index].real();
            }

            auto build_audio = [&](std::uint32_t low_bin,
                                   const std::vector<vhsdecode_cuda_complex64>& filter,
                                   std::vector<TestComplex>& destination) {
                const std::uint32_t bin_count =
                    static_cast<std::uint32_t>(filter.size());
                const std::uint32_t half = bin_count / 2u;
                std::vector<TestComplex> sliced(bin_count);
                for (std::uint32_t index = 0; index < half; ++index) {
                    sliced[index] = spectrum[low_bin + index];
                    sliced[half + index] =
                        spectrum[ld_length - low_bin - half + index];
                }
                for (std::uint32_t index = 0; index < bin_count; ++index) {
                    sliced[index] *= TestComplex(
                        filter[index].real, filter[index].imag);
                }
                const std::vector<TestComplex> analytic = inverse_dft(sliced);
                std::copy(analytic.begin(), analytic.end(),
                          destination.begin() + (batch * bin_count));
            };
            build_audio(left_low_bin, left_filter, expected_left);
            build_audio(right_low_bin, right_filter, expected_right);
        }
    };

    auto validate_ld_outputs = [&]() {
        constexpr double tolerance = 5e-11;
        for (std::uint32_t index = 0; index < ld_count; ++index) {
            if (std::abs(ld_high_pass[index] - ld_input[index]) > tolerance) {
                std::cerr << "Combined LD RF high-pass output differs at sample "
                          << index << '\n';
                return false;
            }
            if (std::abs(efm_output[index] - expected_efm[index]) > tolerance) {
                std::cerr << "LD EFM CUDA output differs at sample " << index << '\n';
                return false;
            }
        }
        for (std::size_t index = 0; index < left_output.size(); ++index) {
            if (std::abs(left_output[index].real - expected_left[index].real()) > tolerance ||
                std::abs(left_output[index].imag - expected_left[index].imag()) > tolerance) {
                std::cerr << "LD left analog-audio CUDA output differs at sample "
                          << index << '\n';
                return false;
            }
        }
        for (std::size_t index = 0; index < right_output.size(); ++index) {
            if (std::abs(right_output[index].real - expected_right[index].real()) > tolerance ||
                std::abs(right_output[index].imag - expected_right[index].imag()) > tolerance) {
                std::cerr << "LD right analog-audio CUDA output differs at sample "
                          << index << '\n';
                return false;
            }
        }
        return true;
    };

    build_ld_reference();
    auto ld_status = vhsdecode_cuda_rf_batch_execute(context, &ld_job);
    if (ld_status != VHSDECODE_CUDA_SUCCESS || !validate_ld_outputs()) {
        std::cerr << "LD frequency CUDA batch failed: "
                  << vhsdecode_cuda_status_string(ld_status) << '\n';
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    // Reuse the same workspace while mutating two filters. Exact cache
    // comparison must re-upload changed contents and retain the unchanged one.
    efm_filter[5].real += 0.125;
    left_filter[3].imag -= 0.05;
    std::fill(ld_high_pass.begin(), ld_high_pass.end(), 0.0);
    std::fill(efm_output.begin(), efm_output.end(), 0.0);
    std::fill(left_output.begin(), left_output.end(), vhsdecode_cuda_complex64{});
    std::fill(right_output.begin(), right_output.end(), vhsdecode_cuda_complex64{});
    build_ld_reference();
    ld_status = vhsdecode_cuda_rf_batch_execute(context, &ld_job);
    if (ld_status != VHSDECODE_CUDA_SUCCESS || !validate_ld_outputs()) {
        std::cerr << "LD cached-filter CUDA batch failed: "
                  << vhsdecode_cuda_status_string(ld_status) << '\n';
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    vhsdecode_cuda_ld_frequency_options invalid_ld_options = ld_options;
    invalid_ld_options.audio_left_low_bin = ld_length / 2u;
    ld_job.reserved[VHSDECODE_CUDA_RF_LD_OPTIONS_RESERVED_INDEX] =
        static_cast<std::uint64_t>(
            reinterpret_cast<std::uintptr_t>(&invalid_ld_options));
    if (vhsdecode_cuda_rf_batch_execute(context, &ld_job) !=
        VHSDECODE_CUDA_NOT_SUPPORTED) {
        std::cerr << "Out-of-half-spectrum LD audio slice was not rejected\n";
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    vhsdecode_cuda_buffer* post_context_buffer = nullptr;
    if (vhsdecode_cuda_buffer_create(context, 64u, &post_context_buffer) !=
        VHSDECODE_CUDA_SUCCESS) {
        std::cerr << "Unable to create buffer for post-context destruction test\n";
        vhsdecode_cuda_destroy(context);
        return 1;
    }

    std::cout << info.name << ": 32K FP64 self-test max_abs="
              << metrics.max_abs_error << ", nrmse=" << metrics.nrmse
              << "; C2C batch=2 passed; R2C 7040/20K passed"
              << "; 4K standard batch=2 passed"
              << "; VHS Rust vector bit-exact; CVBS batch passed"
              << "; LD EFM/audio batch=2 and filter cache passed"
              << "; overflow and buffer lifetime checks passed\n";
    vhsdecode_cuda_destroy(context);
    vhsdecode_cuda_buffer_destroy(post_context_buffer);
    return 0;
}
