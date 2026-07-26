using System.Buffers.Binary;
using System.Security.Cryptography;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaPhaseCompensationCurrentTests
{
    private const string ExpectedHash =
        "A006345D72FE328078FF3CA925FB1CB12DD9481A259B78C62113E5073A050F5B";

    [Fact(DisplayName = "Current phase-compensated upconversion matches pinned PR 341 Numba output")]
    public void CurrentPhaseCompensatedUpconversionMatchesPinnedNumbaOutput()
    {
        const int LineCount = 8;
        const int LineLength = 64;
        const int LineOffset = 3;
        const double FscHz = 3_579_545.0;
        var samples = new double[LineCount * LineLength];
        for (int index = 0; index < samples.Length; index++)
        {
            int integer = ((index * 7_919 + 104_729) % 65_521) - 32_760;
            samples[index] = (float)integer * 0.01f;
        }

        ChromaPhaseLine[] phaseSequence = BuildPhaseSequence(
            LineCount,
            LineLength,
            LineOffset,
            FscHz);

        VhsChromaDecoder.UpconvertChromaPhaseCompensatedCurrentInPlace(
            samples,
            LineOffset,
            LineLength,
            phaseSequence,
            colorUnderCarrierHz: 629_921.0,
            FscHz,
            targetPhaseEvenDegrees: -33.0,
            targetPhaseOddDegrees: -33.0);

        Assert.Equal(ExpectedHash, Float32BitsSha256(samples));
    }

    private static ChromaPhaseLine[] BuildPhaseSequence(
        int lines,
        int lineLength,
        int lineOffset,
        double fscHz)
    {
        const int BurstLength = 40;
        var phaseSequence = new ChromaPhaseLine[lines];
        for (int index = 0; index < lines; index++)
        {
            int start = index * lineLength;
            double amplitude =
                82.0 + ((((index * 13) % 17) - 8) * 1.25);
            double magnitude = amplitude * (BurstLength / 2.0);
            phaseSequence[index] = new ChromaPhaseLine(
                LineNumber: lineOffset + index,
                PhaseRotation: (index * 3) % 4,
                BurstPhaseDegrees: (index * 91) % 360,
                BurstMagnitude: magnitude,
                I: magnitude,
                Q: -magnitude / 3.0)
            {
                BurstStart = start,
                BurstEnd = start + BurstLength,
                BurstCenter = start + ((BurstLength - 1) / 2),
                BurstAmplitude = amplitude,
                BurstFrequencyHz =
                    fscHz + ((((index * 19) % 11) - 5) * 17.25),
                BurstDc = (((index * 7) % 9) - 4) * 0.125
            };
        }

        return phaseSequence;
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
