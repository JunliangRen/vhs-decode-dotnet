using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class CvbsReceiverTests
{
    [Theory]
    [InlineData("-n", "NTSC")]
    [InlineData("-p", "PAL")]
    public void FiftyOhmReceiverUsesTheCvbsPath(string flag, string system)
    {
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, flag, "--50ohms"));
        Assert.Equal(system, session.System);
        Assert.True(session.TbcRenderer.UseCvbsReceiver);
        Assert.False(session.RestoreVhdCvbsLuma);
    }

    [Fact]
    public void CvbsVhdEnablesReceiverAndRollingRestoration()
    {
        using DecodeSession session = DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, "-n", "--tf", "vhd"));
        Assert.True(session.TbcRenderer.UseCvbsReceiver);
        Assert.True(session.TbcRenderer.CaptureCvbsBlanking);
        Assert.True(session.RestoreVhdCvbsLuma);
        Assert.Throws<ArgumentException>(() => DecodeSessionFactory.Create(Parse(CliSpecs.Cvbs, "-p", "--tf", "VHD")));
        Assert.Throws<CommandLineParseException>(() => Parse(CliSpecs.Vhs, "-n", "--50ohms"));
        Assert.Equal("VHD", Parse(CliSpecs.Vhs, "-n", "--tf", "VHD").Get<string>("tape_format"));
    }

    [Fact]
    public void PorchRestorationReducesTiltAndPreservesEdges()
    {
        const int width = 910;
        const int height = 263;
        var spec = new TbcFrameSpec("NTSC", width, height, 315e6 / 22, 74, 110, 134, 894);
        var converter = new VideoOutputConverter(0, 1, 1024, -40, 358.4);
        var gate = new CvbsReceiver.Gates(18, 58, 120, 132, 895, 907, 0);
        const double corner = 7600;
        double factor = 2 * Math.PI * corner / spec.OutputSampleRateHz;
        double[] raw = new double[spec.FieldSampleCount];
        for (int y = 0; y < height; y++)
        {
            double sum = 0;
            bool affected = y == 51;
            for (int x = 0; x < width; x++)
            {
                double expected = x < 68 ? -10000 : x is >= 300 and < 700 ? 6000 : 0;
                if (affected)
                {
                    expected += 250 * Math.Max(0, x - 131) / (900.5 - 131);
                }
                double sample = (expected - factor * sum) / (1 + factor / 2);
                raw[y * width + x] = sample;
                sum += sample;
            }
        }
        Assert.True(CvbsReceiver.RestoreLevels(raw, spec, gate, corner, converter));
        Assert.Equal(0, raw[51 * width + 850], 8);
        Assert.Equal(24, raw[51 * width + 300] - raw[51 * width + 299], 8);
        Assert.Equal(-40, raw[51 * width + 25], 8);
    }

    [Fact]
    public void CadenceTrackingRequiresRealHorizontalPulses()
    {
        const int period = 2542;
        const int first = period * 13;
        const int lines = 263;
        double[] video = new double[period * 290];
        Assert.Null(CvbsReceiver.TrackLineZero(video, first, period, 40e6, lines, 10000));
        for (int y = 20; y < lines - 12; y++)
        {
            video.AsSpan(first + y * period + 16, 188).Fill(-10000);
        }
        double located = Assert.IsType<double>(
            CvbsReceiver.TrackLineZero(video, first, period, 40e6, lines, 10000));
        Assert.InRange(located, first + 3, first + 29);
    }

    [Fact]
    public void InitialSyncPrefersEarlierValidatedBlanking()
    {
        const int period = 2542;
        var video = new double[period * 280];
        Array.Fill(video, 6500);
        for (int y = 0; y < 280; y++)
        {
            video.AsSpan(y * period, 188).Fill(-12000);
            video.AsSpan(y * period + 188, 212).Clear();
        }
        for (int i = 0; i < 6; i++)
        {
            int start = 100 * period + i * (period / 2);
            video.AsSpan(start, 1080).Fill(-12000);
            video.AsSpan(start + 1080, period / 2 - 1080).Clear();
        }

        IReadOnlyList<Pulse>? earlier = null;
        int examined = 0;
        CvbsReceiver.Sync? result = CvbsReceiver.AcquireSync(video, 40e6, period, pulses =>
        {
            // Model two usable separators whose vertical-blanking parser
            // reports different earliest complete fields.
            switch (++examined)
            {
                case 1:
                    return 100 * period;
                case 2:
                    earlier = pulses;
                    return 80 * period;
                default:
                    return null;
            }
        });
        Assert.NotNull(earlier);
        Assert.Same(earlier, Assert.IsType<CvbsReceiver.Sync>(result).Pulses);
    }

    private static ParsedCommand Parse(DecodeCommandSpec spec, params string[] args)
        => new CommandLineParser().Parse(spec, [.. args, "input.s16", "output"], spec.Aliases[0]);
}
