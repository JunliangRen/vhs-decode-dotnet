#include "fm_video_lpf_response.h"

#include <cmath>
#include <cstddef>
#include <iomanip>
#include <iostream>
#include <string>
#include <vector>

namespace {

int failures = 0;

void expect_true(bool condition, const std::string& label)
{
    if (!condition) {
        std::cerr << "FAIL: " << label << '\n';
        ++failures;
    }
}

void expect_near(
    double actual,
    double expected,
    double tolerance,
    const std::string& label)
{
    if (!std::isfinite(actual) || std::abs(actual - expected) > tolerance) {
        std::cerr << std::setprecision(17)
                  << "FAIL: " << label
                  << " expected " << expected
                  << " +/- " << tolerance
                  << ", got " << actual << '\n';
        ++failures;
    }
}

void expect_finite_response(
    const std::vector<double>& response,
    const std::string& label)
{
    for (std::size_t index = 0; index < response.size(); ++index) {
        if (!std::isfinite(response[index]) || response[index] < 0.0) {
            std::cerr << std::setprecision(17)
                      << "FAIL: " << label << " bin " << index
                      << " is " << response[index] << '\n';
            ++failures;
            return;
        }
    }
}

std::size_t frequency_bin(double frequency_hz, double sample_rate_hz, std::size_t fft_size)
{
    return static_cast<std::size_t>(
        std::llround(frequency_hz * static_cast<double>(fft_size) / sample_rate_hz));
}

void test_pal_butterworth(double sample_rate_hz)
{
    constexpr int order = 6;
    constexpr double corner_hz = 3.4e6;
    constexpr std::size_t fft_size = 200U;
    const auto response = vhsdecode_cuda_fast::build_fm_video_lpf_half_spectrum(
        VideoProfile::PAL_625_50_VHS,
        order,
        corner_hz,
        sample_rate_hz,
        fft_size);

    const std::string rate_label = sample_rate_hz == 20.0e6 ? "PAL 20 MSPS" : "PAL 40 MSPS";
    expect_true(
        vhsdecode_cuda_fast::fm_video_lpf_shape(VideoProfile::PAL_625_50_VHS) ==
            vhsdecode_cuda_fast::FmVideoLpfShape::Butterworth,
        rate_label + " selects Butterworth");
    expect_true(response.size() == fft_size / 2U + 1U, rate_label + " bin count");
    expect_finite_response(response, rate_label);
    expect_near(response.front(), 1.0, 1.0e-12, rate_label + " DC gain");

    const std::size_t corner_bin = frequency_bin(corner_hz, sample_rate_hz, fft_size);
    expect_near(
        response[corner_bin],
        1.0 / std::sqrt(2.0),
        1.0e-11,
        rate_label + " 3.4 MHz gain");
}

void test_super_gaussian_profile(
    VideoProfile profile,
    double sample_rate_hz,
    std::size_t fft_size,
    const std::string& label)
{
    constexpr int order = 9;
    constexpr double width_hz = 6.6e6;
    const auto response = vhsdecode_cuda_fast::build_fm_video_lpf_half_spectrum(
        profile,
        order,
        width_hz,
        sample_rate_hz,
        fft_size);

    expect_true(
        vhsdecode_cuda_fast::fm_video_lpf_shape(profile) ==
            vhsdecode_cuda_fast::FmVideoLpfShape::SuperGaussian,
        label + " selects Super-Gaussian");
    expect_true(response.size() == fft_size / 2U + 1U, label + " bin count");
    expect_finite_response(response, label);
    expect_near(response.front(), 1.0, 1.0e-12, label + " DC gain");

    const std::size_t half_width_bin = frequency_bin(
        width_hz / 2.0,
        sample_rate_hz,
        fft_size);
    expect_near(
        response[half_width_bin],
        0.5,
        1.0e-12,
        label + " half-width gain");

    const std::size_t width_bin = frequency_bin(width_hz, sample_rate_hz, fft_size);
    expect_true(
        response[width_bin] < 1.0e-100,
        label + " full-width high-order rejection");
}

}  // namespace

int main()
{
    test_pal_butterworth(20.0e6);
    test_pal_butterworth(40.0e6);
    test_super_gaussian_profile(
        VideoProfile::NTSC_525_60_VHS,
        40.0e6,
        400U,
        "NTSC 40 MSPS");
    test_super_gaussian_profile(
        VideoProfile::MPAL_525_60_VHS,
        20.0e6,
        200U,
        "MPAL 20 MSPS");

    if (failures != 0) {
        std::cerr << failures << " FM video LPF response assertion(s) failed\n";
        return 1;
    }

    std::cout << "PAL Butterworth and NTSC/MPAL Super-Gaussian responses passed\n";
    return 0;
}
