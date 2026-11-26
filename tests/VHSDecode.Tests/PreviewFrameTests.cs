using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Tbc;
using VHSDecode.Preview;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PreviewFrameTests
{
    [Fact]
    public void BgraConversionUsesChromaFromTheSameField()
    {
        var frame = new PreviewFrame(
            [100, 100, 100, 100, 100, 100, 100, 100, 16, 240, 128, 128],
            2, 4, TimeSpan.Zero, 25);
        var bgra = new byte[32];

        frame.CopyToBgra(bgra);

        Assert.Equal(bgra[..8], bgra[16..24]);
        Assert.Equal(bgra[8..16], bgra[24..]);
        Assert.Equal(0, bgra[0]);
        Assert.Equal(255, bgra[8]);
        Assert.All(new[] { 3, 7, 11, 15, 19, 23, 27, 31 }, i => Assert.Equal(255, bgra[i]));
    }

    [Fact]
    public void FullFieldCropPreservesRowsAndKeepsOwnedSourcePixels()
    {
        byte[] pixels = Enumerable.Range(0, 48).Select(value => (byte)value).ToArray();
        var field = new PreviewField(pixels, new(0, 0, 4, 3), new(1, 1, 2, 2),
            true, TimeSpan.FromMilliseconds(16), 50, default, default, 17_734_475, 16_000, 256);
        var cropped = new byte[16];
        field.CopyToBgra(cropped, PreviewRegion.ActiveVideo);

        Assert.Equal(pixels[20..28].Concat(pixels[36..44]).ToArray(), cropped);
        var full = new byte[48];
        field.CopyToBgra(full, PreviewRegion.FullRaster);
        Assert.Equal(pixels, full);
        Assert.Throws<ArgumentException>(() => field.CopyToBgra(cropped, PreviewRegion.FullRaster));
    }

    [Fact]
    public void FullRasterLumaUsesTheFieldCalibrationWithoutChangingLegacyRendering()
    {
        var command = new CommandLineParser().Parse(CliSpecs.Vhs, ["--pal", "input.s16", "output"]);
        using var session = DecodeSessionFactory.Create(command);
        int width = session.TbcFrameSpec.OutputLineLength;
        int height = session.TbcFrameSpec.OutputLineCount;
        var converter = new VideoOutputConverter(4_100_000, 8000, 25_000, -40, 200);
        ushort white = converter.ConvertHz(converter.IreToHz(100));
        var samples = Enumerable.Repeat(white, checked(width * height)).ToArray();
        var field = new TbcDecodedField(0, samples, null!, null!, 0, 0, 0, 0, OutputConverter: converter);
        var native = new PreviewFieldRenderer(session, width, height * 2, fullRaster: true, nativeChroma: true);
        byte[] pixel = native.RenderFullFieldBgra(field, true)[..4];
        Assert.Equal<byte>([255, 255, 255, 255], pixel);

        var legacy = new PreviewFieldRenderer(session, 64, 48);
        var first = legacy.Render(field, true);
        var second = legacy.Render(field with { OutputConverter = null }, true);
        Assert.Equal(first.Luma, second.Luma);
    }

    [Theory]
    [InlineData("pal", 576)]
    [InlineData("ntsc", 480)]
    public void FullRasterRetainsEverySourceSampleWithEncoderPadding(string system, int activeHeight)
    {
        var command = new VHSDecode.Core.CommandLine.CommandLineParser().Parse(
            VHSDecode.Core.CommandLine.CliSpecs.Vhs,
            ["--" + system, "--preview-server", "--preview-full-raster", "input.s16"]);
        Assert.True(command.Get<bool>("preview_full_raster"));
        using var session = VHSDecode.Core.Decode.DecodeSessionFactory.Create(command);
        var spec = session.TbcFrameSpec;
        var raster = PreviewFileDecoder.Dimensions(session, PreviewRegion.FullRaster);
        Assert.Equal(activeHeight, PreviewFileDecoder.Dimensions(session, PreviewRegion.ActiveVideo).Height);
        Assert.InRange(raster.Width - spec.OutputLineLength, 0, 1);
        Assert.InRange(raster.Height - 2 * spec.OutputLineCount, 0, 2);
        Assert.Equal(0, raster.Width % 2);
        Assert.Equal(0, raster.Height % 4);
    }

    [Fact]
    public void CancelledFileDecodeDoesNotOpenTheInput()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.Throws<OperationCanceledException>(() => PreviewFileDecoder.Decode(
            new PreviewFileOptions("missing.s16", PreviewSourceFormat.Vhs, PreviewVideoStandard.Ntsc),
            _ => throw new InvalidOperationException("Unexpected frame"), stop.Token));
    }
}
