using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaCurrentProcessingIntegrationTests
{
    [Fact(DisplayName = "Current chroma field routes fitted burst data through gain and CTI")]
    public void CurrentChromaFieldRoutesFittedBurstDataThroughGainAndCti()
    {
        const int LineLength = 64;
        const int LineCount = 48;
        const int LineOffset = 1;
        double[] chroma = BuildInput(LineLength * LineCount);
        double[] lineLocations = Enumerable.Range(0, LineOffset + LineCount + 1)
            .Select(static line => line * (double)LineLength)
            .ToArray();
        VhsChromaFieldOptions options = CreateOptions(ctiMix: 1.0);

        ChromaPhaseSequenceResult phase = VhsChromaDecoder.AnalyzeFieldPhase(
            chroma,
            options,
            lineLocations,
            inputLineLength: LineLength,
            lineOffset: LineOffset,
            burstFilter: static samples => samples);

        Assert.Equal(LineCount, phase.PhaseSequence.Length);
        for (int index = 0; index < phase.PhaseSequence.Length; index++)
        {
            ChromaPhaseLine line = phase.PhaseSequence[index];
            Assert.Equal(index * LineLength, line.BurstStart);
            Assert.True(double.IsFinite(line.BurstAmplitude));
            Assert.True(double.IsFinite(line.BurstDc));
            Assert.True(double.IsFinite(line.BurstFrequencyHz));
            Assert.NotEqual(0.0, line.BurstFrequencyHz);
        }

        VhsChromaFieldResult result = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0,
            lineOffset: LineOffset);
        VhsChromaFieldResult repeated = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options,
            phase,
            isFirstField: true,
            fieldNumber: 0,
            lineOffset: LineOffset);
        VhsChromaFieldResult ctiDisabled = VhsChromaDecoder.DecodeFieldWithPhase(
            chroma,
            options with { CtiMix = 0.0 },
            phase,
            isFirstField: true,
            fieldNumber: 0,
            lineOffset: LineOffset);

        Assert.Equal(result.Samples, repeated.Samples);
        Assert.Equal(
            "42367AD445A47D4DFFE06144DAFF3386051B448A1D94648B2E53F3264BD71517",
            Sha256(result.Samples));
        Assert.Equal(
            "9A735F22A8BB44A6723B8D9736596203AA1714B9007B5C3EA51EEFD0801A0E29",
            Sha256(ctiDisabled.Samples));
    }

    [Fact(DisplayName = "Legacy chroma field does not route current processing")]
    public void LegacyChromaFieldDoesNotRouteCurrentProcessing()
    {
        VhsChromaFieldOptions current = CreateOptions(ctiMix: 1.0);
        VhsChromaFieldOptions legacy = current with
        {
            UseCurrentChromaProcessing = false
        };

        Assert.True(current.UseCurrentChromaProcessing);
        Assert.False(legacy.UseCurrentChromaProcessing);
    }

    [Fact(DisplayName = "Current NTSC burst deemphasis uses the PR 341 boundary")]
    public void CurrentNtscBurstDeemphasisUsesPr341Boundary()
    {
        double[] samples = Enumerable.Repeat(1.0, 16).ToArray();

        double[] legacy = VhsChromaDecoder.ApplyBurstDeemphasis(
            samples,
            lineOffset: 1,
            linesOut: 2,
            lineLength: 8,
            burstStart: 1,
            burstEnd: 2);
        double[] current = VhsChromaDecoder.ApplyBurstDeemphasis(
            samples,
            lineOffset: 1,
            linesOut: 2,
            lineLength: 8,
            burstStart: 1,
            burstEnd: 2,
            samplesAfterBurst: 4);

        Assert.Equal(1.0, legacy[6]);
        Assert.Equal(2.0, current[6]);
        Assert.Equal(2.0, legacy[7]);
        Assert.Equal(2.0, current[7]);
    }

    private static VhsChromaFieldOptions CreateOptions(double ctiMix)
        => new(
            ColorSystem: "NTSC",
            OutputLineLength: 64,
            OutputLineCount: 48,
            OutputSampleRateHz: 4_000_000.0,
            FscMHz: 1.0,
            ColorUnderCarrierHz: 250_000.0,
            BurstStart: 8,
            BurstEnd: 24,
            BurstAbsRef: 72.0,
            ChromaRotation: [-1, 1],
            DisableComb: false,
            DisablePhaseCorrection: false,
            EnableColorKiller: false,
            DetectChromaTrackPhase: false)
        {
            UseCurrentChromaProcessing = true,
            SyncTipLength = 20,
            CtiMix = ctiMix,
            CtiWidth = 2
        };

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

    private static string Sha256(ushort[] samples)
        => Convert.ToHexString(
            SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan())));
}
