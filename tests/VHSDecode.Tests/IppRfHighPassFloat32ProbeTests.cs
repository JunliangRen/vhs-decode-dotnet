using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class IppRfHighPassFloat32ProbeTests
{
    [Fact]
    public void ExtractAndReduceMatchTheStockQuantizedChannelBits()
    {
        double payloadNaN = BitConverter.UInt64BitsToDouble(0x7ff0123456789abcUL);
        double negativeNaN = BitConverter.UInt64BitsToDouble(0xfff8a5a500000000UL);
        double halfway = 1.0 + Math.ScaleB(1.0, -24);
        double[][] inputs =
        [
            [],
            [0.0, -0.0, double.Epsilon, -double.Epsilon, float.Epsilon, -float.Epsilon, halfway],
            [1.0, Math.BitIncrement(halfway), Math.BitDecrement(halfway), -halfway, 16_777_216.0, -16_777_216.0],
            [float.MaxValue, -float.MaxValue, 1.0, -1.0, 0.0],
            [float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue],
            [double.MaxValue, -double.MaxValue, 0.0, -0.0, 1.0],
            [payloadNaN, 0.0, negativeNaN, -0.0, payloadNaN],
            [1.0, 0.0, -0.0, 1.0, negativeNaN],
        ];
        foreach (double[] input in inputs)
        {
            Complex[] inverse = input.Select((value, i) => new Complex(value, i + 2)).ToArray();
            double[] stock = ComplexRealExtractor.Create(inverse);
            RfBlockDecodePipeline.QuantizeToFloat32InPlace(stock);
            float[] compact = ComplexRealExtractor.CreateFloat32(inverse);
            AssertSampleBits(stock, compact);
            Assert.Equal(BitConverter.DoubleToInt64Bits(NumbaReduction.StandardDeviationFloat32InputToFloat64(stock)),
                BitConverter.DoubleToInt64Bits(NumbaReduction.StandardDeviationOwnedRfHighPassFloat32(compact)));
        }
    }

    [Theory]
    [InlineData(-3, 2, 13)]
    [InlineData(0, 0, 3)]
    [InlineData(3, 1, 14)]
    public void OverlayAndRangesKeepExactBoundariesAndOffsetClipping(int offset, int start, int end)
    {
        float[] compact =
        [
            0f, -0f, -3f, 3f, MathF.BitDecrement(-3f), MathF.BitIncrement(3f), float.NaN,
            float.PositiveInfinity, float.NegativeInfinity, 2f, -2f, -4f, 4f, float.NaN, -3f,
        ];
        int length = compact.Length;
        var wide = new RfDecodedSpan(0, [], new double[length], new double[length],
            RfHighPass: compact.Select(value => (double)value).ToArray());
        RfDecodedSpan packed = wide with { RfHighPass = null, LdRfHighPassFloat32 = compact };
        bool[] sync = new bool[length];
        bool[] stockMap = Enumerable.Repeat(true, length).ToArray();
        bool[] compactMap = (bool[])stockMap.Clone();
        var thresholds = new LaserDiscDemodDropoutComparison.Thresholds(-1, 1, -2, -1, 1, -2, 1, 3);
        LaserDiscDemodDropoutComparison.FillErrorMap(wide, sync, stockMap, start, end, thresholds,
            hasRawDemod: true, hasVideoLowPass: false, hasRfHighPass: true, rfHighPassOffset: offset);
        LaserDiscDemodDropoutComparison.FillErrorMap(packed, sync, compactMap, start, end, thresholds,
            hasRawDemod: true, hasVideoLowPass: false, hasRfHighPass: true, rfHighPassOffset: offset);
        Assert.Equal(MemoryMarshal.AsBytes(stockMap.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(compactMap.AsSpan()).ToArray());
        Assert.Equal(RangeBits(stockMap, start, end), RangeBits(compactMap, start, end));
    }

    [Fact]
    public void OwnedIppFieldRetainsIndependentCompactHighPassAndExactWindowOffsets()
    {
        Assert.SkipUnless(Avx2.IsSupported && Fma.IsSupported, "Compact LD video requires AVX2 and FMA.");
        Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");

        var command = new CommandLineParser().Parse(CliSpecs.LaserDisc,
            ["--NTSC", "--threads", "4", "--dsp-backend", "ipp-fast", "capture.s16", "unused"]);
        using DecodeSession stock = DecodeSessionFactory.Create(command);
        using DecodeSession candidate = DecodeSessionFactory.Create(command);
        var stockReader = Assert.IsType<RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader>(
            stock.StreamDecoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        var compactReader = Assert.IsType<RfBlockStreamDecoder.OwnedImmediateLaserDiscFieldReader>(
            candidate.StreamDecoder.TryCreateOwnedImmediateLaserDiscFieldReader());
        stockReader.EnableCompactCpuPostFmVideoIfSupported();
        compactReader.EnableCompactCpuPostFmVideoIfSupported();
        compactReader.EnableCompactRfHighPass();
        Assert.False(stockReader.CompactRfHighPass);
        Assert.True(compactReader.CompactRfHighPass);

        int stride = candidate.StreamDecoder.BlockStride;
        int blockLength = candidate.StreamDecoder.BlockLength;
        short[] samples = new short[(2 * stride) + blockLength];
        for (int i = 0; i < samples.Length; i++)
        {
            double phase = Math.Tau * 8_300_000.0 * i / candidate.DecodeSampleRateHz + 3 * Math.Sin(i * 0.003);
            samples[i] = (short)Math.Round(19_000 * Math.Cos(phase) + 2_000 * Math.Sin(i * 0.173));
        }
        byte[] bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        using var stockStream = new MemoryStream(bytes, writable: false);
        using var compactStream = new MemoryStream(bytes, writable: false);

        using var stockFirst = Assert.IsType<RfBlockStreamDecoder.RfDecodedSpanLease>(stockReader.Read(stockStream, 11, 17));
        using var compactFirst = Assert.IsType<RfBlockStreamDecoder.RfDecodedSpanLease>(compactReader.Read(compactStream, 11, 17));
        Assert.Null(compactFirst.Span.RfHighPass);
        float[] retained = Assert.IsType<float[]>(compactFirst.Span.LdRfHighPassFloat32);
        AssertSampleBits(Assert.IsType<double[]>(stockFirst.Span.RfHighPass), retained);
        int[] retainedBits = retained.Select(BitConverter.SingleToInt32Bits).ToArray();

        using var stockCross = Assert.IsType<RfBlockStreamDecoder.RfDecodedSpanLease>(
            stockReader.Read(stockStream, stride - 7, stride + 23));
        using var compactCross = Assert.IsType<RfBlockStreamDecoder.RfDecodedSpanLease>(
            compactReader.Read(compactStream, stride - 7, stride + 23));
        Assert.Null(compactCross.Span.RfHighPass);
        float[] cross = Assert.IsType<float[]>(compactCross.Span.LdRfHighPassFloat32);
        Assert.NotSame(retained, cross);
        AssertSampleBits(Assert.IsType<double[]>(stockCross.Span.RfHighPass), cross);
        Assert.Equal(retainedBits, retained.Select(BitConverter.SingleToInt32Bits).ToArray());
        Assert.Throws<InvalidOperationException>(compactReader.EnableCompactRfHighPass);
    }

    private static void AssertSampleBits(ReadOnlySpan<double> stock, ReadOnlySpan<float> compact)
    {
        Assert.Equal(stock.Length, compact.Length);
        for (int i = 0; i < stock.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits((float)stock[i]), BitConverter.SingleToInt32Bits(compact[i]));
        }
    }

    private static (long Start, long End)[] RangeBits(bool[] mask, int start, int end)
        => LaserDiscDropoutDetector.BuildErrorRanges(mask, start, end, 40.0)
            .Select(range => (BitConverter.DoubleToInt64Bits(range.Start), BitConverter.DoubleToInt64Bits(range.End)))
            .ToArray();
}
