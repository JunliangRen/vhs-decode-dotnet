using System.Numerics;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftConcurrencyTests
{
    [Fact(DisplayName = "Parallel in-place DUCC inverse FFT remains bit-exact")]
    public void ParallelInPlaceDuccInverseFftRemainsBitExact()
    {
        Complex[] spectrum = Enumerable.Range(0, 32_768)
            .Select(index => new Complex(
                Math.Sin(index * 0.017) + (index * 0.0001),
                Math.Cos(index * 0.013) - (index * 0.0002)))
            .ToArray();
        Complex[] expected = PocketFftComplex.InverseDucc(spectrum);

        Parallel.For(
            0,
            16,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            _ =>
            {
                Complex[] actual = spectrum.ToArray();
                PocketFftComplex.InverseDuccInPlace(actual);
                Assert.Equal(expected, actual);
            });
    }
}
