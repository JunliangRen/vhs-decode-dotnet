using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscBurstReductionNumericsCompatibilityTests
{
    private const int LineLength = 4_000;
    private const int LineCount = 325;

    [Fact(DisplayName = "PAL LD burst levels use Numba float32 centering and RMS")]
    public void PalLaserDiscBurstLevelsUseNumbaFloat32CenteringAndRms()
    {
        TbcFieldDecodePipeline pipeline = CreatePipeline("PAL");
        double[] video = BuildBurstVideo();
        double[] lineLocations = BuildLineLocations();

        double? burstLevel = pipeline.ComputeLaserDiscPalBurstLevel(
            video,
            lineLocations,
            line: 12,
            hzIre: 66_335.5 / 30.0);

        Assert.NotNull(burstLevel);
        Assert.Equal(0x40E9D1C5EE62296EUL, BitConverter.DoubleToUInt64Bits(burstLevel.Value));

        double? medianBurstIre = pipeline.ComputeLaserDiscMedianBurstIre(
            video,
            lineLocations,
            isFirstField: true,
            CreateConverter());

        Assert.NotNull(medianBurstIre);
        Assert.Equal(0x401526B9E6EB3E34UL, BitConverter.DoubleToUInt64Bits(medianBurstIre.Value));
    }

    [Fact(DisplayName = "NTSC LD burst medians use Numba float32 RMS")]
    public void NtscLaserDiscBurstMediansUseNumbaFloat32Rms()
    {
        TbcFieldDecodePipeline pipeline = CreatePipeline("NTSC");
        double? medianBurstIre = pipeline.ComputeLaserDiscMedianBurstIre(
            BuildBurstVideo(),
            BuildLineLocations(),
            isFirstField: true,
            CreateConverter());

        Assert.NotNull(medianBurstIre);
        Assert.Equal(0x401526B9C1D89DCDUL, BitConverter.DoubleToUInt64Bits(medianBurstIre.Value));
    }

    private static TbcFieldDecodePipeline CreatePipeline(string system)
    {
        VideoOutputConverter converter = CreateConverter();
        var analyzer = new SyncAnalyzer(
            sampleRateHz: 40_000_000.0,
            linePeriodUs: 100.0,
            hsyncPulseUs: 10.0,
            equalizingPulseUs: 5.0,
            vsyncPulseUs: 20.0);
        var spec = new TbcFrameSpec(
            system,
            OutputLineLength: LineLength,
            OutputLineCount: LineCount,
            OutputSampleRateHz: 40_000_000.0,
            ColourBurstStart: null,
            ColourBurstEnd: null,
            ActiveVideoStart: null,
            ActiveVideoEnd: null);
        return new TbcFieldDecodePipeline(
            analyzer,
            new TbcFieldRenderer(spec, converter),
            converter,
            system,
            TbcDropoutDetectionOptions.Disabled,
            decodeVbiData: true);
    }

    private static VideoOutputConverter CreateConverter()
        => new(
            ire0: 8_000_000.0,
            hzIre: 10_000.0,
            outputZero: 1_024,
            vsyncIre: -40.0,
            outputScale: 358.4);

    private static double[] BuildBurstVideo()
    {
        var video = new double[(LineCount + 1) * LineLength];
        for (int line = 0; line <= LineCount; line++)
        {
            int start = (line * LineLength) + 220;
            for (int i = 0; i < 97; i++)
            {
                video[start + i] = (float)(8_100_000 + ((i * 7_919) % 131_071) - 65_535);
            }
        }

        return video;
    }

    private static double[] BuildLineLocations()
        => Enumerable.Range(0, LineCount + 1)
            .Select(line => line * (double)LineLength)
            .ToArray();
}
