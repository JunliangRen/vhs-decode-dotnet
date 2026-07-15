using VHSDecode.Core.Dsp;

namespace VHSDecode.Core.HiFi;

public sealed class HiFiAfeFilter
{
    private const int FilterOrder = 22;
    private const double StopAttenuationDb = 220.0;
    private readonly SosSection[] _sections;

    public HiFiAfeFilter(
        int sampleRateHz,
        double carrierHz,
        double bandPassHalfWidthHz)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        if (!double.IsFinite(carrierHz) || carrierHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(carrierHz));
        }

        if (!double.IsFinite(bandPassHalfWidthHz) || bandPassHalfWidthHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(bandPassHalfWidthHz));
        }

        SampleRateHz = sampleRateHz;
        CarrierHz = carrierHz;
        BandPassHalfWidthHz = bandPassHalfWidthHz;
        _sections = IirFilterDesign.ChebyshevTypeIIBandPassSos(
            FilterOrder,
            StopAttenuationDb,
            carrierHz - bandPassHalfWidthHz,
            carrierHz + bandPassHalfWidthHz,
            sampleRateHz);
    }

    public int SampleRateHz { get; }
    public double CarrierHz { get; }
    public double BandPassHalfWidthHz { get; }
    public ReadOnlyMemory<SosSection> Sections => _sections;

    public float[] Apply(ReadOnlySpan<float> input)
        => SosFilter.ApplyForwardBackwardFloat32(_sections, input);

    public static (HiFiAfeFilter Left, HiFiAfeFilter Right) FromPlan(HiFiDecodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return (
            new HiFiAfeFilter(
                plan.IfRateHz,
                plan.Afe.LeftCarrierHz,
                plan.Afe.LeftNotchWidthHz),
            new HiFiAfeFilter(
                plan.IfRateHz,
                plan.Afe.RightCarrierHz,
                plan.Afe.RightNotchWidthHz));
    }
}
