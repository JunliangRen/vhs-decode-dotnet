using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftTerminal128Tests
{
    private delegate void PlanTransform(ReadOnlySpan<Complex> input, Span<Complex> output, bool forward);

    [Theory(DisplayName = "Packet128 terminal SIMD keeps frozen and original generic-plan bits")]
    [InlineData(16_384)]
    [InlineData(32_768)]
    public void PacketKeepsFrozenAndGenericPlanBits(int length)
    {
        var reference = new OriginalPacketReference(length);
        foreach (string pattern in new[] { "random", "signed-zero" })
        foreach (bool forward in new[] { true, false })
        {
            Complex[] input = BuildInput(length, pattern);
            string before = Hash(input);
            Complex[] expected = reference.Transform(input, forward);
            Complex[] actual = forward ? PocketFftComplex.ForwardDucc(input) : PocketFftComplex.InverseDucc(input);
            EqualDefinedBits(expected, actual);
            if (pattern == "random") WindowsFrozenBitOracle.Equal(FrozenHash(length, forward), Hash(actual));
            Complex[] aliased = input.ToArray();
            if (forward) PocketFftComplex.ForwardDuccInPlace(aliased);
            else PocketFftComplex.InverseDuccInPlace(aliased);
            EqualDefinedBits(expected, aliased);
            Assert.Equal(before, Hash(input));
        }
    }

    [Theory(DisplayName = "Rooted128 fixed stages keep generic scalar-bound and offset contracts")]
    [InlineData(16_384)]
    [InlineData(32_768)]
    public void RootedPlanRetainsGuardBoundariesAndOffsets(int rootLength)
    {
        object plan = CreatePlan(128, rootLength);
        PlanTransform original = BindPlan(plan, "Transform");
        PlanTransform packet = BindPlan(plan, "TransformPacket");
        foreach (string pattern in new[] { "random", "signed-zero", "subnormal", "guard-below", "guard-at", "guard-above", "special" })
        foreach (bool forward in new[] { true, false })
        {
            Complex canary = new(13.75, -991.125);
            Complex[] source = Enumerable.Repeat(canary, 132).ToArray();
            BuildInput(128, pattern).CopyTo(source, 2);
            string before = Hash(source);
            Complex[] expected = Enumerable.Repeat(canary, 132).ToArray();
            Complex[] actual = expected.ToArray();
            original(source.AsSpan(2, 128), expected.AsSpan(1, 128), forward);
            packet(source.AsSpan(2, 128), actual.AsSpan(1, 128), forward);
            EqualDefinedBits(expected, actual);
            Assert.Equal(before, Hash(source));
            Complex[] aliased = source.AsSpan(2, 128).ToArray();
            packet(aliased, aliased, forward);
            EqualDefinedBits(expected.AsSpan(1, 128), aliased);
        }
    }

    [Fact(DisplayName = "Packet128 retains active matrix dimensions after larger TLS use")]
    public void PacketRetainsGrowShrinkAndInputSliceOwnership()
    {
        _ = PocketFftComplex.ForwardDucc(BuildInput(1_048_576, "random"));
        var references = new Dictionary<int, OriginalPacketReference>
        {
            [16_384] = new(16_384), [32_768] = new(32_768)
        };
        foreach ((int length, bool forward) in new[] { (16_384, true), (32_768, false), (16_384, false), (32_768, true) })
        {
            Complex[] backing = BuildInput(length + 5, "random");
            string before = Hash(backing);
            ReadOnlySpan<Complex> input = backing.AsSpan(3, length);
            Complex[] expected = references[length].Transform(input, forward);
            Complex[] actual = forward ? PocketFftComplex.ForwardDucc(input) : PocketFftComplex.InverseDucc(input);
            EqualDefinedBits(expected, actual);
            Assert.Equal(before, Hash(backing));
        }
    }

    [Fact(DisplayName = "Ordinary128 keeps the original generic FFT and scalar-reference bits")]
    public void Ordinary128RetainsGenericPathContract()
    {
        PlanTransform generic = BindPlan(CreatePlan(128, 128), "Transform");
        foreach (string pattern in new[] { "random", "signed-zero", "subnormal", "special" })
        foreach (bool forward in new[] { true, false })
        {
            Complex[] input = BuildInput(128, pattern);
            var expected = new Complex[128];
            generic(input, expected, forward);
            Complex[] actual = forward ? PocketFftComplex.Forward(input) : PocketFftComplex.Inverse(input);
            EqualDefinedBits(expected, actual);
            EqualDefinedBits(PocketFftComplex.TransformScalarReference(input, forward), actual);
            Complex[] smallDucc = forward ? PocketFftComplex.ForwardDucc(input) : PocketFftComplex.InverseDucc(input);
            EqualDefinedBits(expected, smallDucc);
        }
    }

    // Freeze the original packet staging/math while deliberately invoking the
    // unchanged generic Plan.Transform, never the new TransformPacket schedule.
    // Using full-root plans is required: standalone small FFT roots can round differently.
    private sealed class OriginalPacketReference
    {
        private readonly int _firstLength;
        private readonly int _secondLength;
        private readonly PlanTransform _first;
        private readonly PlanTransform _second;
        private readonly Complex[] _twiddles;

        internal OriginalPacketReference(int length)
        {
            _firstLength = _secondLength = 1;
            for (int remaining = length; remaining > 1; remaining >>= 1)
            {
                if (_firstLength > _secondLength) _secondLength <<= 1;
                else _firstLength <<= 1;
            }
            _first = BindPlan(CreatePlan(_firstLength, length), "Transform");
            _second = BindPlan(CreatePlan(_secondLength, length), "Transform");
            _twiddles = new Complex[length];
            Type rootType = typeof(PocketFftComplex).GetNestedType("SinCos2PiByN", BindingFlags.NonPublic)!;
            object roots = Activator.CreateInstance(rootType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [length], culture: null)!;
            MethodInfo get = rootType.GetMethod("Get", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            PropertyInfo real = get.ReturnType.GetProperty("Real")!;
            PropertyInfo imaginary = get.ReturnType.GetProperty("Imaginary")!;
            for (int row = 1; row < _secondLength; row++)
            for (int column = 1; column < _firstLength; column++)
            {
                object value = get.Invoke(roots, [row * column])!;
                _twiddles[(row * _firstLength) + column] = new Complex((double)real.GetValue(value)!, (double)imaginary.GetValue(value)!);
            }
        }

        internal Complex[] Transform(ReadOnlySpan<Complex> input, bool forward)
        {
            var stage = new Complex[input.Length];
            var packet = new Complex[_firstLength];
            for (int row = 0; row < _secondLength; row++)
            {
                for (int column = 0; column < _firstLength; column++) packet[column] = input[row + (_secondLength * column)];
                _first(packet, packet, forward);
                for (int column = 0; column < _firstLength; column++)
                {
                    Complex value = packet[column];
                    if (row != 0 && column != 0)
                    {
                        Complex root = _twiddles[(row * _firstLength) + column];
                        value = forward
                            ? new Complex((value.Real * root.Real) + (value.Imaginary * root.Imaginary),
                                (value.Imaginary * root.Real) - (value.Real * root.Imaginary))
                            : new Complex((value.Real * root.Real) - (value.Imaginary * root.Imaginary),
                                (value.Real * root.Imaginary) + (value.Imaginary * root.Real));
                    }
                    stage[row + (_secondLength * column)] = value;
                }
            }
            for (int row = 0; row < _firstLength; row++)
            {
                Span<Complex> values = stage.AsSpan(row * _secondLength, _secondLength);
                _second(values, values, forward);
            }
            var output = new Complex[input.Length];
            for (int row = 0; row < _secondLength; row++)
            for (int column = 0; column < _firstLength; column++)
                output[(row * _firstLength) + column] = stage[(column * _secondLength) + row];
            return output;
        }
    }

    private static object CreatePlan(int length, int rootLength)
        => Activator.CreateInstance(typeof(PocketFftComplex).GetNestedType("Plan", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [length, rootLength], culture: null)!;

    private static PlanTransform BindPlan(object plan, string name)
        => plan.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: [typeof(ReadOnlySpan<Complex>), typeof(Span<Complex>), typeof(bool)], modifiers: null)!
            .CreateDelegate<PlanTransform>(plan);

    private static Complex[] BuildInput(int length, string pattern)
    {
        var input = new Complex[length]; ulong state = 0xD1B54A32D192ED03UL;
        for (int index = 0; index < input.Length; index++)
            input[index] = new Complex(Next(), Next());
        if (pattern == "signed-zero")
            for (int index = 0; index < input.Length; index++)
                input[index] = new Complex((index & 1) == 0 ? 0.0 : -0.0, (index & 2) == 0 ? -0.0 : 0.0);
        if (pattern == "subnormal")
            for (int index = 0; index < input.Length; index++)
                input[index] = new Complex(double.Epsilon * (index % 8), -double.Epsilon * ((index + 3) % 8));
        if (pattern.StartsWith("guard-", StringComparison.Ordinal))
        {
            double cap = double.MaxValue; cap /= 8.0; cap /= 32.0; cap /= 32.0;
            for (int index = 0; index < input.Length; index++)
                input[index] = new Complex(input[index].Real / 1024.0 * (cap / 4.0), input[index].Imaginary / 1024.0 * (cap / 4.0));
            double edge = pattern == "guard-below" ? Math.BitDecrement(cap) : pattern == "guard-above" ? Math.BitIncrement(cap) : cap;
            input[0] = new Complex(edge, -edge);
        }
        if (pattern == "special")
        {
            double[] values = [0.0, -0.0, double.Epsilon, -double.Epsilon, double.MaxValue / 4.0,
                double.PositiveInfinity, double.NegativeInfinity, BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_1234UL),
                BitConverter.UInt64BitsToDouble(0xFFF0_0000_0000_5678UL)];
            for (int index = 0; index < values.Length; index++) input[index] = new Complex(values[index], values[(index + 3) % values.Length]);
        }
        return input;
        double Next()
        {
            state = unchecked(state * 2862933555777941757UL + 3037000493UL);
            return (((state >> 11) / 9007199254740992.0) - 0.5) * 2048.0;
        }
    }

    private static string FrozenHash(int length, bool forward)
        => (length, forward) switch
        {
            (16_384, true) => "1F45F9DF9BC5B4088D14863137AB2A799885784B50CFA41362C86887405994B8",
            (16_384, false) => "EF7D5F9A015144B5BBADD1B89F50E582FC0E7E8FA54663551A1CF4B3802C119C",
            (32_768, true) => "9511E280E2089A9CAC6888B63F2544C88E9E2FF3FBC7D601212F23BAC0397989",
            (32_768, false) => "2016B804F92A14A858CD0294ECA123D21FB39BB7FFEF8EF0642EDF6764D60E1E",
            _ => throw new ArgumentOutOfRangeException(nameof(length))
        };

    private static void EqualDefinedBits(ReadOnlySpan<Complex> expected, ReadOnlySpan<Complex> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        ReadOnlySpan<double> left = MemoryMarshal.Cast<Complex, double>(expected);
        ReadOnlySpan<double> right = MemoryMarshal.Cast<Complex, double>(actual);
        for (int index = 0; index < left.Length; index++)
        {
            // Existing FFT contract permits JIT-dependent NaN payload/sign only.
            if (double.IsNaN(left[index]) && double.IsNaN(right[index])) continue;
            Assert.Equal(BitConverter.DoubleToUInt64Bits(left[index]), BitConverter.DoubleToUInt64Bits(right[index]));
        }
    }

    private static string Hash(Complex[] values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
}
