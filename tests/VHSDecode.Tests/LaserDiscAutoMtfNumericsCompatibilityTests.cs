using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscAutoMtfNumericsCompatibilityTests
{
    [Theory(DisplayName = "LD automatic MTF mean matches NumPy float64 reduction")]
    [InlineData(30, false, 0x3FDE646C6401B657L)]
    [InlineData(900, true, 0x3FDCD2EA26195D8BL)]
    public void AutomaticMtfMeanMatchesNumpyFloat64Reduction(
        int ratioCount,
        bool isClv,
        long expectedLevelBits)
    {
        var controller = new LaserDiscAutoMtfController();
        if (isClv)
        {
            MarkAsClv(controller);
        }

        LaserDiscMtfUpdate update = default!;
        ulong state = 3;
        for (int i = 0; i < ratioCount; i++)
        {
            state = unchecked((state * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL);
            double unit = (state >> 11) / 9_007_199_254_740_992.0;
            update = controller.Observe(1.125 + (unit * 0.25));
        }

        Assert.Equal(ratioCount, update.RatioCount);
        Assert.Equal(expectedLevelBits, BitConverter.DoubleToInt64Bits(update.Level));
    }

    [Fact(DisplayName = "LD automatic MTF resets CLV state on an empty VBI pair")]
    public void AutomaticMtfResetsClvStateOnEmptyVbiPair()
    {
        var controller = new LaserDiscAutoMtfController();
        MarkAsClv(controller);

        controller.ObserveAcceptedField(CreateField(200, true, []), "NTSC");
        controller.ObserveAcceptedField(CreateField(300, false, []), "NTSC");

        Assert.False(controller.IsClv);
    }

    [Fact(DisplayName = "LD automatic MTF retains first-field VBI for later second fields")]
    public void AutomaticMtfRetainsFirstFieldVbiForLaterSecondFields()
    {
        var controller = new LaserDiscAutoMtfController();
        MarkAsClv(controller);

        controller.ObserveAcceptedField(CreateField(200, false, [0xF00001]), "NTSC");

        Assert.False(controller.IsClv);
    }

    private static void MarkAsClv(LaserDiscAutoMtfController controller)
    {
        controller.ObserveAcceptedField(CreateField(0, true, [0x8AE001]), "NTSC");
        controller.ObserveAcceptedField(CreateField(100, false, [0xF0DD01]), "NTSC");
        Assert.True(controller.IsClv);
    }

    private static TbcDecodedField CreateField(long startSample, bool isFirstField, int[] vbiData)
    {
        return new TbcDecodedField(
            StartSample: startSample,
            Samples: [],
            LineLocations: new LineLocationResult([], []),
            Timing: new SyncTiming(
                NominalLineLength: 1.0,
                HSyncMedian: 1.0,
                HSyncOffset: 0.0,
                HSync: new SyncRange(0.0, 0.0),
                Equalizing: new SyncRange(0.0, 0.0),
                VSync: new SyncRange(0.0, 0.0)),
            SyncThresholdHz: 0.0,
            MeanLineLength: 1.0,
            RawPulseCount: 0,
            ClassifiedPulseCount: 0,
            DetectedFirstField: isFirstField,
            VbiData: vbiData);
    }
}
