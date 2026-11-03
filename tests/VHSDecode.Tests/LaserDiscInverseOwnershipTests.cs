using System.Numerics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class LaserDiscInverseOwnershipTests
{
    [Theory(DisplayName = "Disposable LD audio spectra retain allocating inverse bits")]
    [InlineData(DspBackend.Exact, 1_024)]
    [InlineData(DspBackend.Exact, 32_768)]
    [InlineData(DspBackend.IppFast, 1_024)]
    [InlineData(DspBackend.IppFast, 32_768)]
    public void DisposableAudioSpectraRetainAllocatingInverseBits(DspBackend backend, int length)
    {
        if (backend == DspBackend.IppFast)
        {
            Assert.SkipUnless(IppTestRuntime.TryProbe(out _), "Intel IPP runtime is unavailable.");
        }

        using var demodulator = new RfDemodulator(40_000_000.0, backend);
        var spectrum = new Complex[length];
        uint state = 0xC001_C0DE;
        for (int i = 0; i < spectrum.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            double real = unchecked((int)state) / 65_536.0;
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            double imaginary = unchecked((int)state) / 65_536.0;
            spectrum[i] = new Complex(real, imaginary);
        }
        spectrum[0] = new Complex(0.0, -0.0);
        spectrum[length / 2] = new Complex(-0.0, 0.0);
        Check(spectrum);
        Check(new Complex[length]);

        spectrum[3] = new Complex(BitConverter.UInt64BitsToDouble(0x7ff8_0000_0000_0123), double.PositiveInfinity);
        spectrum[7] = new Complex(double.NegativeInfinity, BitConverter.UInt64BitsToDouble(0xfff8_0000_0000_0456));
        Check(spectrum);

        var disposable = new Complex[length];
        long allocatingStart = GC.GetAllocatedBytesForCurrentThread();
        Complex[] allocated = demodulator.InverseLaserDiscFullSpectrum(disposable, useDuccExact: false);
        long allocatingBytes = GC.GetAllocatedBytesForCurrentThread() - allocatingStart;
        long disposableStart = GC.GetAllocatedBytesForCurrentThread();
        demodulator.InverseLaserDiscFullSpectrumInPlace(disposable, useDuccExact: false);
        long disposableBytes = GC.GetAllocatedBytesForCurrentThread() - disposableStart;
        Assert.True(allocatingBytes - disposableBytes >= length * 16L,
            $"Disposable inverse saved {allocatingBytes - disposableBytes} bytes, expected at least {length * 16L}.");
        GC.KeepAlive(allocated);

        void Check(Complex[] input)
        {
            Complex[] original = input.ToArray();
            Complex[] expected = demodulator.InverseLaserDiscFullSpectrum(input, useDuccExact: false);
            Complex[] actual = input.ToArray();
            demodulator.InverseLaserDiscFullSpectrumInPlace(actual, useDuccExact: false);
            Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())),
                $"Allocating and disposable inverse bits differed for {backend}, length {length}.");
            Assert.True(MemoryMarshal.AsBytes(original.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(input.AsSpan())));
        }
    }
}
