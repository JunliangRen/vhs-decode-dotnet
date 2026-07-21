using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PulseDetectionReuseTests
{
    [Fact(DisplayName = "Reusable pulse detection matches the v0.4.0 scalar state machine")]
    public void ReusablePulseDetectionMatchesScalarStateMachine()
    {
        double[] specialValues =
        [
            double.NaN,
            double.NegativeInfinity,
            -1.0,
            0.0,
            1.0,
            double.PositiveInfinity
        ];
        var random = new Random(0x50414C);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            int length = random.Next(0, 4097);
            var data = new double[length];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = random.Next(20) == 0
                    ? specialValues[random.Next(specialValues.Length)]
                    : random.NextDouble() * 20.0 - 10.0;
            }

            double high = iteration % 17 == 0
                ? specialValues[iteration % specialValues.Length]
                : random.NextDouble() * 12.0 - 6.0;
            int minimumLength = random.Next(0, 12);
            int maximumLength = random.Next(minimumLength, 32);

            Pulse[] expected = FindPulsesScalar(
                data,
                high,
                minimumLength,
                maximumLength);
            IReadOnlyList<Pulse> actual = PulseDetection.FindPulses(
                data,
                high,
                minimumLength,
                maximumLength);
            var scaled = new List<Pulse> { new(99, 99) };
            PulseDetection.FindPulses(
                data,
                high,
                minimumLength,
                maximumLength,
                scaled,
                positionScale: 7);

            Assert.Equal(expected, actual);
            Assert.Equal(
                expected.Select(pulse => new Pulse(pulse.Start * 7, pulse.Length * 7)),
                scaled);
        }
    }

    [Fact(DisplayName = "Reusable pulse storage clears and scales positions exactly")]
    public void ReusablePulseStorageClearsAndScalesPositions()
    {
        var pulses = new List<Pulse> { new(99, 99) };
        PulseDetection.FindPulses(
            [5.0, 1.0, 1.0, 5.0, 1.0, 1.0, 1.0, 5.0],
            high: 2.0,
            minimumSyncLength: 2,
            maximumSyncLength: 3,
            pulses,
            positionScale: 3);

        Assert.Equal([new Pulse(3, 6), new Pulse(12, 9)], pulses);

        PulseDetection.FindPulses(
            [],
            high: 2.0,
            minimumSyncLength: 0,
            maximumSyncLength: 10,
            pulses,
            positionScale: 1);
        Assert.Empty(pulses);
        Assert.Throws<ArgumentOutOfRangeException>(() => PulseDetection.FindPulses(
            [1.0],
            high: 2.0,
            minimumSyncLength: 0,
            maximumSyncLength: 10,
            pulses,
            positionScale: 0));
    }

    [Fact(DisplayName = "Fallback serration search preserves every supported divisor")]
    public void FallbackSerrationSearchPreservesEverySupportedDivisor()
    {
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 40_000_000.0,
            linePeriodUs: 64.0,
            hsyncPulseUs: 4.7,
            equalizingPulseUs: 2.35,
            vsyncPulseUs: 27.3);
        const int halfLine = 1280;
        int equalizingLength = (int)analyzer.UsecToSamples(analyzer.EqualizingPulseUs);
        int vsyncLength = (int)analyzer.UsecToSamples(analyzer.VSyncPulseUs);
        double[] field = Enumerable.Repeat(100.0, 322 * 2560).ToArray();
        for (int line = 0; line < 240; line++)
        {
            Array.Fill(field, 60.0, 100 + (line * 2560), 188);
        }

        int vbiStart = 250 * 2560;
        for (int pulse = 0; pulse < 18; pulse++)
        {
            int length = pulse is >= 6 and < 12 ? vsyncLength : equalizingLength;
            Array.Fill(field, 60.0, vbiStart + (pulse * halfLine), length);
        }

        for (int divisor = 1; divisor <= 10; divisor++)
        {
            SerrationLevelRefinement refinement = LevelDetection.SearchFallbackSerrationLevels(
                field,
                analyzer,
                divisor,
                blankLevel: 100.0,
                referenceSyncLevel: 60.0,
                hzIre: 1.0,
                checkLongPulses: false,
                out SerrationLevelFailureKind failureKind)
                ?? throw new InvalidOperationException($"Divisor {divisor} did not find VBI levels.");

            Assert.Equal(SerrationLevelFailureKind.None, failureKind);
            Assert.Equal(60.0, refinement.SyncLevel);
            Assert.Equal(100.0, refinement.BlankLevel);
            Assert.Equal(6, refinement.VsyncPulseCount);
            Assert.True(refinement.PulseCount > 200);
        }
    }

    private static Pulse[] FindPulsesScalar(
        ReadOnlySpan<double> syncReference,
        double high,
        int minimumSyncLength,
        int maximumSyncLength)
    {
        if (syncReference.IsEmpty)
        {
            return [];
        }

        bool inPulse = syncReference[0] <= high;
        int currentStart = 0;
        var pulses = new List<Pulse>();
        for (int position = 0; position < syncReference.Length; position++)
        {
            double value = syncReference[position];
            if (inPulse)
            {
                if (value > high)
                {
                    int length = position - currentStart;
                    if (length >= minimumSyncLength
                        && length <= maximumSyncLength
                        && currentStart != 0)
                    {
                        pulses.Add(new Pulse(currentStart, length));
                    }

                    inPulse = false;
                }
            }
            else if (value <= high)
            {
                currentStart = position;
                inPulse = true;
            }
        }

        return pulses.ToArray();
    }
}
