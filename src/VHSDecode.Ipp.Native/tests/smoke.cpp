#include "vhsdecode_ipp.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <numbers>
#include <thread>
#include <vector>

namespace {

bool expect_status(int32_t status, const char* operation)
{
    if (status == VHSDECODE_IPP_STATUS_OK) {
        return true;
    }

    std::cerr << operation << " failed: " << status << " ("
              << vhsdecode_ipp_status_string(status) << ")\n";
    return false;
}

bool check_round_trip(vhsdecode_ipp_fft64_context* context, int32_t length, double seed)
{
    std::vector<double> input(static_cast<size_t>(length));
    std::vector<vhsdecode_ipp_complex64> spectrum(static_cast<size_t>(length / 2 + 1));
    std::vector<double> output(static_cast<size_t>(length));

    for (int32_t i = 0; i < length; ++i) {
        const double phase = (2.0 * std::numbers::pi * static_cast<double>(i)) /
            static_cast<double>(length);
        input[static_cast<size_t>(i)] =
            std::sin((3.0 + seed) * phase) + 0.25 * std::cos((17.0 + seed) * phase);
    }

    if (!expect_status(vhsdecode_ipp_fft64_forward_real(
            context, input.data(), length, spectrum.data(), length / 2 + 1),
            "FFT forward")) {
        return false;
    }
    if (!expect_status(vhsdecode_ipp_fft64_inverse_real(
            context, spectrum.data(), length / 2 + 1, output.data(), length),
            "FFT inverse")) {
        return false;
    }

    double max_error = 0.0;
    for (int32_t i = 0; i < length; ++i) {
        max_error = (std::max)(max_error, std::abs(
            input[static_cast<size_t>(i)] - output[static_cast<size_t>(i)]));
    }
    if (max_error > 1.0e-11) {
        std::cerr << "FFT round-trip error too large: " << max_error << '\n';
        return false;
    }
    return true;
}

std::vector<double> make_filter_input(int32_t length)
{
    std::vector<double> input(static_cast<size_t>(length));
    for (int32_t i = 0; i < length; ++i) {
        const double sample = static_cast<double>(i);
        input[static_cast<size_t>(i)] =
            (0.65 * std::sin(sample * 0.071)) +
            (0.22 * std::cos(sample * 0.193)) +
            (static_cast<double>((i % 11) - 5) * 0.013);
    }
    return input;
}

bool compare_vectors(
    const char* operation,
    const std::vector<double>& expected,
    const std::vector<double>& actual,
    double tolerance = 2.0e-12)
{
    if (expected.size() != actual.size()) {
        std::cerr << operation << " length mismatch: " << expected.size()
                  << " versus " << actual.size() << '\n';
        return false;
    }

    for (size_t i = 0; i < expected.size(); ++i) {
        const double error = std::abs(expected[i] - actual[i]);
        const double allowed = tolerance * (1.0 + std::abs(expected[i]));
        if (error > allowed) {
            std::cerr << operation << " mismatch at " << i
                      << ": expected " << expected[i]
                      << ", actual " << actual[i]
                      << ", error " << error
                      << ", allowed " << allowed << '\n';
            return false;
        }
    }
    return true;
}

void reference_iir(
    const std::vector<double>& numerator,
    const std::vector<double>& denominator,
    const std::vector<double>& input,
    std::vector<double>& state,
    std::vector<double>& output)
{
    const size_t coefficient_count = (std::max)(numerator.size(), denominator.size());
    const size_t order = coefficient_count - 1;
    const double a0 = denominator[0];
    std::vector<double> b(coefficient_count, 0.0);
    std::vector<double> a(coefficient_count, 0.0);
    for (size_t i = 0; i < numerator.size(); ++i) {
        b[i] = numerator[i] / a0;
    }
    for (size_t i = 0; i < denominator.size(); ++i) {
        a[i] = denominator[i] / a0;
    }

    output.resize(input.size());
    for (size_t sample = 0; sample < input.size(); ++sample) {
        const double x = input[sample];
        const double y = (b[0] * x) + state[0];
        for (size_t i = 1; i < order; ++i) {
            state[i - 1] = (b[i] * x) + state[i] - (a[i] * y);
        }
        state[order - 1] = (b[order] * x) - (a[order] * y);
        output[sample] = y;
    }
}

void reference_sos(
    const std::vector<vhsdecode_ipp_sos64_section>& sections,
    const std::vector<double>& input,
    std::vector<double>& state,
    std::vector<double>& output)
{
    output = input;
    for (size_t section_index = 0; section_index < sections.size(); ++section_index) {
        const auto& section = sections[section_index];
        const double b0 = section.b0 / section.a0;
        const double b1 = section.b1 / section.a0;
        const double b2 = section.b2 / section.a0;
        const double a1 = section.a1 / section.a0;
        const double a2 = section.a2 / section.a0;
        double z1 = state[section_index * 2];
        double z2 = state[(section_index * 2) + 1];
        for (double& value : output) {
            const double x = value;
            const double y = (b0 * x) + z1;
            z1 = (b1 * x) - (a1 * y) + z2;
            z2 = (b2 * x) - (a2 * y);
            value = y;
        }
        state[section_index * 2] = z1;
        state[(section_index * 2) + 1] = z2;
    }
}

bool check_direct_iir()
{
    const std::vector<double> numerator{0.18, -0.07, 0.035};
    const std::vector<double> denominator{2.0, -0.6, 0.18, -0.04};
    const std::vector<double> initial_state{0.025, -0.0125, 0.006};
    const std::vector<double> input = make_filter_input(257);

    std::vector<double> expected_state = initial_state;
    std::vector<double> expected_output;
    reference_iir(
        numerator, denominator, input, expected_state, expected_output);

    vhsdecode_ipp_iir64_context* context = nullptr;
    if (!expect_status(vhsdecode_ipp_iir64_create(
            numerator.data(),
            static_cast<int32_t>(numerator.size()),
            denominator.data(),
            static_cast<int32_t>(denominator.size()),
            initial_state.data(),
            static_cast<int32_t>(initial_state.size()),
            &context),
            "Direct IIR create")) {
        return false;
    }

    std::vector<double> created_state(initial_state.size());
    if (!expect_status(vhsdecode_ipp_iir64_get_state(
            context, created_state.data(), static_cast<int32_t>(created_state.size())),
            "Direct IIR initial state") ||
        !compare_vectors("Direct IIR initial state", initial_state, created_state, 0.0) ||
        !expect_status(vhsdecode_ipp_iir64_process(context, nullptr, nullptr, 0),
            "Direct IIR empty block")) {
        vhsdecode_ipp_iir64_destroy(context);
        return false;
    }

    std::vector<double> output(input.size());
    if (!expect_status(vhsdecode_ipp_iir64_process(
            context,
            input.data(),
            output.data(),
            static_cast<int32_t>(input.size())),
            "Direct IIR process")) {
        vhsdecode_ipp_iir64_destroy(context);
        return false;
    }
    std::vector<double> final_state(initial_state.size());
    if (!expect_status(vhsdecode_ipp_iir64_get_state(
            context, final_state.data(), static_cast<int32_t>(final_state.size())),
            "Direct IIR get state") ||
        !compare_vectors("Direct IIR output", expected_output, output) ||
        !compare_vectors("Direct IIR final state", expected_state, final_state)) {
        vhsdecode_ipp_iir64_destroy(context);
        return false;
    }

    if (!expect_status(vhsdecode_ipp_iir64_reset(context), "Direct IIR reset")) {
        vhsdecode_ipp_iir64_destroy(context);
        return false;
    }
    std::vector<double> reset_state(initial_state.size(), 1.0);
    if (!expect_status(vhsdecode_ipp_iir64_get_state(
            context, reset_state.data(), static_cast<int32_t>(reset_state.size())),
            "Direct IIR get reset state") ||
        !compare_vectors(
            "Direct IIR reset state",
            std::vector<double>(initial_state.size(), 0.0),
            reset_state,
            0.0)) {
        vhsdecode_ipp_iir64_destroy(context);
        return false;
    }

    if (!expect_status(vhsdecode_ipp_iir64_set_state(
            context, initial_state.data(), static_cast<int32_t>(initial_state.size())),
            "Direct IIR set state")) {
        vhsdecode_ipp_iir64_destroy(context);
        return false;
    }
    std::vector<double> in_place = input;
    if (!expect_status(vhsdecode_ipp_iir64_process(
            context,
            in_place.data(),
            in_place.data(),
            static_cast<int32_t>(in_place.size())),
            "Direct IIR in-place process") ||
        !compare_vectors("Direct IIR in-place output", expected_output, in_place)) {
        vhsdecode_ipp_iir64_destroy(context);
        return false;
    }
    if (!expect_status(vhsdecode_ipp_iir64_destroy(context), "Direct IIR destroy") ||
        !expect_status(vhsdecode_ipp_iir64_destroy(nullptr), "Direct IIR destroy NULL")) {
        return false;
    }

    vhsdecode_ipp_iir64_context* split_context = nullptr;
    if (!expect_status(vhsdecode_ipp_iir64_create(
            numerator.data(),
            static_cast<int32_t>(numerator.size()),
            denominator.data(),
            static_cast<int32_t>(denominator.size()),
            initial_state.data(),
            static_cast<int32_t>(initial_state.size()),
            &split_context),
            "Direct IIR split create")) {
        return false;
    }
    constexpr int32_t split = 73;
    std::vector<double> split_output(input.size());
    const bool split_ok =
        expect_status(vhsdecode_ipp_iir64_process(
            split_context, input.data(), split_output.data(), split),
            "Direct IIR first block") &&
        expect_status(vhsdecode_ipp_iir64_process(
            split_context,
            input.data() + split,
            split_output.data() + split,
            static_cast<int32_t>(input.size()) - split),
            "Direct IIR second block");
    std::vector<double> split_state(initial_state.size());
    const bool state_ok = expect_status(vhsdecode_ipp_iir64_get_state(
        split_context, split_state.data(), static_cast<int32_t>(split_state.size())),
        "Direct IIR split state");
    vhsdecode_ipp_iir64_destroy(split_context);
    return split_ok && state_ok &&
        compare_vectors("Direct IIR split output", output, split_output) &&
        compare_vectors("Direct IIR split state", final_state, split_state);
}

bool check_sos_iir()
{
    const std::vector<vhsdecode_ipp_sos64_section> sections{
        {0.35, 0.1, -0.03, 2.0, -0.4, 0.08},
        {0.42, -0.16, 0.04, 1.0, -0.25, 0.06}};
    const std::vector<double> initial_state{0.02, -0.01, 0.015, -0.007};
    const std::vector<double> input = make_filter_input(257);

    std::vector<double> expected_state = initial_state;
    std::vector<double> expected_output;
    reference_sos(sections, input, expected_state, expected_output);

    vhsdecode_ipp_sos64_context* context = nullptr;
    if (!expect_status(vhsdecode_ipp_sos64_create(
            sections.data(),
            static_cast<int32_t>(sections.size()),
            initial_state.data(),
            static_cast<int32_t>(initial_state.size()),
            &context),
            "SOS create")) {
        return false;
    }

    std::vector<double> created_state(initial_state.size());
    if (!expect_status(vhsdecode_ipp_sos64_get_state(
            context, created_state.data(), static_cast<int32_t>(created_state.size())),
            "SOS initial state") ||
        !compare_vectors("SOS initial state", initial_state, created_state, 0.0) ||
        !expect_status(vhsdecode_ipp_sos64_process(context, nullptr, nullptr, 0),
            "SOS empty block")) {
        vhsdecode_ipp_sos64_destroy(context);
        return false;
    }

    std::vector<double> output(input.size());
    if (!expect_status(vhsdecode_ipp_sos64_process(
            context,
            input.data(),
            output.data(),
            static_cast<int32_t>(input.size())),
            "SOS process")) {
        vhsdecode_ipp_sos64_destroy(context);
        return false;
    }
    std::vector<double> final_state(initial_state.size());
    if (!expect_status(vhsdecode_ipp_sos64_get_state(
            context, final_state.data(), static_cast<int32_t>(final_state.size())),
            "SOS get state") ||
        !compare_vectors("SOS output", expected_output, output) ||
        !compare_vectors("SOS final state", expected_state, final_state)) {
        vhsdecode_ipp_sos64_destroy(context);
        return false;
    }

    if (!expect_status(vhsdecode_ipp_sos64_reset(context), "SOS reset")) {
        vhsdecode_ipp_sos64_destroy(context);
        return false;
    }
    std::vector<double> reset_state(initial_state.size(), 1.0);
    if (!expect_status(vhsdecode_ipp_sos64_get_state(
            context, reset_state.data(), static_cast<int32_t>(reset_state.size())),
            "SOS get reset state") ||
        !compare_vectors(
            "SOS reset state",
            std::vector<double>(initial_state.size(), 0.0),
            reset_state,
            0.0)) {
        vhsdecode_ipp_sos64_destroy(context);
        return false;
    }
    if (!expect_status(vhsdecode_ipp_sos64_set_state(
            context, initial_state.data(), static_cast<int32_t>(initial_state.size())),
            "SOS set state")) {
        vhsdecode_ipp_sos64_destroy(context);
        return false;
    }
    std::vector<double> in_place = input;
    if (!expect_status(vhsdecode_ipp_sos64_process(
            context,
            in_place.data(),
            in_place.data(),
            static_cast<int32_t>(in_place.size())),
            "SOS in-place process") ||
        !compare_vectors("SOS in-place output", expected_output, in_place)) {
        vhsdecode_ipp_sos64_destroy(context);
        return false;
    }
    if (!expect_status(vhsdecode_ipp_sos64_destroy(context), "SOS destroy") ||
        !expect_status(vhsdecode_ipp_sos64_destroy(nullptr), "SOS destroy NULL")) {
        return false;
    }

    vhsdecode_ipp_sos64_context* split_context = nullptr;
    if (!expect_status(vhsdecode_ipp_sos64_create(
            sections.data(),
            static_cast<int32_t>(sections.size()),
            initial_state.data(),
            static_cast<int32_t>(initial_state.size()),
            &split_context),
            "SOS split create")) {
        return false;
    }
    constexpr int32_t split = 73;
    std::vector<double> split_output(input.size());
    const bool split_ok =
        expect_status(vhsdecode_ipp_sos64_process(
            split_context, input.data(), split_output.data(), split),
            "SOS first block") &&
        expect_status(vhsdecode_ipp_sos64_process(
            split_context,
            input.data() + split,
            split_output.data() + split,
            static_cast<int32_t>(input.size()) - split),
            "SOS second block");
    std::vector<double> split_state(initial_state.size());
    const bool state_ok = expect_status(vhsdecode_ipp_sos64_get_state(
        split_context, split_state.data(), static_cast<int32_t>(split_state.size())),
        "SOS split state");
    vhsdecode_ipp_sos64_destroy(split_context);
    return split_ok && state_ok &&
        compare_vectors("SOS split output", output, split_output) &&
        compare_vectors("SOS split state", final_state, split_state);
}

} // namespace

