using System.Numerics;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscV4300DCompatibilityTests
{
    [Fact(DisplayName = "PAL LD V4300D default-block spur threshold uses v0.4.0 magnitudes")]
    public void PalLaserDiscV4300DSpurThresholdUsesRelease40Magnitudes()
    {
        const int length = 32_768;
        const double sampleRateHz = 40_000_000.0;
        int start = (int)(length * (8_420_000.0 / sampleRateHz));
        int end = (int)(1.0 + (length * (8_600_000.0 / sampleRateHz)));
        Assert.Equal(149, end - start);

        var spectrum = new Complex[length];
        ulong state = 1;
        for (int bin = start; bin < end; bin++)
        {
            state = unchecked((state * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL);
            double unit = (state >> 11) * (1.0 / 9_007_199_254_740_992.0);
            double magnitude = 0.1 + (5.0 * unit * unit * unit);
            spectrum[bin] = new Complex(magnitude, 0.0);
            spectrum[length - bin] = new Complex(magnitude, 0.0);
        }

        Complex[] cleaned = RfDemodulator.RemoveLdPalV4300DSpur(spectrum, sampleRateHz);

        Assert.Equal(spectrum, cleaned);
    }
}
