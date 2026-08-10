using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CurrentChromaBurstFitterTests
{
    [Fact(DisplayName = "Vectorized current burst dot matches the scalar OpenBLAS reduction")]
    public void VectorizedCurrentBurstDotMatchesScalarOpenBlasReduction()
    {
        if (!Avx.IsSupported || !Fma.IsSupported)
        {
            return;
        }

        int[] lengths =
        [
            0, 1, 2, 3, 4, 7, 8, 15, 16, 17, 31, 32, 33, 39, 40, 41,
            63, 64, 65, 127, 128, 129, 255, 256, 257
        ];
        var random = new Random(341);
        foreach (int length in lengths)
        {
            for (int iteration = 0; iteration < 64; iteration++)
            {
                var left = new double[length];
                var right = new double[length];
                for (int index = 0; index < length; index++)
                {
                    left[index] = (random.NextDouble() - 0.5) * 1e6;
                    right[index] = (random.NextDouble() - 0.5) * 1e-3;
                }

                double expected = CurrentChromaBurstFitter.OpenBlasHaswellDotScalar(
                    left,
                    right);
                double actual = CurrentChromaBurstFitter.OpenBlasHaswellDot(left, right);
                Assert.Equal(
                    BitConverter.DoubleToUInt64Bits(expected),
                    BitConverter.DoubleToUInt64Bits(actual));
            }
        }
    }

    [Fact(DisplayName = "Vectorized current burst dot preserves exceptional IEEE values")]
    public void VectorizedCurrentBurstDotPreservesExceptionalIeeeValues()
    {
        if (!Avx.IsSupported || !Fma.IsSupported)
        {
            return;
        }

        ulong[] exceptionalBits =
        [
            0x0000000000000000UL,
            0x8000000000000000UL,
            0x0000000000000001UL,
            0x8000000000000001UL,
            0x0010000000000000UL,
            0x8010000000000000UL,
            0x7FEFFFFFFFFFFFFFUL,
            0xFFEFFFFFFFFFFFFFUL,
            0x7FF0000000000000UL,
            0xFFF0000000000000UL,
            0x7FF8000000000341UL,
            0xFFF8000000000341UL
        ];
        for (int length = 1; length <= 64; length++)
        {
            var left = new double[length];
            var right = new double[length];
            for (int index = 0; index < length; index++)
            {
                left[index] = BitConverter.UInt64BitsToDouble(
                    exceptionalBits[index % exceptionalBits.Length]);
                right[index] = BitConverter.UInt64BitsToDouble(
                    exceptionalBits[(index * 5 + 3) % exceptionalBits.Length]);
            }

            double expected = CurrentChromaBurstFitter.OpenBlasHaswellDotScalar(
                left,
                right);
            double actual = CurrentChromaBurstFitter.OpenBlasHaswellDot(left, right);
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(expected),
                BitConverter.DoubleToUInt64Bits(actual));
        }
    }

    [Fact(DisplayName = "Current chroma burst fitting matches pinned PR 341 and workspace-boundary output")]
    public void CurrentChromaBurstFittingMatchesPinnedOutputs()
    {
        const int BurstStart = 73;
        const double FscHz = 3_579_545.0;
        uint[] inputBits =
        [
            0x426C25FD, 0xC2650C6F, 0xC24B43ED, 0x4283B723,
            0x426C61DE, 0xC264D01D, 0xC24AFFCE, 0x4283D8FA,
            0x426C9DBF, 0xC26493CA, 0xC24ABBAF, 0x42834AD1,
            0x426CD9A0, 0xC265B778, 0xC24A7790, 0x42836CA7,
            0x426D1581, 0xC2657B26, 0xC24A3371, 0x42838E7E,
            0x426D5162, 0xC2653ED3, 0xC24B4F52, 0x4283B055,
            0x426C2D42, 0xC2650280, 0xC24B0B33, 0x4283D22B,
            0x426C6923, 0xC264C62E, 0xC24AC714, 0x4283F402,
            0x426CA504, 0xC265E9DB, 0xC24A82F4, 0x428365D9,
            0x426CE0E5, 0xC265AD88, 0xC24A3ED5, 0x428387AF
        ];
        double[] burst = inputBits
            .Select(static bits => (double)BitConverter.UInt32BitsToSingle(bits))
            .ToArray();
        (double[] sine, double[] cosine) = VhsChromaDecoder.BuildCarrierTables(
            sampleCount: 256,
            carrierMHz: FscHz / 1_000_000.0,
            outputSampleRateMHz: (FscHz * 4.0) / 1_000_000.0);

        CurrentChromaBurstFit fit = CurrentChromaBurstFitter.Fit(
            burst,
            BurstStart,
            sine,
            cosine,
            FscHz);

        Assert.Equal(0x40933CB58A527D18UL, BitConverter.DoubleToUInt64Bits(fit.I));
        Assert.Equal(0x40912A0F2BA8A118UL, BitConverter.DoubleToUInt64Bits(fit.Q));
        Assert.Equal(0x40573DAEB19A76D0UL, BitConverter.DoubleToUInt64Bits(fit.Center));
        Assert.Equal(0x4054A008E5044C38UL, BitConverter.DoubleToUInt64Bits(fit.Amplitude));
        Assert.Equal(0x4099C80B1E455F46UL, BitConverter.DoubleToUInt64Bits(fit.Magnitude));
        Assert.Equal(0x4010FA93333330EFUL, BitConverter.DoubleToUInt64Bits(fit.Dc));
        Assert.Equal(0x414B4F4C80000029UL, BitConverter.DoubleToUInt64Bits(fit.FrequencyHz));
        Assert.Equal(0x3FE75000083EFFC8UL, BitConverter.DoubleToUInt64Bits(fit.PhaseRadians));
        Assert.Equal(0x4044DED4E09B8A2DUL, BitConverter.DoubleToUInt64Bits(fit.PhaseDegrees));

        (double[] boundarySine, double[] boundaryCosine) =
            VhsChromaDecoder.BuildCarrierTables(
                sampleCount: 512,
                carrierMHz: FscHz / 1_000_000.0,
                outputSampleRateMHz: (FscHz * 4.0) / 1_000_000.0);
        AssertBoundaryFit(
            255,
            boundarySine,
            boundaryCosine,
            "40ACC1F47D39F4B2;40B0C0DE5B977319;4069114B395F1AEE;4046523B25368AC5;40B63BE8EA11543A;40505FEA6735A150;414B4F4C7FFFFDF9;3FEB2A447216DD04;404851C8ADBDDEAE");
        AssertBoundaryFit(
            256,
            boundarySine,
            boundaryCosine,
            "40AD7FD47D39F4B2;40B0C0DE440EC559;4069214B4411010C;40465230D41B5478;40B65230D41B5478;40505FE6800000F8;414B4F4C7FFFFDE1;3FEB2A553EC5561C;404851D7B7E9794E");
        AssertBoundaryFit(
            257,
            boundarySine,
            boundaryCosine,
            "40AD7FD4747C9B92;40B12418440EC559;4069314B65D578DC;4046525ADAD9E99C;40B668AD35B4C386;40505FF47F817C23;414B4F4C7FFFFD92;3FEB2A8A49782D8C;404852073431F542");
    }

    private static void AssertBoundaryFit(
        int length,
        ReadOnlySpan<double> sine,
        ReadOnlySpan<double> cosine,
        string expectedBits)
    {
        const int BurstStart = 73;
        const double FscHz = 3_579_545.0;
        int[] carrier = [35, -28, -32, 31];
        var burst = new double[length];
        for (int index = 0; index < burst.Length; index++)
        {
            int dither = ((index * 37) % 101) - 50;
            burst[index] = 64.0 + carrier[index & 3] + (dither / 128.0);
        }

        CurrentChromaBurstFit fit = CurrentChromaBurstFitter.Fit(
            burst,
            BurstStart,
            sine,
            cosine,
            FscHz);
        ulong[] bits =
        [
            BitConverter.DoubleToUInt64Bits(fit.I),
            BitConverter.DoubleToUInt64Bits(fit.Q),
            BitConverter.DoubleToUInt64Bits(fit.Center),
            BitConverter.DoubleToUInt64Bits(fit.Amplitude),
            BitConverter.DoubleToUInt64Bits(fit.Magnitude),
            BitConverter.DoubleToUInt64Bits(fit.Dc),
            BitConverter.DoubleToUInt64Bits(fit.FrequencyHz),
            BitConverter.DoubleToUInt64Bits(fit.PhaseRadians),
            BitConverter.DoubleToUInt64Bits(fit.PhaseDegrees)
        ];
        Assert.Equal(
            expectedBits,
            string.Join(';', bits.Select(static value => value.ToString("X16"))));
    }
}
