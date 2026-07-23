using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppIirSosInteropTests
{
    private static readonly double[] IirNumerator = [0.25, -0.075, 0.0375];
    private static readonly double[] IirDenominator = [2.0, -0.9, 0.28, -0.035];
    private static readonly double[] IirInitialState = [0.125, -0.0625, 0.03125];

    private static readonly SosSection[] SosSections =
    [
        new SosSection(0.4, 0.15, -0.025, 2.0, -0.65, 0.12),
        new SosSection(0.75, -0.2, 0.05, 1.25, -0.35, 0.08)
    ];

    private static readonly double[] SosInitialState = [0.125, -0.0625, 0.03125, -0.015625];

    [Fact(DisplayName = "Managed SOS ABI row matches six native doubles")]
    public void ManagedSosAbiRowMatchesNativeLayout()
    {
        Assert.Equal(48, Marshal.SizeOf<IppSos64Section>());
        Assert.Equal(0, Marshal.OffsetOf<IppSos64Section>(nameof(IppSos64Section.B0)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<IppSos64Section>(nameof(IppSos64Section.B1)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<IppSos64Section>(nameof(IppSos64Section.B2)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<IppSos64Section>(nameof(IppSos64Section.A0)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<IppSos64Section>(nameof(IppSos64Section.A1)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<IppSos64Section>(nameof(IppSos64Section.A2)).ToInt32());
    }

    [Fact(DisplayName = "IIR and SOS constructors validate shapes before probing native runtime")]
    public void FilterConstructorsValidateBeforeNativeProbe()
    {
        Assert.Throws<ArgumentException>(() => new IppIir64([], [1.0, 0.0]));
        Assert.Throws<ArgumentException>(() => new IppIir64([1.0, 0.0], []));
        Assert.Throws<ArgumentException>(() => new IppIir64([1.0], [1.0]));
        Assert.Throws<ArgumentException>(() => new IppIir64([1.0, 0.0], [0.0, 1.0]));
        Assert.Throws<ArgumentException>(
            () => new IppIir64([1.0, 0.0], [1.0, 0.0], [0.0, 0.0]));
        Assert.Throws<ArgumentException>(() => new IppSos64([]));
        Assert.Throws<ArgumentException>(
            () => new IppSos64([new SosSection(1, 0, 0, 0, 0, 0)]));
        Assert.Throws<ArgumentException>(() => new IppSos64(SosSections, [0.0]));
    }

    [Fact(DisplayName = "IPP direct IIR matches managed scalar output and final state")]
    public void IppDirectIirMatchesManagedScalarOutputAndState()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildInput(4_096);
        (double[] expected, double[] expectedState) = ScalarIir(
            IirNumerator,
            IirDenominator,
            input,
            IirInitialState);
        var actual = new double[input.Length];

        using var filter = new IppIir64(IirNumerator, IirDenominator, IirInitialState);
        Assert.Equal(3, filter.Order);
        Assert.Equal(3, filter.StateLength);
        filter.Process(input, actual);

        AssertClose(expected, actual, 5e-13);
        AssertClose(expectedState, filter.GetState(), 5e-13);
    }

    [Fact(DisplayName = "IPP direct IIR preserves split state, reset, set-state and in-place semantics")]
    public void IppDirectIirPreservesStateControlsAndInPlaceSemantics()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildInput(2_057);
        (double[] expectedInitial, double[] expectedInitialState) = ScalarIir(
            IirNumerator,
            IirDenominator,
            input,
            IirInitialState);
        (double[] expectedZero, _) = ScalarIir(
            IirNumerator,
            IirDenominator,
            input,
            new double[IirInitialState.Length]);
        var split = new double[input.Length];

        using var filter = new IppIir64(IirNumerator, IirDenominator, IirInitialState);
        const int SplitPoint = 733;
        filter.Process(input.AsSpan(0, SplitPoint), split.AsSpan(0, SplitPoint));
        filter.Process(input.AsSpan(SplitPoint), split.AsSpan(SplitPoint));
        AssertClose(expectedInitial, split, 5e-13);
        AssertClose(expectedInitialState, filter.GetState(), 5e-13);

        double[] stateBeforeEmpty = filter.GetState();
        filter.Process(ReadOnlySpan<double>.Empty, Span<double>.Empty);
        AssertClose(stateBeforeEmpty, filter.GetState(), 0.0);

        filter.Reset();
        Assert.Equal(new double[filter.StateLength], filter.GetState());
        double[] inPlace = input.ToArray();
        filter.ProcessInPlace(inPlace);
        AssertClose(expectedZero, inPlace, 5e-13);

        filter.SetState(IirInitialState);
        inPlace = input.ToArray();
        filter.ProcessInPlace(inPlace);
        AssertClose(expectedInitial, inPlace, 5e-13);

        var overlap = new double[32];
        Assert.Throws<ArgumentException>(
            () => filter.Process(overlap.AsSpan(0, 16), overlap.AsSpan(1, 16)));
        Assert.Throws<ArgumentException>(
            () => filter.Process(input.AsSpan(0, 16), new double[15]));
    }

    [Fact(DisplayName = "IPP SOS matches managed scalar cascade output and final state")]
    public void IppSosMatchesManagedScalarOutputAndState()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildInput(4_096);
        (double[] expected, double[] expectedState) = ScalarSos(
            SosSections,
            input,
            SosInitialState);
        var actual = new double[input.Length];

        using var filter = new IppSos64(SosSections, SosInitialState);
        Assert.Equal(SosSections.Length, filter.SectionCount);
        Assert.Equal(SosInitialState.Length, filter.StateLength);
        filter.Process(input, actual);

        AssertClose(expected, actual, 5e-13);
        AssertClose(expectedState, filter.GetState(), 5e-13);
    }

    [Fact(DisplayName = "IPP SOS preserves split state, reset, set-state and in-place semantics")]
    public void IppSosPreservesStateControlsAndInPlaceSemantics()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildInput(2_057);
        (double[] expectedInitial, double[] expectedInitialState) = ScalarSos(
            SosSections,
            input,
            SosInitialState);
        (double[] expectedZero, _) = ScalarSos(
            SosSections,
            input,
            new double[SosInitialState.Length]);
        var split = new double[input.Length];

        using var filter = new IppSos64(SosSections, SosInitialState);
        const int SplitPoint = 733;
        filter.Process(input.AsSpan(0, SplitPoint), split.AsSpan(0, SplitPoint));
        filter.Process(input.AsSpan(SplitPoint), split.AsSpan(SplitPoint));
        AssertClose(expectedInitial, split, 5e-13);
        AssertClose(expectedInitialState, filter.GetState(), 5e-13);

        filter.Reset();
        Assert.Equal(new double[filter.StateLength], filter.GetState());
        double[] inPlace = input.ToArray();
        filter.ProcessInPlace(inPlace);
        AssertClose(expectedZero, inPlace, 5e-13);

        filter.SetState(SosInitialState);
        inPlace = input.ToArray();
        filter.ProcessInPlace(inPlace);
        AssertClose(expectedInitial, inPlace, 5e-13);

        var overlap = new double[32];
        Assert.Throws<ArgumentException>(
            () => filter.Process(overlap.AsSpan(0, 16), overlap.AsSpan(1, 16)));
    }

    [Fact(DisplayName = "Separate IPP IIR and SOS contexts remain deterministic in parallel")]
    public void SeparateFilterContextsRemainDeterministicInParallel()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildInput(1_024);
        (double[] expectedIir, double[] expectedIirState) = ScalarIir(
            IirNumerator,
            IirDenominator,
            input,
            IirInitialState);
        (double[] expectedSos, double[] expectedSosState) = ScalarSos(
            SosSections,
            input,
            SosInitialState);

        Parallel.For(
            0,
            12,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            _ =>
            {
                using var iir = new IppIir64(IirNumerator, IirDenominator, IirInitialState);
                using var sos = new IppSos64(SosSections, SosInitialState);
                var iirOutput = new double[input.Length];
                var sosOutput = new double[input.Length];
                iir.Process(input, iirOutput);
                sos.Process(input, sosOutput);
                AssertClose(expectedIir, iirOutput, 5e-13);
                AssertClose(expectedIirState, iir.GetState(), 5e-13);
                AssertClose(expectedSos, sosOutput, 5e-13);
                AssertClose(expectedSosState, sos.GetState(), 5e-13);
            });
    }

    [Fact(DisplayName = "One IPP IIR context serializes concurrent stateful blocks")]
    public void OneIirContextSerializesConcurrentStatefulBlocks()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int CallCount = 10;
        double[] input = BuildInput(127);
        string[] sequentialHashes = new string[CallCount];
        double[] sequentialState;
        using (var sequential = new IppIir64(IirNumerator, IirDenominator))
        {
            for (int index = 0; index < CallCount; index++)
            {
                var output = new double[input.Length];
                sequential.Process(input, output);
                sequentialHashes[index] = Hash(output);
            }

            sequentialState = sequential.GetState();
        }

        var concurrentHashes = new string[CallCount];
        using var concurrent = new IppIir64(IirNumerator, IirDenominator);
        Parallel.For(
            0,
            CallCount,
            new ParallelOptions { MaxDegreeOfParallelism = 5 },
            index =>
            {
                var output = new double[input.Length];
                concurrent.Process(input, output);
                concurrentHashes[index] = Hash(output);
            });

        Assert.Equal(
            sequentialHashes.Order(StringComparer.Ordinal),
            concurrentHashes.Order(StringComparer.Ordinal));
        AssertClose(sequentialState, concurrent.GetState(), 0.0);
    }

    [Fact(DisplayName = "One IPP SOS context serializes concurrent stateful blocks")]
    public void OneSosContextSerializesConcurrentStatefulBlocks()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int CallCount = 10;
        double[] input = BuildInput(127);
        var sequentialOutputs = new double[CallCount][];
        double[] sequentialState;
        using (var sequential = new IppSos64(SosSections))
        {
            for (int index = 0; index < CallCount; index++)
            {
                var output = new double[input.Length];
                sequential.Process(input, output);
                sequentialOutputs[index] = output;
            }

            sequentialState = sequential.GetState();
        }

        var concurrentOutputs = new double[CallCount][];
        using var concurrent = new IppSos64(SosSections);
        Parallel.For(
            0,
            CallCount,
            new ParallelOptions { MaxDegreeOfParallelism = 5 },
            index =>
            {
                var output = new double[input.Length];
                concurrent.Process(input, output);
                concurrentOutputs[index] = output;
            });

        double[][] orderedSequential = sequentialOutputs.OrderBy(output => output[0]).ToArray();
        double[][] orderedConcurrent = concurrentOutputs.OrderBy(output => output[0]).ToArray();
        for (int index = 0; index < orderedSequential.Length; index++)
        {
            AssertClose(orderedSequential[index], orderedConcurrent[index], 5e-13);
        }

        AssertClose(sequentialState, concurrent.GetState(), 5e-13);
    }

    [Fact(DisplayName = "Disposed IPP filter contexts reject state and process calls")]
    public void DisposedFilterContextsRejectCalls()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        var iir = new IppIir64(IirNumerator, IirDenominator);
        var sos = new IppSos64(SosSections);
        iir.Dispose();
        iir.Dispose();
        sos.Dispose();
        sos.Dispose();

        Assert.Throws<ObjectDisposedException>(() => iir.Process([1.0], new double[1]));
        Assert.Throws<ObjectDisposedException>(() => iir.Reset());
        Assert.Throws<ObjectDisposedException>(() => iir.GetState());
        Assert.Throws<ObjectDisposedException>(() => iir.SetState(new double[iir.StateLength]));
        Assert.Throws<ObjectDisposedException>(() => sos.Process([1.0], new double[1]));
        Assert.Throws<ObjectDisposedException>(() => sos.Reset());
        Assert.Throws<ObjectDisposedException>(() => sos.GetState());
        Assert.Throws<ObjectDisposedException>(() => sos.SetState(new double[sos.StateLength]));
    }

    private static double[] BuildInput(int length)
        => Enumerable.Range(0, length)
            .Select(index =>
                (0.6 * Math.Sin(index * 0.017))
                + (0.25 * Math.Cos(index * 0.031))
                + (((index * 37) % 101) * 0.0002))
            .ToArray();

    private static (double[] Output, double[] State) ScalarIir(
        ReadOnlySpan<double> numerator,
        ReadOnlySpan<double> denominator,
        ReadOnlySpan<double> input,
        ReadOnlySpan<double> initialState)
    {
        int coefficientCount = Math.Max(numerator.Length, denominator.Length);
        double a0 = denominator[0];
        var b = new double[coefficientCount];
        var a = new double[coefficientCount];
        for (int index = 0; index < numerator.Length; index++)
        {
            b[index] = numerator[index] / a0;
        }

        for (int index = 0; index < denominator.Length; index++)
        {
            a[index] = denominator[index] / a0;
        }

        double[] state = initialState.ToArray();
        var output = new double[input.Length];
        for (int sample = 0; sample < input.Length; sample++)
        {
            double x = input[sample];
            double y = (b[0] * x) + state[0];
            for (int index = 1; index < state.Length; index++)
            {
                state[index - 1] = (b[index] * x) + state[index] - (a[index] * y);
            }

            state[^1] = (b[^1] * x) - (a[^1] * y);
            output[sample] = y;
        }

        return (output, state);
    }

    private static (double[] Output, double[] State) ScalarSos(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<double> input,
        ReadOnlySpan<double> initialState)
    {
        double[] state = initialState.ToArray();
        var output = new double[input.Length];
        for (int sample = 0; sample < input.Length; sample++)
        {
            double value = input[sample];
            for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
            {
                SosSection section = sections[sectionIndex].Normalize();
                int stateOffset = sectionIndex * 2;
                double filtered = (section.B0 * value) + state[stateOffset];
                state[stateOffset] = (section.B1 * value)
                    - (section.A1 * filtered)
                    + state[stateOffset + 1];
                state[stateOffset + 1] = (section.B2 * value) - (section.A2 * filtered);
                value = filtered;
            }

            output[sample] = value;
        }

        return (output, state);
    }

    private static void AssertClose(
        ReadOnlySpan<double> expected,
        ReadOnlySpan<double> actual,
        double relativeTolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            double tolerance = relativeTolerance * Math.Max(1.0, Math.Abs(expected[index]));
            Assert.InRange(Math.Abs(expected[index] - actual[index]), 0.0, tolerance);
        }
    }

    private static string Hash(ReadOnlySpan<double> values)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values)));
}
