using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppSos32InteropTests
{
    private static readonly SosSection[] DirectSections =
    [
        new SosSection(0.4, 0.15, -0.025, 2.0, -0.65, 0.12),
        new SosSection(0.75, -0.2, 0.05, 1.25, -0.35, 0.08)
    ];

    private static readonly SosSection[] PoolSections =
    [
        new SosSection(0.2, 0.1, 0.03, 1.0, -0.42, 0.11),
        new SosSection(0.35, -0.08, 0.02, 1.0, -0.27, 0.07)
    ];

    private static readonly float[] InitialState = [0.125F, -0.0625F, 0.03125F, -0.015625F];

    [Fact(DisplayName = "Managed SOS32 ABI row matches six native floats")]
    public void ManagedSos32AbiRowMatchesNativeLayout()
    {
        Assert.Equal(24, Marshal.SizeOf<IppSos32Section>());
        Assert.Equal(0, Marshal.OffsetOf<IppSos32Section>(nameof(IppSos32Section.B0)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<IppSos32Section>(nameof(IppSos32Section.B1)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<IppSos32Section>(nameof(IppSos32Section.B2)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<IppSos32Section>(nameof(IppSos32Section.A0)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<IppSos32Section>(nameof(IppSos32Section.A1)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<IppSos32Section>(nameof(IppSos32Section.A2)).ToInt32());
    }

    [Fact(DisplayName = "SOS32 validates shapes before probing native runtime")]
    public void Sos32ValidatesBeforeNativeProbe()
    {
        Assert.Throws<ArgumentException>(() => new IppSos32([]));
        Assert.Throws<ArgumentException>(
            () => new IppSos32([new SosSection(1, 0, 0, double.Epsilon, 0, 0)]));
        Assert.Throws<ArgumentException>(() => new IppSos32(DirectSections, [0.0F]));
        Assert.Null(IppSos32FilterPool.TryCreate(null));
        Assert.Null(IppSos32FilterPool.TryCreate([]));
        Assert.Null(IppSos32FilterPool.TryCreate(DirectSections));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IppSos32FilterPool.TryCreate(PoolSections, 0));
    }

    [Fact(DisplayName = "IPP SOS32 matches scalar float output and final state")]
    public void IppSos32MatchesScalarOutputAndState()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        float[] input = BuildFloatInput(4_096);
        (float[] expected, float[] expectedState) = ScalarSos32(
            DirectSections,
            input,
            InitialState);
        var actual = new float[input.Length];

        using var filter = new IppSos32(DirectSections, InitialState);
        Assert.Equal(DirectSections.Length, filter.SectionCount);
        Assert.Equal(InitialState.Length, filter.StateLength);
        filter.Process(input, actual);

        AssertClose(expected, actual, 2.0e-5F);
        AssertClose(expectedState, filter.GetState(), 2.0e-5F);
    }

    [Fact(DisplayName = "IPP SOS32 preserves split, reset, state and in-place semantics")]
    public void IppSos32PreservesStateControlsAndInPlaceSemantics()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        float[] input = BuildFloatInput(2_057);
        (float[] expectedInitial, float[] expectedState) = ScalarSos32(
            DirectSections,
            input,
            InitialState);
        (float[] expectedZero, _) = ScalarSos32(
            DirectSections,
            input,
            new float[InitialState.Length]);
        var split = new float[input.Length];

        using var filter = new IppSos32(DirectSections, InitialState);
        const int SplitPoint = 733;
        filter.Process(input.AsSpan(0, SplitPoint), split.AsSpan(0, SplitPoint));
        filter.Process(input.AsSpan(SplitPoint), split.AsSpan(SplitPoint));
        AssertClose(expectedInitial, split, 2.0e-5F);
        AssertClose(expectedState, filter.GetState(), 2.0e-5F);

        filter.Reset();
        Assert.Equal(new float[filter.StateLength], filter.GetState());
        float[] inPlace = input.ToArray();
        filter.ProcessInPlace(inPlace);
        AssertClose(expectedZero, inPlace, 2.0e-5F);

        filter.SetState(InitialState);
        inPlace = input.ToArray();
        filter.ProcessInPlace(inPlace);
        AssertClose(expectedInitial, inPlace, 2.0e-5F);

        var overlap = new float[32];
        Assert.Throws<ArgumentException>(
            () => filter.Process(overlap.AsSpan(0, 16), overlap.AsSpan(1, 16)));
    }

    [Fact(DisplayName = "IPP SOS32 pool is deterministic in parallel and bounds retained contexts")]
    public void IppSos32PoolIsDeterministicAndBounded()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildDoubleInput(8_192);
        using IppSos32FilterPool pool = IppSos32FilterPool.TryCreate(
            PoolSections,
            maximumRetainedContexts: 4)!;
        var hashes = new string[24];
        Parallel.For(
            0,
            hashes.Length,
            new ParallelOptions { MaxDegreeOfParallelism = 12 },
            index =>
            {
                var output = new float[input.Length];
                SosFilter.ApplyForwardBackwardFloat32ToSingle(
                    PoolSections,
                    input,
                    output,
                    ippFilter: pool);
                hashes[index] = Convert.ToHexString(
                    SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan())));
            });

        Assert.Single(hashes.Distinct(StringComparer.Ordinal));
        Assert.InRange(pool.RetainedContextCount, 1, 4);
        Assert.True(pool.CreatedContextCount >= pool.RetainedContextCount);
    }

    [Fact(DisplayName = "IPP SOS32 forward-backward pool stays numerically close to managed float32")]
    public void IppSos32PoolStaysCloseToManagedFloat32()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        double[] input = BuildDoubleInput(16_384);
        double[] expected = SosFilter.ApplyForwardBackwardFloat32(PoolSections, input);
        var actual = new double[input.Length];
        using IppSos32FilterPool pool = IppSos32FilterPool.TryCreate(PoolSections)!;
        SosFilter.ApplyForwardBackwardFloat32(
            PoolSections,
            input,
            actual,
            ippFilter: pool);

        AssertClose(expected, actual, 5.0e-5);
    }

    [Fact(DisplayName = "Disposed SOS32 context and pool reject further work")]
    public void DisposedSos32ObjectsRejectCalls()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        var filter = new IppSos32(DirectSections);
        filter.Dispose();
        filter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => filter.Process([1.0F], new float[1]));
        Assert.Throws<ObjectDisposedException>(() => filter.Reset());
        Assert.Throws<ObjectDisposedException>(() => filter.GetState());
        Assert.Throws<ObjectDisposedException>(() => filter.SetState(new float[filter.StateLength]));

        IppSos32FilterPool pool = IppSos32FilterPool.TryCreate(PoolSections)!;
        pool.Dispose();
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => pool.ApplyForwardBackwardInPlace(new float[32]));
    }

    private static float[] BuildFloatInput(int length)
        => BuildDoubleInput(length).Select(value => (float)value).ToArray();

    private static double[] BuildDoubleInput(int length)
        => Enumerable.Range(0, length)
            .Select(index =>
                (0.6 * Math.Sin(index * 0.017))
                + (0.25 * Math.Cos(index * 0.031))
                + (((index * 37) % 101) * 0.0002))
            .ToArray();

    private static (float[] Output, float[] State) ScalarSos32(
        IReadOnlyList<SosSection> sections,
        ReadOnlySpan<float> input,
        ReadOnlySpan<float> initialState)
    {
        float[] state = initialState.ToArray();
        float[] output = input.ToArray();
        for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            SosSection source = sections[sectionIndex];
            float a0 = (float)source.A0;
            float b0 = (float)source.B0 / a0;
            float b1 = (float)source.B1 / a0;
            float b2 = (float)source.B2 / a0;
            float a1 = (float)source.A1 / a0;
            float a2 = (float)source.A2 / a0;
            int stateOffset = sectionIndex * 2;
            float z1 = state[stateOffset];
            float z2 = state[stateOffset + 1];
            for (int sample = 0; sample < output.Length; sample++)
            {
                float value = output[sample];
                float filtered = (b0 * value) + z1;
                z1 = (b1 * value) - (a1 * filtered) + z2;
                z2 = (b2 * value) - (a2 * filtered);
                output[sample] = filtered;
            }

            state[stateOffset] = z1;
            state[stateOffset + 1] = z2;
        }

        return (output, state);
    }

    private static void AssertClose(
        ReadOnlySpan<float> expected,
        ReadOnlySpan<float> actual,
        float relativeTolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            float tolerance = relativeTolerance * Math.Max(1.0F, Math.Abs(expected[index]));
            Assert.InRange(Math.Abs(expected[index] - actual[index]), 0.0F, tolerance);
        }
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
}
