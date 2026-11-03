using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscUninitializedErrorMapTests
{
    [Theory(DisplayName = "LD dropout ranges ignore poisoned bytes outside the completely overwritten active map")]
    [InlineData(16, 80)]
    [InlineData(21, 70)]
    [InlineData(33, 65)]
    [InlineData(20, 20)]
    public void ActiveMapIsFullyDefinedAndOutsideBytesAreNeverConsumed(int start, int end)
    {
        const int length = 96;
        var video = new double[length];
        video[20] = video[70] = 161.0;
        var high = new double[length];
        high[25] = 4.0;
        var span = new RfDecodedSpan(0, [], video, [], RfHighPass: high);
        var limits = new LaserDiscDemodDropoutComparison.Thresholds(
            -50.0f, 160.0f, -75.0f, -30.0f, 115.0f, -50.0f, 20_000_000.0, 3.0f);
        bool[] map = GC.AllocateUninitializedArray<bool>(length);
        MemoryMarshal.AsBytes(map.AsSpan()).Fill(0xA5);

        LaserDiscDemodDropoutComparison.FillErrorMap(
            span, new bool[length], map, start, end, limits,
            hasRawDemod: false, hasVideoLowPass: false, hasRfHighPass: true, rfHighPassOffset: 5);

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(map.AsSpan());
        for (int index = 0; index < length; index++)
        {
            byte expected = index < start || index >= end
                ? (byte)0xA5
                : index is 20 or 30 or 70 ? (byte)1 : (byte)0;
            Assert.Equal(expected, bytes[index]);
        }

        RfDropoutRange[] expectedRanges = (start, end) switch
        {
            (16, 80) => [new(12.0, 41.0), new(70.0, 70.0)],
            (21, 70) => [new(30.0, 30.0)],
            _ => []
        };
        Assert.Equal(expectedRanges, LaserDiscDropoutDetector.BuildErrorRanges(map, start, end, 40.0));
    }
}