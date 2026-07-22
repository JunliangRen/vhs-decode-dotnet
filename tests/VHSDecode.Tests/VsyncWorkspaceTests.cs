using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VsyncWorkspaceTests
{
    [Fact(DisplayName = "VSync workspace reuse remains exact across input lengths")]
    public void WorkspaceReuseRemainsExactAcrossInputLengths()
    {
        double[] longInput = Enumerable.Repeat(100.0, 120_000).ToArray();
        double[] shortInput = Enumerable.Repeat(100.0, 60_000).ToArray();
        var reused = CreateDetector();
        VsyncSerrationResult first = reused.Analyze(longInput);
        _ = reused.Analyze(shortInput);
        VsyncSerrationResult afterResize = reused.Analyze(longInput);
        VsyncSerrationResult fresh = CreateDetector().Analyze(longInput);

        AssertResultEqual(fresh, first);
        AssertResultEqual(fresh, afterResize);
        Assert.Equal(2, reused.RetainedAnalysisWorkspaceCount);
        Assert.True(reused.HasRetainedAnalysisWorkspace(120_000, 121_024));
        Assert.True(reused.HasRetainedAnalysisWorkspace(60_000, 61_024));

        _ = reused.Analyze(Enumerable.Repeat(100.0, 30_000).ToArray());

        Assert.Equal(2, reused.RetainedAnalysisWorkspaceCount);
        Assert.True(reused.HasRetainedAnalysisWorkspace(120_000, 121_024));
        Assert.True(reused.HasRetainedAnalysisWorkspace(30_000, 31_024));
        Assert.False(reused.HasRetainedAnalysisWorkspace(60_000, 61_024));
    }

    private static VsyncSerrationDetector CreateDetector()
        => new(
            sampleRateHz: 4_000_000.0,
            framesPerSecond: 25.0,
            frameLines: 625.0,
            equalizingPulseUs: 2.35,
            divisor: 1);

    private static void AssertResultEqual(VsyncSerrationResult expected, VsyncSerrationResult actual)
    {
        Assert.Equal(expected.FoundSerration, actual.FoundSerration);
        Assert.Equal(expected.HasLevels, actual.HasLevels);
        Assert.Equal(expected.SyncLevel, actual.SyncLevel);
        Assert.Equal(expected.BlankLevel, actual.BlankLevel);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected.SyncLevelBias),
            BitConverter.DoubleToInt64Bits(actual.SyncLevelBias));
        Assert.Equal(expected.Measurements, actual.Measurements);
        Assert.Equal(expected.EnvelopeMinima, actual.EnvelopeMinima);
        Assert.Equal(expected.HarmonicMinima, actual.HarmonicMinima);
        Assert.Equal(expected.Candidates, actual.Candidates);
        Assert.Equal(expected.LevelCountBeforePull, actual.LevelCountBeforePull);
    }
}
