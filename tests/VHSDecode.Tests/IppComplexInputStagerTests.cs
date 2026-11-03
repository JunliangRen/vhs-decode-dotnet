using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class IppComplexInputStagerTests
{
    [Theory(DisplayName = "IPP real staging writes every real and positive-zero imaginary bit")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(1_023)]
    [InlineData(1_024)]
    [InlineData(32_767)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    public void WritesEveryBitWithOffsetsAndTails(int length)
    {
        double[] source = BuildInput(length + 3);
        double[] original = source.ToArray();
        for (int offset = 0; offset < 4; offset++)
        {
            var expected = new Complex[length];
            for (int index = 0; index < length; index++) expected[index] = new Complex(source[offset + index], 0.0);
            Complex[] actual = IppComplexInputStager.Create(source.AsSpan(offset, length));
            Assert.Equal(length, actual.Length);
            Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
        }
        Assert.True(MemoryMarshal.AsBytes(original.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(source.AsSpan())));
    }

    [Fact(DisplayName = "IPP staged buffers are independently owned")]
    public void OwnsItsOutput()
    {
        double[] input = BuildInput(17);
        Complex[] first = IppComplexInputStager.Create(input);
        Complex[] second = IppComplexInputStager.Create(input);
        Assert.NotSame(first, second);
        first[0] = new Complex(913.0, -7.0);
        Assert.Equal(BitConverter.DoubleToUInt64Bits(input[0]), BitConverter.DoubleToUInt64Bits(second[0].Real));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(second[0].Imaginary));
    }

    [Theory(DisplayName = "IPP full-spectrum forward keeps scalar staging FFT bits")]
    [InlineData(1_024)]
    [InlineData(32_768)]
    public void ForwardKeepsScalarStagingFftBits(int length)
    {
        if (!IppTestRuntime.TryProbe(out _)) Assert.Skip("Intel IPP runtime is unavailable.");
        var input = new double[length];
        var expected = new Complex[length];
        for (int index = 0; index < length; index++)
        {
            input[index] = (index * 7_919 % 65_521) - 32_760.0;
            expected[index] = new Complex(input[index], 0.0);
        }
        using var fft = new IppComplexFft64(length);
        fft.Forward(expected, expected);
        using var demodulator = new RfDemodulator(40_000_000.0, DspBackend.IppFast);
        Complex[] actual = demodulator.ForwardLaserDiscFullSpectrum(input);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
    }

    private static double[] BuildInput(int length)
    {
        ulong[] bits =
        [
            0x0000000000000000UL, 0x8000000000000000UL, 0x0000000000000001UL, 0x8000000000000001UL,
            0x0010000000000000UL, 0x8010000000000000UL, 0x7FEFFFFFFFFFFFFFUL, 0xFFEFFFFFFFFFFFFFUL,
            0x7FF0000000000000UL, 0xFFF0000000000000UL, 0x7FF8000000000123UL, 0xFFF8000000000456UL,
            0x7FF0000000000123UL, 0xFFF0000000000456UL, 0x3FF0000000000000UL, 0xBFF0000000000000UL
        ];
        return Enumerable.Range(0, length).Select(index => BitConverter.UInt64BitsToDouble(bits[index % bits.Length])).ToArray();
    }
}
