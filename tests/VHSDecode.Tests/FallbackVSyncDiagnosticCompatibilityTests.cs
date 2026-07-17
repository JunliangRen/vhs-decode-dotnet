using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class FallbackVSyncDiagnosticCompatibilityTests
{
    [Fact(DisplayName = "Fallback VSync reports current-field predicted line0 like v0.4.0")]
    public void CurrentFieldPredictedLine0DiagnosticMatches()
    {
        FallbackVSyncResolution resolution = Resolve(
            BuildBackupPattern(primaryPatternStart: 2_000),
            frameLines: 5);

        Assert.Equal(700.0, resolution.Line0Location);
        Assert.Equal(
            "WARNING, line0 hsync not found for current field, but vsync area found, using predicted position, result may be garbled.",
            resolution.DiagnosticMessage);
    }

    [Fact(DisplayName = "Fallback VSync reports relaxed backup selection like v0.4.0")]
    public void RelaxedBackupDiagnosticMatches()
    {
        FallbackVSyncResolution resolution = Resolve(
            BuildBackupPattern(primaryPatternStart: 26_200),
            frameLines: 525,
            relaxed: true);

        Assert.Equal(700.0, resolution.Line0Location);
        Assert.Equal(
            "Switching to backup line0 estimation as primary is out of range.",
            resolution.DiagnosticMessage);
    }

    [Fact(DisplayName = "Fallback VSync reports whole-block predicted line0 like v0.4.0")]
    public void WholeBlockPredictedLine0DiagnosticMatches()
    {
        FallbackVSyncResolution resolution = Resolve(
            BuildBackupPattern(primaryPatternStart: null),
            frameLines: 525);

        Assert.Equal(700.0, resolution.Line0Location);
        Assert.Equal(
            "WARNING, line0 hsync not found in entire block, but vsync area found, using predicted position, result may be garbled.",
            resolution.DiagnosticMessage);
    }

    [Fact(DisplayName = "Fallback VSync reports out-of-range line0 like v0.4.0")]
    public void OutOfRangeLine0DiagnosticMatches()
    {
        Pulse[] pulses =
        [
            new(200, 10),
            new(300, 10),
            new(400, 4),
            new(450, 4),
            new(500, 4),
            new(550, 4)
        ];

        FallbackVSyncResolution resolution = Resolve(pulses, frameLines: 5);

        Assert.Equal(300.0, resolution.Line0Location);
        Assert.Equal(
            "WARNING, line0 hsync not found for current field, probably skipping one field.",
            resolution.DiagnosticMessage);
    }

    [Fact(DisplayName = "Fallback VSync reports long-pulse line0 guess like v0.4.0")]
    public void LongPulseGuessDiagnosticMatches()
    {
        Pulse[] pulses = [new(500, 40)];

        FallbackVSyncResolution resolution = Resolve(
            pulses,
            frameLines: 525,
            classify: _ => SyncPulseKind.VSync);

        Assert.Equal(200.0, resolution.Line0Location);
        Assert.Equal(
            "WARNING, line0 hsync not found, guessing something, result may be garbled.",
            resolution.DiagnosticMessage);
    }

    private static FallbackVSyncResolution Resolve(
        Pulse[] pulses,
        int frameLines,
        bool relaxed = false,
        Func<int, SyncPulseKind>? classify = null)
    {
        ClassifiedSyncPulse[] classified = pulses
            .Select((pulse, index) => new ClassifiedSyncPulse(
                classify?.Invoke(index) ?? ClassifyPulse(pulse),
                pulse,
                index != 0))
            .ToArray();
        return FallbackVSyncResolver.Resolve(
            classified,
            pulses,
            new double[Math.Max(1, (int)pulses[^1].Start + 100)],
            new SyncRange(35.0, 60.0),
            meanLineLength: 100.0,
            numEqualizingPulses: 6,
            frameLines,
            relaxed)!;
    }

    private static Pulse[] BuildBackupPattern(int? primaryPatternStart)
    {
        var pulses = new List<Pulse>();
        for (int index = 0; index <= 12; index++)
        {
            pulses.Add(new Pulse(index == 7 ? 720 : index * 100, 10));
        }

        pulses.AddRange([
            new Pulse(1_300, 40),
            new Pulse(1_350, 40),
            new Pulse(1_400, 4),
            new Pulse(1_450, 4),
            new Pulse(1_500, 4)
        ]);
        if (primaryPatternStart.HasValue)
        {
            int start = primaryPatternStart.Value;
            pulses.AddRange([
                new Pulse(start, 10),
                new Pulse(start + 100, 10),
                new Pulse(start + 200, 4),
                new Pulse(start + 250, 4),
                new Pulse(start + 300, 4),
                new Pulse(start + 350, 4)
            ]);
        }

        return [.. pulses];
    }

    private static SyncPulseKind ClassifyPulse(Pulse pulse)
    {
        if (pulse.Length >= 35)
        {
            return SyncPulseKind.VSync;
        }

        return pulse.Length < 8
            ? SyncPulseKind.Equalizing
            : SyncPulseKind.HSync;
    }
}
