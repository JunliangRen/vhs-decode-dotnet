#pragma once

#include "format/video_format.h"

#include <cmath>
#include <complex>
#include <cstddef>
#include <stdexcept>
#include <vector>

namespace vhsdecode_cuda_fast {

enum class FmVideoLpfShape {
    Butterworth,
    SuperGaussian,
};

inline FmVideoLpfShape fm_video_lpf_shape(VideoProfile profile) noexcept
{
    return profile == VideoProfile::PAL_625_50_VHS
        ? FmVideoLpfShape::Butterworth
        : FmVideoLpfShape::SuperGaussian;
}

namespace fm_video_lpf_detail {

constexpr double pi = 3.14159265358979323846;
constexpr double tau = 6.28318530717958647692;

struct DigitalZpk {
    std::vector<std::complex<double>> zeros;
    std::vector<std::complex<double>> poles;
    std::complex<double> gain{1.0, 0.0};
};

inline DigitalZpk butterworth_analog_prototype(int order)
{
    DigitalZpk result;
    result.poles.reserve(static_cast<std::size_t>(order));
    for (int index = 0; index < order; ++index) {
        const double theta = pi *
            (2.0 * static_cast<double>(index) + 1.0 + static_cast<double>(order)) /
            (2.0 * static_cast<double>(order));
        result.poles.emplace_back(std::polar(1.0, theta));
    }
    return result;
}

inline DigitalZpk transform_lowpass(const DigitalZpk& input, double cutoff_radians)
{
    DigitalZpk result;
    result.zeros.reserve(input.zeros.size());
    result.poles.reserve(input.poles.size());
    for (const auto& zero : input.zeros) {
        result.zeros.push_back(zero * cutoff_radians);
    }
    for (const auto& pole : input.poles) {
        result.poles.push_back(pole * cutoff_radians);
    }
    const int degree = static_cast<int>(input.poles.size()) -
        static_cast<int>(input.zeros.size());
    result.gain = input.gain * std::pow(cutoff_radians, degree);
    return result;
}

inline DigitalZpk bilinear_transform(const DigitalZpk& input, double sample_rate_hz)
{
    DigitalZpk result;
    result.zeros.reserve(input.zeros.size());
    result.poles.reserve(input.poles.size());
    const std::complex<double> twice_sample_rate{2.0 * sample_rate_hz, 0.0};
    for (const auto& zero : input.zeros) {
        result.zeros.push_back(
            (twice_sample_rate + zero) / (twice_sample_rate - zero));
    }
    for (const auto& pole : input.poles) {
        result.poles.push_back(
            (twice_sample_rate + pole) / (twice_sample_rate - pole));
    }
    const int degree = static_cast<int>(input.poles.size()) -
        static_cast<int>(input.zeros.size());
    for (int index = 0; index < degree; ++index) {
        result.zeros.emplace_back(-1.0, 0.0);
    }
    std::complex<double> numerator = input.gain;
    for (const auto& zero : input.zeros) {
        numerator *= (twice_sample_rate - zero);
    }
    std::complex<double> denominator{1.0, 0.0};
    for (const auto& pole : input.poles) {
        denominator *= (twice_sample_rate - pole);
    }
    result.gain = numerator / denominator;
    return result;
}

inline DigitalZpk design_butterworth_lowpass(
    int order,
    double cutoff_hz,
    double sample_rate_hz)
{
    const double warped_cutoff = 2.0 * sample_rate_hz *
        std::tan(pi * cutoff_hz / sample_rate_hz);
    return bilinear_transform(
        transform_lowpass(butterworth_analog_prototype(order), warped_cutoff),
        sample_rate_hz);
}

inline double zpk_magnitude_at_radians(const DigitalZpk& filter, double radians)
{
    const std::complex<double> z = std::exp(std::complex<double>(0.0, radians));
    std::complex<double> numerator = filter.gain;
    for (const auto& zero : filter.zeros) {
        numerator *= (z - zero);
    }
    std::complex<double> denominator{1.0, 0.0};
    for (const auto& pole : filter.poles) {
        denominator *= (z - pole);
    }
    return std::abs(numerator / denominator);
}

inline double super_gaussian_magnitude(double frequency_hz, double corner_hz, int order)
{
    const double half_log_two = std::log(2.0) / 2.0;
    const double scale = std::pow(half_log_two, 1.0 / (2.0 * order));
    const double normalized = 2.0 * frequency_hz * scale / corner_hz;
    return std::exp(-2.0 * std::pow(normalized, 2.0 * order));
}

}  // namespace fm_video_lpf_detail

inline std::vector<double> build_fm_video_lpf_half_spectrum(
    VideoProfile profile,
    int order,
    double corner_hz,
    double sample_rate_hz,
    std::size_t fft_size)
{
    if (order <= 0 || !std::isfinite(corner_hz) || !std::isfinite(sample_rate_hz) ||
        corner_hz <= 0.0 || sample_rate_hz <= 0.0 ||
        corner_hz >= sample_rate_hz / 2.0 || fft_size < 2U) {
        throw std::invalid_argument("Invalid FM video low-pass response parameters");
    }

    const std::size_t frequency_bins = fft_size / 2U + 1U;
    std::vector<double> response(frequency_bins);
    if (fm_video_lpf_shape(profile) == FmVideoLpfShape::Butterworth) {
        const auto filter = fm_video_lpf_detail::design_butterworth_lowpass(
            order,
            corner_hz,
            sample_rate_hz);
        const double step = fm_video_lpf_detail::tau / static_cast<double>(fft_size);
        for (std::size_t index = 0; index < frequency_bins; ++index) {
            response[index] = fm_video_lpf_detail::zpk_magnitude_at_radians(
                filter,
                step * static_cast<double>(index));
        }
        return response;
    }

    const double nyquist_hz = sample_rate_hz / 2.0;
    const double bin_denominator = static_cast<double>(frequency_bins - 1U);
    for (std::size_t index = 0; index < frequency_bins; ++index) {
        const double frequency_hz = static_cast<double>(index) * nyquist_hz /
            bin_denominator;
        response[index] = fm_video_lpf_detail::super_gaussian_magnitude(
            frequency_hz,
            corner_hz,
            order);
    }
    return response;
}

}  // namespace vhsdecode_cuda_fast
