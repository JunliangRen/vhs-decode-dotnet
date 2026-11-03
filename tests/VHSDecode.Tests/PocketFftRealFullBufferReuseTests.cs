using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

[Collection(DspWorkingBufferCollection.Name)]
public sealed class PocketFftRealFullBufferReuseTests
{
    // Frozen from the allocating v1 API before reusing its packed staging buffer.
    [Theory(DisplayName = "Full real DUCC FFT retains allocating API bits with reused packed storage")]
    [InlineData(4, "D98C2E66CFE69931AA44A02531879E9592E1DDDE372E8D027DE6FBD261C8A76B")]
    [InlineData(8, "90BC2447C0EE716B536A9372E00AA744124A5D2535319AC6F27B82AA682A634A")]
    [InlineData(1_024, "EF39F8E5A624DE41ED61B1077C62731F0A50592010A3B9CD83C23EB5D2F728DA")]
    [InlineData(16_384, "D38603987C4E206B3F67129129C2089991219B45414D22B9902B8791047E3450")]
    [InlineData(32_768, "D0A3D4E837EC46BC52E121E1CCCA2257D21318FC950EAF56B7E718BF8FA8910E")]
    [InlineData(65_536, "B9B6464AB28D0AABA8E7112D7D458C07B5B966BF188E8E570895D581500D106E")]
    public void FullRealDuccRetainsAllocatingApiBits(int length, string frozenHash)
    {
        double[] input = BuildInput(length);
        Complex[] random = Check(input);
        WindowsFrozenBitOracle.Equal(frozenHash, Hash(random));
        Check(new double[length]);
        Check(Enumerable.Range(0, length).Select(index => (index & 1) == 0 ? 0.0 : -0.0).ToArray());
        ulong[] specialBits =
        [
            0x0000_0000_0000_0001UL,
            0x8000_0000_0000_0001UL,
            0x7fef_ffff_ffff_ffffUL,
            0xffef_ffff_ffff_ffffUL,
            0x7ff0_0000_0000_0000UL,
            0xfff0_0000_0000_0000UL,
            0x7ff8_0000_0000_0123UL,
            0xfff8_0000_0000_0456UL
        ];
        Check(Enumerable.Range(0, length)
            .Select(index => BitConverter.UInt64BitsToDouble(specialBits[index % specialBits.Length]))
            .ToArray());

        static Complex[] Check(double[] values)
        {
            double[] original = values.ToArray();
            Complex[] expected = BuildAllocatingApiReference(values);
            Complex[] actual = PocketFftComplex.ForwardDuccRealFull(values);
            // Existing FFT special-value compatibility excludes NaN sign/payload:
            // even repeated v1 calls can change those bits across JIT tiers.
            ReadOnlySpan<double> expectedComponents = MemoryMarshal.Cast<Complex, double>(expected);
            ReadOnlySpan<double> actualComponents = MemoryMarshal.Cast<Complex, double>(actual);
            for (int i = 0; i < expectedComponents.Length; i++)
            {
                if (double.IsNaN(expectedComponents[i]))
                {
                    Assert.True(double.IsNaN(actualComponents[i]));
                }
                else
                {
                    Assert.Equal(BitConverter.DoubleToUInt64Bits(expectedComponents[i]),
                        BitConverter.DoubleToUInt64Bits(actualComponents[i]));
                }
            }
            Assert.True(MemoryMarshal.AsBytes(original.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(values.AsSpan())),
                "The preserving full real FFT changed its input.");
            return actual;
        }
    }

    [Fact(DisplayName = "Warmed full real DUCC FFT allocates only its returned spectrum")]
    public void WarmedFullRealDuccAllocatesOnlyReturnedSpectrum()
    {
        foreach (int length in new[] { 65_536, 4, 32_768, 8, 16_384, 1_024 })
        {
            double[] input = BuildInput(length);
            for (int warmup = 0; warmup < 8; warmup++)
            {
                _ = PocketFftComplex.ForwardDuccRealFull(input);
            }
            long minimumAllocated = long.MaxValue;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Complex[] spectrum = PocketFftComplex.ForwardDuccRealFull(input);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                minimumAllocated = Math.Min(minimumAllocated, allocated);
                GC.KeepAlive(spectrum);
            }
            Assert.InRange(minimumAllocated, length * 16L, (length * 16L) + 256);
        }
    }

    [Fact(DisplayName = "Parallel full real DUCC FFT keeps thread-local scratch and returned spectra independent")]
    public async Task ParallelFullRealDuccKeepsScratchAndReturnedSpectraIndependent()
    {
        const int workers = 8;
        int[] lengths = [1_024, 16_384, 32_768, 65_536];
        double[][][] inputs = Enumerable.Range(0, workers)
            .Select(worker => lengths.Select(length => BuildInput(length, 0x16CE_AA41U ^ ((uint)worker * 7_919U))).ToArray())
            .ToArray();
        string[][] expected = inputs.Select(cases => cases.Select(input => Hash(BuildAllocatingApiReference(input))).ToArray()).ToArray();
        using var barrier = new Barrier(workers);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task[] tasks = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
        {
            Complex[]? retained = null;
            string? retainedHash = null;
            for (int iteration = 0; iteration < lengths.Length; iteration++)
            {
                int index = (worker + iteration) % lengths.Length;
                barrier.SignalAndWait(cancellation);
                Complex[] actual = PocketFftComplex.ForwardDuccRealFull(inputs[worker][index]);
                Assert.Equal(expected[worker][index], Hash(actual));
                if (retained is null)
                {
                    retained = actual;
                    retainedHash = Hash(actual);
                }
            }
            Assert.Equal(retainedHash, Hash(Assert.IsType<Complex[]>(retained)));
        }, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(tasks);
    }

    private static Complex[] BuildAllocatingApiReference(ReadOnlySpan<double> input)
    {
        // Retain the former API's independent half-spectrum and expansion path.
        Complex[] halfSpectrum = PocketFftComplex.ForwardDuccReal(input);
        var output = new Complex[input.Length];
        halfSpectrum.CopyTo(output, 0);
        for (int i = 1; i < halfSpectrum.Length - 1; i++)
        {
            output[^i] = Complex.Conjugate(halfSpectrum[i]);
        }
        output[0] = new Complex(output[0].Real, -0.0);
        output[input.Length / 2] = new Complex(output[input.Length / 2].Real, -0.0);
        return output;
    }

    private static double[] BuildInput(int length, uint state = 0x16CE_AA41U)
    {
        var input = new double[length];
        for (int i = 0; i < input.Length; i++)
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            input[i] = unchecked((int)state) / 65_536.0;
        }
        input[0] = -0.0;
        return input;
    }

    private static string Hash(Complex[] values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
}