int main()
{
    if (vhsdecode_ipp_get_abi_version() != VHSDECODE_IPP_ABI_VERSION) {
        std::cerr << "ABI version mismatch\n";
        return 1;
    }

    vhsdecode_ipp_runtime_info_v1 runtime{};
    runtime.struct_size = sizeof(runtime);
    if (!expect_status(vhsdecode_ipp_get_runtime_info(&runtime), "Runtime probe")) {
        return 1;
    }
    std::cout << runtime.ipp_name << ' ' << runtime.ipp_version
              << " build=" << runtime.ipp_build
              << " target=" << runtime.ipp_target_cpu
              << " enabled-features=0x" << std::hex << runtime.enabled_cpu_features
              << std::dec << '\n';

    if (vhsdecode_ipp_fft64_create(12, nullptr) != VHSDECODE_IPP_STATUS_NULL_POINTER) {
        std::cerr << "Null output-context validation failed\n";
        return 1;
    }
    vhsdecode_ipp_fft64_context* invalid_context = nullptr;
    if (vhsdecode_ipp_fft64_create(12, &invalid_context) !=
        VHSDECODE_IPP_STATUS_UNSUPPORTED_LENGTH) {
        std::cerr << "Invalid FFT length validation failed\n";
        return 1;
    }

    constexpr int32_t fft_length = 1024;
    vhsdecode_ipp_fft64_context* context = nullptr;
    if (!expect_status(vhsdecode_ipp_fft64_create(fft_length, &context), "FFT create")) {
        return 1;
    }

    std::vector<double> impulse(static_cast<size_t>(fft_length));
    std::vector<vhsdecode_ipp_complex64> impulse_spectrum(
        static_cast<size_t>(fft_length / 2 + 1));
    impulse[0] = 1.0;
    if (!expect_status(vhsdecode_ipp_fft64_forward_real(
            context,
            impulse.data(),
            fft_length,
            impulse_spectrum.data(),
            fft_length / 2 + 1),
            "FFT impulse forward")) {
        vhsdecode_ipp_fft64_destroy(context);
        return 1;
    }
    for (const auto& bin : impulse_spectrum) {
        if (std::abs(bin.real - 1.0) > 1.0e-14 || std::abs(bin.imag) > 1.0e-14) {
            std::cerr << "FFT CCS-to-complex layout check failed\n";
            vhsdecode_ipp_fft64_destroy(context);
            return 1;
        }
    }

    if (!check_round_trip(context, fft_length, 0.0)) {
        vhsdecode_ipp_fft64_destroy(context);
        return 1;
    }

    const vhsdecode_ipp_complex64 lhs[] = {{1.0, 2.0}, {3.0, 4.0}};
    const vhsdecode_ipp_complex64 rhs[] = {{3.0, -4.0}, {-2.0, 5.0}};
    vhsdecode_ipp_complex64 product[2]{};
    if (!expect_status(vhsdecode_ipp_complex64_multiply(lhs, rhs, product, 2),
            "Complex multiply")) {
        vhsdecode_ipp_fft64_destroy(context);
        return 1;
    }
    if (std::abs(product[0].real - 11.0) > 1.0e-14 ||
        std::abs(product[0].imag - 2.0) > 1.0e-14) {
        std::cerr << "Complex multiply produced an unexpected result\n";
        vhsdecode_ipp_fft64_destroy(context);
        return 1;
    }

    const vhsdecode_ipp_complex64 analytic[] = {{3.0, 4.0}, {-1.0, 0.0}};
    double magnitude[2]{};
    double phase[2]{};
    if (!expect_status(vhsdecode_ipp_complex64_magnitude_phase(
            analytic, magnitude, phase, 2), "Magnitude/phase")) {
        vhsdecode_ipp_fft64_destroy(context);
        return 1;
    }
    if (std::abs(magnitude[0] - 5.0) > 1.0e-14 ||
        std::abs(phase[0] - std::atan2(4.0, 3.0)) > 1.0e-14) {
        std::cerr << "Magnitude/phase produced an unexpected result\n";
        vhsdecode_ipp_fft64_destroy(context);
        return 1;
    }

    std::atomic<bool> concurrent_ok{true};
    std::vector<std::thread> workers;
    for (int worker = 0; worker < 4; ++worker) {
        workers.emplace_back([context, worker, &concurrent_ok]() {
            for (int iteration = 0; iteration < 20; ++iteration) {
                if (!check_round_trip(context, fft_length,
                        static_cast<double>(worker + iteration) * 0.01)) {
                    concurrent_ok.store(false, std::memory_order_relaxed);
                    return;
                }
            }
        });
    }
    for (auto& worker : workers) {
        worker.join();
    }
    if (!concurrent_ok.load(std::memory_order_relaxed)) {
        vhsdecode_ipp_fft64_destroy(context);
        return 1;
    }

    if (!expect_status(vhsdecode_ipp_fft64_destroy(context), "FFT destroy") ||
        !expect_status(vhsdecode_ipp_fft64_destroy(nullptr), "FFT destroy NULL")) {
        return 1;
    }

    if (!check_direct_iir() || !check_sos_iir()) {
        return 1;
    }

    std::cout << "vhsdecode_ipp smoke test passed\n";
    return 0;
}
