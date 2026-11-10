using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaCurrentHeadSwitchTests
{
    [Fact]
    public void CurrentPalFitsTheFollowingTrackAcrossTheHeadSwitch()
    {
        const int lineCount = 80;
        const int width = 64;
        const int switchLine = 70;
        const double offsetDegrees = 16.0;
        double[] locations = Enumerable.Range(0, lineCount + 1)
            .Select(line => (double)(line * width))
            .ToArray();
        var result = VhsChromaDecoder.GetPhaseRotationSequence(
            chromaRotation: [1, -1],
            chromaRotationIndex: 0,
            lineLocations: locations,
            lineOffset: 0,
            linesOut: lineCount,
            inputLineLength: width,
            burstProbe: Probe,
            detectChromaTrackPhase: true,
            rotationCheckStartLine: 64,
            enableColorKiller: false,
            prevBurstDetectedLine: 0,
            colorSystem: "PAL",
            workerThreads: 1,
            fitPalHeadSwitch: true);

        Assert.Equal(1, result.NextChromaRotationIndex);
        for (int line = 0; line < lineCount; line++)
        {
            ChromaPhaseLine burst = result.PhaseSequence[line];
            Assert.Equal(ExpectedRotation(line), burst.PhaseRotation);
            Assert.Equal(line < switchLine ? 0.0 : offsetDegrees * Math.PI / 180.0, burst.PhaseCorrectionRadians, 12);
        }

        static int ExpectedRotation(int line)
            => line < switchLine ? (line + 1) & 3 : (switchLine - line) & 3;

        static ChromaBurstDemodulationResult Probe(int line, int phase, double _)
        {
            double expected = ((line & 1) == 0 ? 43.0 : -43.0) + (line * 0.125);
            double measured = expected + ((ExpectedRotation(line) - phase) * 90.0)
                + (line < switchLine ? 0.0 : offsetDegrees);
            measured -= 360.0 * Math.Floor((measured + 180.0) / 360.0);
            return new ChromaBurstDemodulationResult(measured, 0.0, 100_000.0, 0.0, 0.0);
        }
    }

    [Fact]
    public void CurrentUpconversionSkipsHiddenLinesAndUsesTheQuadratureCorrection()
    {
        double[] samples = [1, 1, 1, 1, 1, 1, 1, 1];
        ChromaPhaseLine[] phaseSequence =
        [
            new(0, 0),
            new(2, 0) { PhaseCorrectionRadians = Math.PI / 6.0 },
            new(3, 1)
        ];
        double[][] heterodyne = [Constant(2), Constant(3), Constant(4), Constant(5)];
        double corrected = (float)((2.0 * Math.Cos(Math.PI / 6.0)) + (3.0 * Math.Sin(Math.PI / 6.0)));
        double[] expected = [corrected, corrected, 3.0, 3.0, 0.0, 0.0, 0.0, 0.0];

        Assert.Equal(expected, VhsChromaDecoder.UpconvertChromaCore(samples, 2, 2, phaseSequence, heterodyne, true));
        Assert.True(VhsChromaDecoder.TryUpconvertChromaInPlace(samples, 2, 2, phaseSequence, heterodyne, true));
        Assert.Equal(expected, samples);

        static double[] Constant(double value) => Enumerable.Repeat(value, 8).ToArray();
    }

    [Fact]
    public void CurrentReferenceWaveRoundsAfterDoublePrecisionTrigonometry()
    {
        const int index = 199_999;
        (_, double[] currentCosine) = VhsChromaDecoder.BuildCarrierTables(index + 1, 1.0, 4.0, useDoubleAngles: true);
        (_, double[] legacyCosine) = VhsChromaDecoder.BuildCarrierTables(index + 1, 1.0, 4.0);
        double expected = (float)Math.Cos((Math.Tau / 4.0) * index);
        Assert.Equal(expected, currentCosine[index]);
        Assert.NotEqual(expected, legacyCosine[index]);
    }
}
