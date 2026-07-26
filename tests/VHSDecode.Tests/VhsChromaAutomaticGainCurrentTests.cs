using System.Buffers.Binary;
using System.Security.Cryptography;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaAutomaticGainCurrentTests
{
    private const string InputHash =
        "72C48D82DC902269AAEC31392A59BCDA7E426BA5EF0E8C4B9190CEFD72D3D46A";
    private const string GainedHash =
        "6B820ADA3A312090353A58C9C5F72D9B48FB290894756CAA0AD76FF2A0E16D5E";

    [Fact(DisplayName = "Current chroma ACC matches pinned PR 341 Numba output")]
    public void CurrentChromaAccMatchesPinnedNumbaOutput()
    {
        const int LineCount = 24;
        const int LineLength = 64;
        const int LineOffset = 3;
        double[] samples = BuildInput(LineCount * LineLength);
        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            LineOffset);
        Assert.Equal(InputHash, Float32BitsSha256(samples));

        CurrentAutomaticChromaGainResult result =
            VhsChromaDecoder.ApplyCurrentAutomaticChromaGainInPlace(
                samples,
                burstAbsRef: 72.0,
                phaseSequence,
                burstDetectedLine: 8,
                syncTipLength: 20);

        Assert.Equal(GainedHash, Float32BitsSha256(samples));
        ulong[] expectedMads =
        [
            0x405C8402D8000000UL,
            0x406709FFD8000000UL,
            0x405CB5D098000000UL,
            0x4059CD1D48000000UL,
            0x40612AEE80000000UL,
            0x40626727D0000000UL,
            0x4059F15C54000000UL,
            0x4059F96D50000000UL,
            0x40628CFCA0000000UL,
            0x40629B2288000000UL,
            0x405A392550000000UL,
            0x405A4BD280000000UL,
            0x4065FA2900000000UL,
            0x405CDD4A62000000UL,
            0x405A821588000000UL,
            0x405CC42E70000000UL,
            0x4066D1355C000000UL,
            0x405C8DE6F0000000UL,
            0x405A3BB3F0000000UL
        ];
        Assert.Equal(
            0xC03ED8B020000000UL,
            BitConverter.DoubleToUInt64Bits(
                CalculateMedian(samples.AsSpan((9 * LineLength) - 16, 12))));
        for (int index = 0; index < expectedMads.Length; index++)
        {
            int syncStart = ((index + 6) * LineLength) - 16;
            Assert.Equal(
                expectedMads[index],
                BitConverter.DoubleToUInt64Bits(
                    CalculateSyncTipMad(samples.AsSpan(syncStart, 12))));
        }

        Assert.Equal(
            unchecked((long)0x4054A1AF286BCA1BUL),
            BitConverter.DoubleToInt64Bits(result.MeanBurstAmplitude));
        Assert.Equal(
            unchecked((long)0x4067B3DA611B790BUL),
            BitConverter.DoubleToInt64Bits(result.NoiseFloor));
    }

    private static double CalculateSyncTipMad(ReadOnlySpan<double> samples)
    {
        double median = CalculateMedian(samples);
        var values = samples.ToArray();
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = Math.Abs((float)values[index] - median);
        }

        return NumpyReduction.MedianFloat64(values);
    }

    private static double CalculateMedian(ReadOnlySpan<double> samples)
    {
        var values = new double[samples.Length];
        for (int index = 0; index < samples.Length; index++)
        {
            values[index] = (float)samples[index];
        }

        return NumbaReduction.MedianFloat32(values);
    }

    private static ChromaPhaseLine[] BuildPhaseSequence(
        int lines,
        int lineLength,
        int lineOffset)
    {
        const int BurstLength = 40;
        var phaseSequence = new ChromaPhaseLine[lines];
        for (int index = 0; index < lines; index++)
        {
            int lineNumber = lineOffset + index;
            int start = index * lineLength;
            double amplitude =
                82.0 + ((((index * 13) % 17) - 8) * 1.25);
            double magnitude = amplitude * (BurstLength / 2.0);
            phaseSequence[index] = new ChromaPhaseLine(
                lineNumber,
                PhaseRotation: 0,
                BurstPhaseDegrees: (index * 91) % 360,
                BurstMagnitude: magnitude,
                I: magnitude,
                Q: -magnitude / 3.0)
            {
                BurstStart = start,
                BurstEnd = start + BurstLength,
                BurstCenter = start + ((BurstLength - 1) / 2),
                BurstAmplitude = amplitude,
                BurstFrequencyHz = 3_579_545.0
            };
        }

        return phaseSequence;
    }

    private static double[] BuildInput(int length)
    {
        var samples = new double[length];
        for (int index = 0; index < samples.Length; index++)
        {
            int integer = ((index * 7_919 + 104_729) % 65_521) - 32_760;
            samples[index] = (float)integer * 0.01f;
        }

        return samples;
    }

    private static string Float32BitsSha256(ReadOnlySpan<double> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(int))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(int), sizeof(int)),
                BitConverter.SingleToInt32Bits((float)values[index]));
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
