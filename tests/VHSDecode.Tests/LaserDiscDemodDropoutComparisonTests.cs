using System.Runtime.InteropServices;
using VHSDecode.Core.Decode;
using Xunit;
using Thresholds = VHSDecode.Core.Decode.LaserDiscDemodDropoutComparison.Thresholds;

namespace VHSDecode.Tests;

public sealed class LaserDiscDemodDropoutComparisonTests
{
    private static readonly Thresholds Limits = new(-50.0f, 160.0f, -75.0f,
        -30.0f, 115.0f, -50.0f, 20_000_000.0, 3.0f);

    [Theory(DisplayName = "LD demod comparison retains scalar map bytes for tails and optional channels")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(257)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    public void RetainsScalarMapWithTailsAndOptionalChannels(int length)
    {
        for (int channels = 0; channels < 8; channels++)
        {
            RfDecodedSpan span = BuildSpan(length, channels, special: length % 4 == 3);
            bool[] sync = BuildSync(length);
            Check(span, sync, 0, length, Limits, rfOffset: 19);
            int start = Math.Min(1, length);
            Check(span, sync, start, Math.Max(start, length - 1), Limits, rfOffset: -17);
        }
    }

    [Theory(DisplayName = "LD RFHPF offset writes retain original bounds and integer mapping")]
    [InlineData(-500)]
    [InlineData(-17)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(500)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void RetainsOffsetMapping(int offset)
    {
        Check(BuildSpan(257, 7, special: true), BuildSync(257), 1, 256, Limits, offset);
    }

    [Fact(DisplayName = "LD positive RFHPF offset does not erase base or earlier mapped errors")]
    public void PositiveOffsetOnlySetsTrueAfterTheCompleteBasePass()
    {
        var video = new double[32];
        video[2] = 161.0;
        var high = new double[32];
        high[0] = 4.0;
        high[1] = -4.0;
        high[20] = 4.0;
        high[31] = 4.0;
        var span = new RfDecodedSpan(0, [], video, [], RfHighPass: high);
        var map = new bool[32];
        Fill(span, new bool[32], map, 0, map.Length, Limits, false, false, true, rfOffset: 5);
        for (int index = 0; index < map.Length; index++)
            Assert.Equal(index is 2 or 5 or 6 or 25, map[index]);
    }

    [Fact(DisplayName = "LD raw demod compares float-rounded samples against the original double bound")]
    public void RawDemodMaximumRemainsDouble()
    {
        var raw = Enumerable.Repeat(20_000_000.0, 17).ToArray();
        var span = new RfDecodedSpan(0, [], new double[17], raw);
        var sync = new bool[17];
        var map = new bool[17];
        Thresholds below = Limits with { RawDemodMaximum = Math.BitDecrement(20_000_000.0) };
        Fill(span, sync, map, 0, map.Length, below, true, false, false, 0);
        Assert.All(map, value => Assert.True(value));
        Check(span, sync, 0, map.Length, below, 0);
        Check(span, sync, 0, map.Length, Limits with { RawDemodMaximum = Math.BitIncrement(20_000_000.0) }, 0);
        Array.Fill(raw, Math.BitIncrement(20_000_000.0));
        Fill(span, sync, map, 0, map.Length, Limits, true, false, false, 0);
        Assert.All(map, value => Assert.False(value));
        Array.Fill(raw, double.MaxValue);
        Fill(span, sync, map, 0, map.Length, Limits with { RawDemodMaximum = double.MaxValue }, true, false, false, 0);
        Assert.All(map, value => Assert.True(value));
    }

    [Fact(DisplayName = "LD video and lowpass thresholds retain float rounding and mixed sync minima")]
    public void VideoAndLowPassRemainFloatComparisons()
    {
        double[] videoEdges = [160.0, Math.BitIncrement(160.0), Math.BitDecrement(160.0),
            float.BitIncrement(160.0f), -50.0, Math.BitDecrement(-50.0), float.BitDecrement(-50.0f)];
        double[] lowEdges = [115.0, Math.BitIncrement(115.0), Math.BitDecrement(115.0),
            float.BitIncrement(115.0f), -30.0, Math.BitDecrement(-30.0), float.BitDecrement(-30.0f)];
        double[] video = Enumerable.Range(0, 33).Select(index => videoEdges[index % videoEdges.Length]).ToArray();
        double[] low = Enumerable.Range(0, 33).Select(index => lowEdges[index % lowEdges.Length]).ToArray();
        Check(new RfDecodedSpan(0, [], video, []), new bool[33], 0, 33, Limits, 0);
        Check(new RfDecodedSpan(0, [], new double[33], [], VideoLowPass: low), new bool[33], 0, 33, Limits, 0);
        Check(new RfDecodedSpan(0, [], video, [], VideoLowPass: low), BuildSync(33), 1, 32, Limits, 0);

        var belowNormal = Enumerable.Repeat(-60.0, 17).ToArray();
        var sync = new bool[17];
        for (int index = 0; index < sync.Length; index++)
            MemoryMarshal.AsBytes(sync.AsSpan())[index] = (byte)(index % 4 == 0 ? 0 : index % 4 == 1 ? 1 : index % 4 == 2 ? 2 : 128);
        var map = new bool[17];
        Fill(new RfDecodedSpan(0, [], belowNormal, []), sync, map, 0, 17, Limits, false, false, false, 0);
        for (int index = 0; index < map.Length; index++) Assert.Equal(index % 4 == 0, map[index]);
    }

    [Fact(DisplayName = "LD dropout ordered comparisons preserve NaN and infinity behavior")]
    public void PreservesOrderedSpecialValueComparisons()
    {
        RfDecodedSpan span = BuildSpan(257, 7, special: true);
        foreach (float highMaximum in new[] { -3.0f, -0.0f, 0.0f, 3.0f, float.PositiveInfinity, float.NaN })
            Check(span, BuildSync(257), 1, 256, Limits with { RfHighPassMaximum = highMaximum }, 19);
        foreach (double rawMaximum in new[] { double.NegativeInfinity, double.PositiveInfinity, double.NaN })
            Check(span, BuildSync(257), 1, 256, Limits with { RawDemodMaximum = rawMaximum }, -17);
        Check(span, BuildSync(257), 1, 256,
            Limits with { ValidMinimum = float.NaN, SyncMinimum = float.NaN, ValidMaximum05 = float.NaN }, 0);
        double[] nan = Enumerable.Repeat(double.NaN, 17).ToArray();
        var allNan = new RfDecodedSpan(0, [], nan, nan, VideoLowPass: nan, RfHighPass: nan);
        bool[] map = DirtyMap(17);
        Fill(allNan, BuildSync(17), map, 0, 17, Limits, true, true, true, 0);
        Assert.All(map, value => Assert.False(value));
    }

    [Fact(DisplayName = "LD comparison ignores optional channels with mismatched lengths")]
    public void IgnoresMismatchedOptionalChannels()
    {
        RfDecodedSpan original = BuildSpan(33, 7, special: true);
        Check(original with { DemodRaw = original.DemodRaw[..^1] }, BuildSync(33), 0, 33, Limits, 0);
        Check(original with { VideoLowPass = original.VideoLowPass![..^1] }, BuildSync(33), 0, 33, Limits, 0);
        Check(original with { RfHighPass = original.RfHighPass![..^1] }, BuildSync(33), 0, 33, Limits, 0);
        double[] aliased = original.Video.ToArray();
        Check(original with { Video = aliased, DemodRaw = aliased, VideoLowPass = aliased, RfHighPass = aliased }, BuildSync(33), 0, 33, Limits, 1);
    }

    [Fact(DisplayName = "LD short and malformed helper inputs retain scalar exceptions and partial writes")]
    public void PreservesScalarExceptionsAndShortCircuitBehavior()
    {
        RfDecodedSpan source = BuildSpan(33, 7, special: true);
        bool[] sync = BuildSync(33);
        CheckFailure(source, sync[..^1], 33, 0, 33, true, true, true);
        CheckFailure(source, sync, 32, 0, 33, true, true, true);
        CheckFailure(source with { DemodRaw = source.DemodRaw[..^1] }, sync, 33, 0, 33, true, true, true);
        CheckFailure(source with { VideoLowPass = source.VideoLowPass![..^1] }, sync, 33, 0, 33, true, true, true);
        CheckFailure(source with { RfHighPass = source.RfHighPass![..^1] }, sync, 33, 0, 33, true, true, true);
        CheckFailure(source with { VideoLowPass = null }, sync, 33, 0, 33, true, true, true);
        CheckFailure(source with { RfHighPass = null }, sync, 33, 0, 33, true, true, true);
        CheckFailure(source, sync, 33, -1, 8, true, true, true);
        CheckFailure(source, sync, 33, 0, 34, true, true, true);
        CheckFailure(source, sync, 33, 3, 2, true, true, true);
        // Empty loops must not eagerly dereference channels or sync data.
        CheckFailure(new RfDecodedSpan(0, [], null!, null!), null!, 0, 0, 0, true, true, true);
    }

    private static void Check(RfDecodedSpan span, bool[] sync, int start, int end, Thresholds limits, int rfOffset)
    {
        bool hasRaw = span.DemodRaw.Length == span.Video.Length;
        bool hasLow = span.VideoLowPass?.Length == span.Video.Length;
        bool hasHigh = span.RfHighPass?.Length == span.Video.Length;
        double[][] inputs = [span.Input, span.Video, span.DemodRaw, span.VideoLowPass ?? [], span.RfHighPass ?? []];
        double[][] before = inputs.Select(array => array.ToArray()).ToArray();
        bool[] syncBefore = sync.ToArray();
        bool[] expected = DirtyMap(span.Video.Length), actual = expected.ToArray();
        ScalarReference(span, sync, expected, start, end, limits, hasRaw, hasLow, hasHigh, rfOffset);
        Fill(span, sync, actual, start, end, limits, hasRaw, hasLow, hasHigh, rfOffset);
        AssertMapEqual(expected, actual);
        for (int channel = 0; channel < inputs.Length; channel++)
            Assert.True(MemoryMarshal.AsBytes(before[channel].AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(inputs[channel].AsSpan())));
        Assert.True(MemoryMarshal.AsBytes(syncBefore.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(sync.AsSpan())));
    }

    private static void CheckFailure(RfDecodedSpan span, bool[] sync, int mapLength, int start, int end,
        bool hasRaw, bool hasLow, bool hasHigh)
    {
        bool[] expected = DirtyMap(mapLength), actual = expected.ToArray();
        Exception? expectedError = Capture(() => ScalarReference(span, sync, expected, start, end, Limits, hasRaw, hasLow, hasHigh, 1));
        Exception? actualError = Capture(() => Fill(span, sync, actual, start, end, Limits, hasRaw, hasLow, hasHigh, 1));
        Assert.Equal(expectedError?.GetType(), actualError?.GetType());
        Assert.Equal((expectedError as ArgumentException)?.ParamName, (actualError as ArgumentException)?.ParamName);
        AssertMapEqual(expected, actual);
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Fill(RfDecodedSpan span, bool[] sync, bool[] map, int start, int end,
        Thresholds limits, bool hasRaw, bool hasLow, bool hasHigh, int rfOffset)
        => LaserDiscDemodDropoutComparison.FillErrorMap(span, sync, map, start, end, limits, hasRaw, hasLow, hasHigh, rfOffset);

    // Independent original comparison passes, including read/write and offset order.
    private static void ScalarReference(RfDecodedSpan span, bool[] sync, bool[] map, int start, int end,
        Thresholds limits, bool hasRaw, bool hasLow, bool hasHigh, int rfOffset)
    {
        for (int index = start; index < end; index++)
        {
            float minimum = sync[index] ? limits.SyncMinimum : limits.ValidMinimum;
            float video = (float)span.Video[index];
            bool videoOut = video < minimum || video > limits.ValidMaximum;
            bool rawOut = hasRaw && (float)span.DemodRaw[index] > limits.RawDemodMaximum;
            bool lowOut = false;
            if (hasLow)
            {
                float lowMinimum = sync[index] ? limits.SyncMinimum05 : limits.ValidMinimum05;
                float low = (float)span.VideoLowPass![index];
                lowOut = low < lowMinimum || low > limits.ValidMaximum05;
            }
            map[index] = videoOut || rawOut || lowOut;
        }
        if (hasHigh)
        {
            for (int index = start; index < end; index++)
            {
                float value = (float)span.RfHighPass![index];
                if (!(value < -limits.RfHighPassMaximum || value > limits.RfHighPassMaximum)) continue;
                int mapped = index + rfOffset;
                if (mapped >= start && mapped < end) map[mapped] = true;
            }
        }
    }

    private static RfDecodedSpan BuildSpan(int length, int channels, bool special)
    {
        var video = new double[length];
        var raw = (channels & 1) != 0 ? new double[length] : [];
        double[]? low = (channels & 2) != 0 ? new double[length] : null;
        double[]? high = (channels & 4) != 0 ? new double[length] : null;
        double[] values = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue,
            -double.MaxValue, BitConverter.UInt64BitsToDouble(0xFFF8_5432_1000_0001UL), -0.0, 0.0];
        for (int index = 0; index < length; index++)
        {
            video[index] = special ? values[index % values.Length] : index % 101 == 7 ? 161.0 : (index % 13) - 6.0;
            if (raw.Length > 0) raw[index] = special ? values[(index + 2) % values.Length] : 8_000_000.0;
            if (low is not null) low[index] = special ? values[(index + 4) % values.Length] : index % 97 == 5 ? 116.0 : 0.0;
            if (high is not null) high[index] = special ? values[(index + 6) % values.Length] : index % 71 == 3 ? 4.0 : 0.0;
        }
        return new RfDecodedSpan(0, [], video, raw, VideoLowPass: low, RfHighPass: high);
    }

    private static bool[] BuildSync(int length) => Enumerable.Range(0, length).Select(index => index % 3 == 0).ToArray();
    private static bool[] DirtyMap(int length) => Enumerable.Range(0, length).Select(index => index % 3 != 0).ToArray();
    private static void AssertMapEqual(bool[] expected, bool[] actual)
        => Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())),
            "Demod dropout map bytes or inactive entries changed.");
}
