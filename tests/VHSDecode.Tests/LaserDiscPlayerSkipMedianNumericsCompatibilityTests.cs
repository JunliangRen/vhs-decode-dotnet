using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscPlayerSkipMedianNumericsCompatibilityTests
{
    [Fact(DisplayName = "LD player-skip scoring uses Numba float32 line medians")]
    public void LaserDiscPlayerSkipScoringUsesNumbaFloat32LineMedians()
    {
        const int lineStride = 2_544;
        const int lineCount = 8;
        const double medianCenter = 7_592_572.0;
        var video = new double[lineStride * lineCount];
        for (int line = 0; line < lineCount; line++)
        {
            int start = line * lineStride;
            Array.Fill(video, medianCenter, start, lineStride / 2);
            Array.Fill(video, medianCenter + 0.5, start + (lineStride / 2), lineStride / 2);
        }

        double[] lineLocations = Enumerable.Range(0, lineCount)
            .Select(line => line * (double)lineStride)
            .ToArray();
        var converter = new VideoOutputConverter(
            ire0: medianCenter - 60_000.0,
            hzIre: 12_000.0,
            outputZero: 1_024,
            vsyncIre: -40.0,
            outputScale: 358.4);

        int score = LaserDiscPlayerSkipDetector.ScorePreviousFieldEnd(
            video,
            lineLocations,
            outputLineCount: 0,
            lineOffset: 0,
            nominalLineLength: lineStride - 1.0,
            converter);

        Assert.Equal(25, score);
    }
}
