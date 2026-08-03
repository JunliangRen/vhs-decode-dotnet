using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsChromaPhaseParallelTests
{
    [Theory(DisplayName = "Parallel current phase prefix matches serial phase analysis")]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelCurrentPhasePrefixMatchesSerialPhaseAnalysis(
        bool detectChromaTrackPhase)
    {
        const int LineOffset = 3;
        const int LinesOut = 80;
        double[] lineLocations = Enumerable.Range(0, LineOffset + LinesOut + 1)
            .Select(static line => (line * 100.0) + ((line % 5) * 0.125))
            .ToArray();

        static ChromaBurstDemodulationResult Probe(
            int lineNumber,
            int phaseRotation,
            double lineScale)
        {
            double phaseDegrees = (phaseRotation * 90.0)
                + (lineNumber >= 70 ? 120.0 : lineNumber * 0.125);
            double phaseRadians = phaseDegrees * Math.PI / 180.0;
            double magnitude = 30_000.0 + lineNumber;
            return new ChromaBurstDemodulationResult(
                phaseDegrees,
                lineScale,
                magnitude,
                Math.Cos(phaseRadians) * magnitude,
                Math.Sin(phaseRadians) * magnitude)
            {
                Start = lineNumber * 100,
                End = (lineNumber + 1) * 100,
                Center = lineNumber + 0.5,
                Amplitude = magnitude / 4.0,
                Dc = lineScale,
                FrequencyHz = 3_579_545.0 + lineNumber
            };
        }

        ChromaPhaseSequenceResult serial = Analyze(workerThreads: 1);
        ChromaPhaseSequenceResult parallel = Analyze(workerThreads: 8);

        AssertPhaseAnalysisEqual(serial, parallel);

        ChromaPhaseSequenceResult Analyze(int workerThreads)
            => VhsChromaDecoder.GetPhaseRotationSequence(
                chromaRotation: [1, 3],
                chromaRotationIndex: 0,
                lineLocations,
                LineOffset,
                LinesOut,
                inputLineLength: 100,
                Probe,
                detectChromaTrackPhase,
                rotationCheckStartLine: LineOffset + LinesOut - 16,
                enableColorKiller: true,
                prevBurstDetectedLine: 0,
                colorSystem: "NTSC",
                workerThreads);
    }

    [Fact(DisplayName = "Parallel current phase prefix preserves probe exceptions")]
    public void ParallelCurrentPhasePrefixPreservesProbeExceptions()
    {
        double[] lineLocations = Enumerable.Range(0, 65)
            .Select(static line => line * 100.0)
            .ToArray();
        int[] probeCounts = new int[64];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => VhsChromaDecoder.GetPhaseRotationSequence(
                chromaRotation: [1, 3],
                chromaRotationIndex: 0,
                lineLocations,
                lineOffset: 0,
                linesOut: 64,
                inputLineLength: 100,
                burstProbe: (lineNumber, _, _) => Probe(lineNumber),
                detectChromaTrackPhase: false,
                rotationCheckStartLine: 48,
                enableColorKiller: false,
                prevBurstDetectedLine: 0,
                colorSystem: "NTSC",
                workerThreads: 8));

        Assert.Equal("first pinned probe failure", exception.Message);
        Assert.All(probeCounts, static count => Assert.Equal(1, count));

        ChromaBurstDemodulationResult Probe(int lineNumber)
        {
            Interlocked.Increment(ref probeCounts[lineNumber]);
            if (lineNumber == 7)
            {
                Thread.Sleep(10);
                throw new InvalidOperationException("first pinned probe failure");
            }

            if (lineNumber == 20)
            {
                throw new InvalidOperationException("later pinned probe failure");
            }

            return new ChromaBurstDemodulationResult(
                        0.0,
                        0.0,
                        30_000.0,
                        30_000.0,
                        0.0);
        }
    }

    [Theory(DisplayName = "Parallel current phase prefix matches serial after track-phase flip")]
    [InlineData("NTSC")]
    [InlineData("PAL")]
    public void ParallelCurrentPhasePrefixMatchesSerialAfterTrackPhaseFlip(string colorSystem)
    {
        const int LinesOut = 80;
        double[] lineLocations = Enumerable.Range(0, LinesOut + 1)
            .Select(static line => line * 100.0)
            .ToArray();

        ChromaPhaseSequenceResult serial = Analyze(workerThreads: 1);
        ChromaPhaseSequenceResult parallel = Analyze(workerThreads: 8);

        AssertPhaseAnalysisEqual(serial, parallel);

        ChromaPhaseSequenceResult Analyze(int workerThreads)
            => VhsChromaDecoder.GetPhaseRotationSequence(
                chromaRotation: [1, 3],
                chromaRotationIndex: 0,
                lineLocations,
                lineOffset: 0,
                LinesOut,
                inputLineLength: 100,
                burstProbe: (lineNumber, phaseRotation, _) =>
                    new ChromaBurstDemodulationResult(
                        colorSystem == "NTSC" ? lineNumber * 180.0 : 0.0,
                        phaseRotation * 90.0,
                        30_000.0,
                        30_000.0,
                        0.0),
                detectChromaTrackPhase: false,
                rotationCheckStartLine: LinesOut - 16,
                enableColorKiller: false,
                prevBurstDetectedLine: 0,
                colorSystem,
                workerThreads);
    }

    private static void AssertPhaseAnalysisEqual(
        ChromaPhaseSequenceResult expected,
        ChromaPhaseSequenceResult actual)
    {
        Assert.Equal(expected.NextChromaRotationIndex, actual.NextChromaRotationIndex);
        Assert.Equal(expected.BurstDetectedLine, actual.BurstDetectedLine);
        Assert.Equal(expected.BurstMagnitudeAverage, actual.BurstMagnitudeAverage);
        Assert.Equal(expected.BurstPhaseAverageDegrees, actual.BurstPhaseAverageDegrees);
        Assert.Equal(expected.EvenBurstPhaseAverageDegrees, actual.EvenBurstPhaseAverageDegrees);
        Assert.Equal(expected.OddBurstPhaseAverageDegrees, actual.OddBurstPhaseAverageDegrees);
        Assert.Equal(expected.PhaseSequence.Length, actual.PhaseSequence.Length);
        for (int index = 0; index < expected.PhaseSequence.Length; index++)
        {
            Assert.Equal(expected.PhaseSequence[index], actual.PhaseSequence[index]);
        }
    }
}
