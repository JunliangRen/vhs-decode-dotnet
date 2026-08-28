using System.Numerics;

namespace VHSDecode.Core.Dsp;

/// <summary>
/// Immutable float32 half-spectrum snapshots used by the Approx VHS pipeline.
/// </summary>
internal sealed class ApproxRfFilterBank
{
    private readonly Complex32[] _rfVideo;
    private readonly Complex32[] _rfHighPass;
    private readonly Complex32[] _rfMtf;
    private readonly Complex32[] _video;
    private readonly Complex32[] _videoLowPass05;
    private readonly Complex32[] _chromaBurst;

    private ApproxRfFilterBank(
        int realLength,
        Complex32[] rfVideo,
        Complex32[] rfHighPass,
        Complex32[] rfMtf,
        Complex32[] video,
        Complex32[] videoLowPass05,
        Complex32[] chromaBurst)
    {
        RealLength = realLength;
        _rfVideo = rfVideo;
        _rfHighPass = rfHighPass;
        _rfMtf = rfMtf;
        _video = video;
        _videoLowPass05 = videoLowPass05;
        _chromaBurst = chromaBurst;
        RfMtfIsIdentity = IsIdentity(rfMtf);
    }

    internal int RealLength { get; }

    internal ReadOnlySpan<Complex32> RfVideo => _rfVideo;

    internal ReadOnlySpan<Complex32> RfHighPass => _rfHighPass;

    internal ReadOnlySpan<Complex32> RfMtf => _rfMtf;

    internal ReadOnlySpan<Complex32> Video => _video;

    internal ReadOnlySpan<Complex32> VideoLowPass05 => _videoLowPass05;

    internal ReadOnlySpan<Complex32> ChromaBurst => _chromaBurst;

    internal bool RfMtfIsIdentity { get; }

    internal static ApproxRfFilterBank Create(DecodeFilterSet filters, int realLength)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (realLength <= 0 || (realLength & 1) != 0)
        {
            throw new ArgumentException(
                "Approx RF filter snapshots require a positive even real block length.",
                nameof(realLength));
        }

        int spectrumLength = (realLength / 2) + 1;
        return new ApproxRfFilterBank(
            realLength,
            ConvertHalfSpectrum(filters.RfVideo, realLength, spectrumLength, nameof(filters.RfVideo)),
            ConvertHalfSpectrum(filters.RfHighPass, realLength, spectrumLength, nameof(filters.RfHighPass)),
            ConvertHalfSpectrum(filters.RfMtf, realLength, spectrumLength, nameof(filters.RfMtf)),
            ConvertHalfSpectrum(filters.Video, realLength, spectrumLength, nameof(filters.Video)),
            ConvertHalfSpectrum(filters.VideoLowPass05, realLength, spectrumLength, nameof(filters.VideoLowPass05)),
            filters.ChromaBurst is null
                ? []
                : ConvertHalfSpectrum(
                    filters.ChromaBurst,
                    realLength,
                    spectrumLength,
                    nameof(filters.ChromaBurst)));
    }

    internal void ValidateRealLength(int realLength)
    {
        if (realLength != RealLength)
        {
            throw new ArgumentException(
                $"Approx RF filter snapshot length {RealLength} does not match input length {realLength}.",
                nameof(realLength));
        }
    }

    private static Complex32[] ConvertHalfSpectrum(
        ReadOnlySpan<Complex> source,
        int realLength,
        int spectrumLength,
        string parameterName)
    {
        if (source.Length != realLength && source.Length != spectrumLength)
        {
            throw new ArgumentException(
                "Frequency filter length must match the real block or half-spectrum length.",
                parameterName);
        }

        var output = new Complex32[spectrumLength];
        for (int index = 0; index < spectrumLength; index++)
        {
            output[index] = new Complex32(
                (float)source[index].Real,
                (float)source[index].Imaginary);
        }

        return output;
    }

    private static bool IsIdentity(ReadOnlySpan<Complex32> filter)
    {
        foreach (Complex32 value in filter)
        {
            if (value.Real != 1.0f || value.Imaginary != 0.0f)
            {
                return false;
            }
        }

        return true;
    }

}
