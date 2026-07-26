using VHSDecode.Core.CommandLine;
using VHSDecode.Core.Decode;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Tbc;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsVSyncLevelRefinerCurrentTests
{
    [Fact(DisplayName = "Current VHS VSync level refiner matches the PR 341 oracle")]
    public void CurrentVhsVSyncLevelRefinerMatchesPr341Oracle()
    {
        double[] vSync = BuildOracleVSync();
        double[] original = vSync.ToArray();
        var refiner = new VhsVSyncLevelRefiner();

        VhsVSyncLevelRefinementResult result = refiner.Refine(
            vSync,
            originalSyncTip: -40.0,
            originalBlank: 0.0);

        Assert.Equal(original, vSync);
        Assert.Equal(127, result.SyncSampleCount);
        Assert.Equal(124, result.BlankSampleCount);
        Assert.Equal(
            unchecked((long)0xC043FE5FF77D2735UL),
            BitConverter.DoubleToInt64Bits(result.SyncTipLevel));
        Assert.Equal(
            unchecked((long)0xBF9987D1B17A1603UL),
            BitConverter.DoubleToInt64Bits(result.BlankLevel));
    }

    [Theory(DisplayName = "Current VHS VSync fastmath reductions match Numba")]
    [MemberData(nameof(ReductionCases))]
    public void CurrentVhsVSyncFastMathReductionsMatchNumba(
        int length,
        long expectedMeanBits,
        long expectedVarianceBits)
    {
        double[] values = BuildReductionValues(length);

        (double mean, double variance) =
            VhsVSyncLevelRefiner.MeanVarianceInUpstreamOrder(values);

        Assert.Equal(expectedMeanBits, BitConverter.DoubleToInt64Bits(mean));
        Assert.Equal(expectedVarianceBits, BitConverter.DoubleToInt64Bits(variance));
    }

    [Fact(DisplayName = "Current VHS VSync level refiner preserves upstream boundary gates")]
    public void CurrentVhsVSyncLevelRefinerPreservesUpstreamBoundaryGates()
    {
        var refiner = new VhsVSyncLevelRefiner();

        VhsVSyncLevelRefinementResult empty = refiner.Refine([], -40.0, 0.0);
        VhsVSyncLevelRefinementResult tooFew = refiner.Refine(
            [-40.0, -39.9, 0.0, 0.1, -40.1, -0.1],
            -40.0,
            0.0);
        VhsVSyncLevelRefinementResult badAmplitude = refiner.Refine(
            [
                .. Enumerable.Repeat(-28.0, 64),
                .. Enumerable.Repeat(-12.0, 64)
            ],
            -40.0,
            0.0);
        VhsVSyncLevelRefinementResult zeroVariance = refiner.Refine(
            [
                .. Enumerable.Repeat(-40.25, 80),
                .. Enumerable.Repeat(0.25, 80)
            ],
            -40.0,
            0.0);

        Assert.Equal(new VhsVSyncLevelRefinementResult(-40.0, 0.0, 0, 0), empty);
        Assert.Equal(new VhsVSyncLevelRefinementResult(-40.0, 0.0, 3, 3), tooFew);
        Assert.Equal(new VhsVSyncLevelRefinementResult(-40.0, 0.0, 64, 64), badAmplitude);
        Assert.Equal(80, zeroVariance.SyncSampleCount);
        Assert.Equal(80, zeroVariance.BlankSampleCount);
        Assert.Equal(
            unchecked((long)0xC0441FFFFFF9742CUL),
            BitConverter.DoubleToInt64Bits(zeroVariance.SyncTipLevel));
        Assert.Equal(
            unchecked((long)0x3FCFFFFFF9742BB2UL),
            BitConverter.DoubleToInt64Bits(zeroVariance.BlankLevel));
    }

    [Fact(DisplayName = "Current VHS VSync field window follows Python slice semantics")]
    public void CurrentVhsVSyncFieldWindowFollowsPythonSliceSemantics()
    {
        var field = new double[400];
        for (int index = 0; index < field.Length; index++)
        {
            field[index] = index % 2 == 0
                ? -40.0 + ((index % 7) * 0.01)
                : (index % 5) * 0.01;
        }

        var refiner = new VhsVSyncLevelRefiner();
        VhsVSyncLevelRefinementResult fieldResult = refiner.RefineField(
            field,
            line0Location: 10.5,
            meanLineLength: 20.0,
            originalSyncTip: -40.0,
            originalBlank: 0.0);
        VhsVSyncLevelRefinementResult directResult = refiner.Refine(
            field.AsSpan(30, 170),
            originalSyncTip: -40.0,
            originalBlank: 0.0);
        VhsVSyncLevelRefinementResult wrappedEmpty = refiner.RefineField(
            field,
            line0Location: -40.5,
            meanLineLength: 20.0,
            originalSyncTip: -40.0,
            originalBlank: 0.0);

        Assert.Equal(directResult, fieldResult);
        Assert.Equal(new VhsVSyncLevelRefinementResult(-40.0, 0.0, 0, 0), wrappedEmpty);
        Assert.Equal(7, VhsVSyncLevelRefiner.NormalizePythonSliceIndex(-3, 10));
        Assert.Equal(0, VhsVSyncLevelRefiner.NormalizePythonSliceIndex(-20, 10));
        Assert.Equal(10, VhsVSyncLevelRefiner.NormalizePythonSliceIndex(12, 10));
    }

    [Fact(DisplayName = "Current VHS VSync level refiner reuses its workspace")]
    public void CurrentVhsVSyncLevelRefinerReusesItsWorkspace()
    {
        double[] vSync = BuildLargeVSync(100_000);
        var refiner = new VhsVSyncLevelRefiner();
        _ = refiner.Refine(vSync, -40.0, 0.0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        VhsVSyncLevelRefinementResult result = refiner.Refine(vSync, -40.0, 0.0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.SyncSampleCount > 0);
        Assert.True(result.BlankSampleCount > 0);
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    [Fact(DisplayName = "Current VHS VSync refinement follows saved-level gating")]
    public void CurrentVhsVSyncRefinementFollowsSavedLevelGating()
    {
        using DecodeSession current = CreateSession("--compat-version", "current");
        using DecodeSession saved = CreateSession(
            "--compat-version",
            "current",
            "--use_saved_levels");
        using DecodeSession v040 = CreateSession("--compat-version", "v0.4.0");

        Assert.True(current.TbcFieldDecoder.CurrentVhsVSyncLevelRefinementEnabled);
        Assert.False(saved.TbcFieldDecoder.CurrentVhsVSyncLevelRefinementEnabled);
        Assert.False(v040.TbcFieldDecoder.CurrentVhsVSyncLevelRefinementEnabled);
    }

    [Fact(DisplayName = "Current VHS VSync back-porch assignment matches pinned upstream")]
    public void CurrentVhsVSyncBackPorchAssignmentMatchesPinnedUpstream()
    {
        using DecodeSession session = CreateSession(
            "--compat-version",
            "current",
            "--ire0_adjust",
            "backporch,hsync");
        var current = new VideoOutputConverter(
            ire0: 1_000.0,
            hzIre: 10.0,
            outputZero: 256,
            vsyncIre: -40.0,
            outputScale: 300.0);
        var refinement = new VhsVSyncLevelRefinementResult(
            SyncTipLevel: -40.0,
            BlankLevel: 0.0,
            SyncSampleCount: 100,
            BlankSampleCount: 100);

        VideoOutputConverter adjusted =
            session.TbcFieldDecoder.ApplyCurrentVhsVSyncLevels(refinement, current);

        Assert.Equal(-40.0, adjusted.Ire0);
        Assert.Equal(1.0, adjusted.HzIre);
    }

    public static TheoryData<int, long, long> ReductionCases => new()
    {
        {
            3,
            unchecked((long)0x40934564151348C4UL),
            unchecked((long)0x3FEA1EE4D16D08A4UL)
        },
        {
            15,
            unchecked((long)0x409347E49BEFF309UL),
            unchecked((long)0x3FEF70B08C2FBBF3UL)
        },
        {
            16,
            unchecked((long)0x4093484587EC035AUL),
            unchecked((long)0x3FF0E33E54711E10UL)
        },
        {
            17,
            unchecked((long)0x4093482B80DBD16BUL),
            unchecked((long)0x3FF00F46564918A7UL)
        },
        {
            69,
            unchecked((long)0x409349A80E6158E0UL),
            unchecked((long)0x3FF40E3F2FF71F88UL)
        },
        {
            127,
            unchecked((long)0x409349E420EA36DEUL),
            unchecked((long)0x3FF3D8CAC7AB24F6UL)
        },
        {
            189,
            unchecked((long)0x409349ED45A90751UL),
            unchecked((long)0x3FF3BD1B9EC4F8E9UL)
        },
        {
            252,
            unchecked((long)0x409349F2F53E0170UL),
            unchecked((long)0x3FF39BF4CDA42FF5UL)
        }
    };

    private static DecodeSession CreateSession(params string[] options)
    {
        string[] arguments = [.. options, "input.s16", "output"];
        ParsedCommand command = new CommandLineParser().Parse(CliSpecs.Vhs, arguments);
        return DecodeSessionFactory.Create(command);
    }

    private static double[] BuildOracleVSync()
    {
        const int Length = 257;
        var vSync = new double[Length];
        for (int index = 0; index < vSync.Length; index++)
        {
            vSync[index] = index % 4 is 0 or 1
                ? -40.0
                    + (((((index * 37) % 23) - 11) * 0.125))
                    + (index * 1e-7)
                : (((index * 29) % 19) - 9) * 0.1
                    - (index * 2e-7);
        }

        vSync[12] = -25.0;
        vSync[48] = -24.0;
        vSync[87] = -15.0;
        vSync[131] = -14.0;
        vSync[206] = -16.0;
        vSync[231] = -17.0;
        return vSync;
    }

    private static double[] BuildReductionValues(int length)
    {
        var values = new double[length];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = 1_234.5
                + (((((index * 47) % 31) - 15) * 0.123456789))
                + (index * 1e-8);
        }

        return values;
    }

    private static double[] BuildLargeVSync(int length)
    {
        var values = new double[length];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = (index & 1) == 0
                ? -40.0 + ((((index * 37) % 23) - 11) * 0.01)
                : ((((index * 29) % 19) - 9) * 0.01);
        }

        return values;
    }
}
