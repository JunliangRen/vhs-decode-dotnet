using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class FallbackVSyncNumericsCompatibilityTests
{
    [Fact(DisplayName = "VHS fallback VSync content check uses NumPy float64 reductions")]
    public void VhsFallbackVSyncContentCheckUsesNumpyFloat64Reductions()
    {
        Pulse[] pulses =
        [
            new(100, 96),
            new(1_300, 96),
            new(2_500, 66),
            new(3_100, 48),
            new(3_700, 48),
            new(4_300, 48)
        ];
        var demodLowPass = new double[5_000];
        Array.Fill(demodLowPass, 16.0, 1_436, 1_024);

        double boundaryValue = BitConverter.Int64BitsToDouble(0x402CF3CF3CF3CF3B);
        Array.Fill(demodLowPass, boundaryValue, 2_606, 454);
        for (int i = 3_188; i < 3_660; i++)
        {
            demodLowPass[i] = ((i - 3_188) & 1) == 0 ? 4.0 : 12.0;
        }

        (double mean, double standardDeviation) =
            NumpyReduction.MeanStandardDeviationFloat64(demodLowPass.AsSpan(2_606, 454));
        Assert.Equal(0x402CF3CF3CF3CF3CUL, BitConverter.DoubleToUInt64Bits(mean));
        Assert.Equal(0x3CE0000000000000UL, BitConverter.DoubleToUInt64Bits(standardDeviation));

        FallbackVSyncResolution? resolution = FallbackVSyncResolver.Resolve(
            validPulses: [],
            rawPulses: pulses,
            demodLowPass,
            vSyncRange: new SyncRange(300.0, 400.0),
            meanLineLength: 1_200.0,
            numEqualizingPulses: 6,
            frameLines: 525);

        Assert.Null(resolution);
    }
}
