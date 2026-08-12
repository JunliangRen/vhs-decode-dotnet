using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftComplexStorageTests
{
    [Fact(DisplayName = "Complex FFT direct output matches frozen power-of-two hashes")]
    public void ComplexFftDirectOutputMatchesFrozenPowerOfTwoHashes()
    {
        (int Length, string Forward, string Inverse, string RealForward)[] cases =
        [
            (2,
                "8B2FBC2238F18AC091DF838B61D6AA22438E5C3432F6C2C3D8FA2F531554B77A",
                "76A7A2C957BE5F029ECED24A0ABD58DD0613A57D0CCFA1B866E5A7694B38EF96",
                "CD118D7AEF61BF97213B05389578C31A7C0776F4EEAC81644610FF463DDF7EFE"),
            (4,
                "C45F84EA767D121455666F7B51BCBAF4862F9DE85288331BA4B48123F5BBC28C",
                "CCA76CE501AF1C440887AB6A0BC92F38E292F7894B2FC9C7EA0CE4D2BB8ECAD4",
                "9FC1B861DA40E7A830D02D975E93C9D9FD9F00CBED7A5D76695DD38480F4650C"),
            (8,
                "2FDF543B2E303A9C4DDBCA54EF7DD7B862915B41E68809FB7F6EAFCD73E33482",
                "B86AA22C59686B7BB6619A0CF84ABCC0E96042CBE18F7BD4A6D86F5B5FE5EBC7",
                "3DB197F9CE57303FB861CE2A80762305213E246453487198CF5A32DA912D876E"),
            (64,
                "4D519DCBAFD6CD7938905C09988697D4B3948DCA7D4E99E79932500D3772DD8F",
                "4E93E9EAAEF92A398743DF901F85E3BD10AD40CAE473F4287E65D1215CAC52E5",
                "EFA0315AD80E66E59C8CB11EFDF05F6AA6247FE5F3A746C8DD27AEDC954AEEAE"),
            (512,
                "61A1D6D5824F44CCEA7C247FCC5CAC002906266AEECB8DABEE55D277ED8B1E0D",
                "D0B6624E66F39A6117D66FEFD5193EA7DF45614D87EF9F460346157C1FA40AD1",
                "E3FA69E060BDB581BA22920FD004867DAA806EB764E0A97FBD96C6800CBA67A3"),
            (32_768,
                "15537D85C693906430C3F33CD2E3198BADCF7DBF779173713C0CB93A51D9C27F",
                "89A6E1AAFD260F194DFB7E601660BDBC794384BBCE25BBE52694AC34135E016B",
                "950264D00BFBB9E577539DD1CD8BAE660B3EA9EAC82DD131794CAA108341061B"),
            (131_072,
                "A86EFB540A2E9F4782DF7AEE6444289A193213E684BC934A561A0769F6E24F6F",
                "BB58295FB402C408B4B1C97ED6A08DB6D4F613C1C98B2E5F15D5070377FB8B3B",
                "C8D27FA613B3206C844C5430C4C299A2A2E7B7D3FC0092FD2F8A1FA86B434E67")
        ];

        foreach ((int length, string forwardHash, string inverseHash, string realForwardHash) in cases)
        {
            Complex[] input = BuildInput(length);
            Complex[] forward = PocketFftComplex.Forward(input);
            Assert.Equal(forwardHash, Hash(forward));
            Assert.Equal(inverseHash, Hash(PocketFftComplex.Inverse(forward)));
            Assert.Equal(
                realForwardHash,
                Hash(PocketFftComplex.ForwardReal(
                    input.Select(static value => value.Real).ToArray())));
        }

        Complex[] repeatedSmall = PocketFftComplex.Forward(BuildInput(cases[0].Length));
        Assert.Equal(cases[0].Forward, Hash(repeatedSmall));
    }

    [Fact(DisplayName = "Complex FFT direct output preserves special-value bits")]
    public void ComplexFftDirectOutputPreservesSpecialValueBits()
    {
        ulong[] patterns =
        [
            0x0000_0000_0000_0000UL,
            0x8000_0000_0000_0000UL,
            0x0000_0000_0000_0001UL,
            0x8000_0000_0000_0001UL,
            0x0010_0000_0000_0000UL,
            0x8010_0000_0000_0000UL,
            0x7FEF_FFFF_FFFF_FFFFUL,
            0xFFEF_FFFF_FFFF_FFFFUL,
            0x7FF0_0000_0000_0000UL,
            0xFFF0_0000_0000_0000UL,
            0x7FF8_0000_0000_0042UL,
            0xFFF8_0000_0000_0199UL
        ];
        for (int index = 0; index < patterns.Length; index++)
        {
            double value = BitConverter.UInt64BitsToDouble(patterns[index]);
            double pairedValue = BitConverter.UInt64BitsToDouble(
                patterns[(index + 1) % patterns.Length]);
            var complexInput = new Complex[64];
            complexInput[0] = new Complex(value, pairedValue);
            Complex[] forward = PocketFftComplex.Forward(complexInput);
            Assert.Equal(Hash(forward), Hash(PocketFftComplex.Forward(complexInput)));

            Complex[] expectedInverse = PocketFftComplex.Inverse(forward);
            Complex[] callerOutput = new Complex[forward.Length];
            PocketFftComplex.Inverse(forward, callerOutput);
            Assert.Equal(Hash(expectedInverse), Hash(callerOutput));

            Complex[] inPlace = forward.ToArray();
            PocketFftComplex.Inverse(inPlace, inPlace);
            Assert.Equal(Hash(expectedInverse), Hash(inPlace));

            var realInput = new double[64];
            realInput[0] = value;
            Complex[] expectedReal = PocketFftComplex.ForwardReal(realInput);
            var realCallerOutput = new Complex[realInput.Length];
            PocketFftComplex.ForwardReal(realInput, realCallerOutput);
            Assert.Equal(Hash(expectedReal), Hash(realCallerOutput));

            var overlappingStorage = new double[2 * realInput.Length];
            realInput.CopyTo(overlappingStorage, 0);
            Span<Complex> overlappingOutput = MemoryMarshal.Cast<double, Complex>(
                overlappingStorage);
            PocketFftComplex.ForwardReal(
                overlappingStorage.AsSpan(0, realInput.Length),
                overlappingOutput);
            Assert.Equal(Hash(expectedReal), Hash(overlappingOutput));
        }
    }

    [Fact(DisplayName = "Complex FFT radix-8 AVX lanes preserve scalar special-value bits")]
    public void ComplexFftRadix8AvxLanesPreserveScalarSpecialValueBits()
    {
        ulong[][] patternSets =
        [
            [
                0x3FF0_0000_0000_0000UL,
                0xBFF0_0000_0000_0000UL,
                0x0000_0000_0000_0000UL,
                0x8000_0000_0000_0000UL,
                0x0000_0000_0000_0001UL,
                0x8000_0000_0000_0001UL,
                0x0010_0000_0000_0000UL,
                0x8010_0000_0000_0000UL
            ],
            [
                0x3FF0_0000_0000_0000UL,
                0xBFF0_0000_0000_0000UL,
                0x7FEF_FFFF_FFFF_FFFFUL,
                0xFFEF_FFFF_FFFF_FFFFUL,
                0x7FF0_0000_0000_0000UL,
                0xFFF0_0000_0000_0000UL,
                0x7FF8_0000_0000_0042UL,
                0xFFF8_0000_0000_0199UL
            ]
        ];
        foreach (int length in new[] { 32, 64, 512 })
        {
            foreach (ulong[] patterns in patternSets)
            {
                var input = new Complex[length];
                for (int index = 0; index < input.Length; index++)
                {
                    input[index] = new Complex(
                        BitConverter.UInt64BitsToDouble(
                            patterns[(2 * index) % patterns.Length]),
                        BitConverter.UInt64BitsToDouble(
                            patterns[((2 * index) + 1) % patterns.Length]));
                }

                Complex[] expectedForward = PocketFftComplex.TransformScalarReference(
                    input,
                    forward: true);
                Complex[] actualForward = PocketFftComplex.Forward(input);
                Assert.Equal(Hash(expectedForward), Hash(actualForward));

                Complex[] expectedInverse = PocketFftComplex.TransformScalarReference(
                    expectedForward,
                    forward: false);
                Complex[] actualInverse = PocketFftComplex.Inverse(actualForward);
                Assert.Equal(Hash(expectedInverse), Hash(actualInverse));
            }
        }
    }

    [Fact(DisplayName = "Complex FFT direct output preserves overlapping storage semantics")]
    public void ComplexFftDirectOutputPreservesOverlappingStorageSemantics()
    {
        foreach (int length in new[] { 8, 64, 512 })
        {
            Complex[] input = BuildInput(length);
            Complex[] expectedInverse = PocketFftComplex.Inverse(input);
            foreach ((int inputOffset, int outputOffset) in new[]
                     {
                         (0, 0),
                         (0, 3),
                         (3, 0)
                     })
            {
                var backing = new Complex[length + 3];
                input.CopyTo(backing, inputOffset);
                PocketFftComplex.Inverse(
                    backing.AsSpan(inputOffset, length),
                    backing.AsSpan(outputOffset, length));
                Assert.Equal(
                    Hash(expectedInverse),
                    Hash(backing.AsSpan(outputOffset, length)));
            }

            double[] realInput = input.Select(static value => value.Real).ToArray();
            Complex[] expectedReal = PocketFftComplex.ForwardReal(realInput);
            foreach ((int inputOffset, int outputOffset) in new[]
                     {
                         (0, 0),
                         (3, 0),
                         (0, 3)
                     })
            {
                var backing = new double[(2 * length) + 3];
                realInput.CopyTo(backing, inputOffset);
                Span<Complex> output = MemoryMarshal.Cast<double, Complex>(
                    backing.AsSpan(outputOffset, 2 * length));
                PocketFftComplex.ForwardReal(
                    backing.AsSpan(inputOffset, length),
                    output);
                Assert.Equal(Hash(expectedReal), Hash(output));
            }
        }
    }

    private static Complex[] BuildInput(int length)
        => Enumerable.Range(0, length)
            .Select(index => new Complex(
                Math.Sin(index * 0.017) + (0.25 * Math.Cos(index * 0.031)),
                Math.Cos(index * 0.013) - (0.125 * Math.Sin(index * 0.029))))
            .ToArray();

    private static string Hash(ReadOnlySpan<Complex> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));
}
