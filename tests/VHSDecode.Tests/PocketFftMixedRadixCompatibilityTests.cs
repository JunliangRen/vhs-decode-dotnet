using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VHSDecode.Core.Dsp;
using Xunit;

namespace VHSDecode.Tests;

public sealed class PocketFftMixedRadixCompatibilityTests
{
    [Theory(DisplayName = "Mixed-radix complex plan matches SciPy")]
    [InlineData(12, "A8A217DCBBB0CCC95331D571D21701B86D774EA554EBE955167D205463A56C36")]
    [InlineData(15, "42222792CD2AB425952620DBCD0569F771FF6213C47DC3BD75199CDE250EA9F4")]
    [InlineData(30, "1037B1869CB46236BA8348168F1B38882C077C0A4F39D4DBE1E2FF9B237AB9A7")]
    [InlineData(60, "B352C7FF9D66B8A202CFD8F0401B02226CDBEAC9A5F0D291972C1458F90DF2F2")]
    [InlineData(120, "3B71682106CC3D612546BDF9C97F99E1997ACCC3202A14475192E9D3DD04DEA7")]
    [InlineData(240, "A3154BF0EACC9560D2B3B8D5FABC93F5957FDB89F9A86466DB63CF55D330FEA9")]
    [InlineData(480, "4C78D73086B52B45D6BD4B4116DB4B1F5CBFB64E427ABA854BBB46BD90C01A5F")]
    [InlineData(960, "73BA908EFE693828BFC7C1B1B976CA49CFE80792171798B2E31506D6EBD99351")]
    [InlineData(1_920, "4A912CB8BE7B23901C0ACC26F6121D8951FE57B5C07E7141E7C9041AA3D7EB9A")]
    [InlineData(3_840, "1417557A34805FF35656391DAFDDB7EBBE3D6DD93E083674FB91283DD12DE087")]
    [InlineData(7_680, "C1E58DDAC0E1CD224E6D1DAF16EE636308315F75F8AB4809B3F9694A8E57ACE0")]
    [InlineData(15_000, "9F4BE2C9B9D38330F48F6BB12CB20AF45CA5D42F41D7CE69CE00071C5E482066")]
    [InlineData(30_000, "2C1F8F74ACCCFC32F69AE805F8E4A6C290C400FA98F097181636E1A183822A29")]
    [InlineData(60_000, "D11A4E174C712CE93DC8B7D1BAF096309CBD806BFD21282CDF0187C63B0269FB")]
    public void MixedRadixComplexPlanMatchesScipy(
        int length,
        string expectedSha256)
    {
        float[] values = DeterministicInput(2 * length);
        Complex32[] input = Enumerable.Range(0, length)
            .Select(index => new Complex32(
                values[2 * index],
                values[(2 * index) + 1]))
            .ToArray();

        Complex32[] spectrum =
            PocketFftComplex32.ForwardAnyLengthDucc(input);

        Assert.Equal(
            expectedSha256,
            Sha256(MemoryMarshal.AsBytes(spectrum.AsSpan())));
    }

    [Fact(DisplayName = "Parallel mixed-radix packets match serial FFT bit for bit")]
    public void ParallelMixedRadixPacketsMatchSerialFftBitForBit()
    {
        const int Length = 120_000;
        float[] values = DeterministicInput(2 * Length);
        Complex32[] input = Enumerable.Range(0, Length)
            .Select(index => new Complex32(
                values[2 * index],
                values[(2 * index) + 1]))
            .ToArray();

        Complex32[] expectedForward =
            PocketFftComplex32.ForwardAnyLengthDucc(input);
        Complex32[] actualForward =
            PocketFftComplex32.ForwardAnyLengthDucc(
                input,
                workerThreads: 4);
        Assert.True(
            MemoryMarshal.AsBytes(expectedForward.AsSpan())
                .SequenceEqual(
                    MemoryMarshal.AsBytes(actualForward.AsSpan())));

        Complex32[] expectedBackward =
            PocketFftComplex32.BackwardAnyLengthDucc(expectedForward);
        Complex32[] actualBackward =
            PocketFftComplex32.BackwardAnyLengthDucc(
                expectedForward,
                workerThreads: 4);
        Assert.True(
            MemoryMarshal.AsBytes(expectedBackward.AsSpan())
                .SequenceEqual(
                    MemoryMarshal.AsBytes(actualBackward.AsSpan())));
    }

    [Fact(DisplayName = "Owned large FFT buffers match preserving APIs bit for bit")]
    public void OwnedLargeFftBuffersMatchPreservingApisBitForBit()
    {
        const int Length = 120_000;
        float[] values = DeterministicInput(2 * Length);
        Complex32[] input = Enumerable.Range(0, Length)
            .Select(index => new Complex32(
                values[2 * index],
                values[(2 * index) + 1]))
            .ToArray();

        Complex32[] expectedForward =
            PocketFftComplex32.ForwardAnyLengthDucc(
                input,
                workerThreads: 4);
        Complex32[] actualForward =
            PocketFftComplex32.ForwardAnyLengthDuccOwned(
                (Complex32[])input.Clone(),
                workerThreads: 4);
        Assert.True(
            MemoryMarshal.AsBytes(expectedForward.AsSpan())
                .SequenceEqual(
                    MemoryMarshal.AsBytes(actualForward.AsSpan())));

        Complex32[] expectedBackward =
            PocketFftComplex32.BackwardAnyLengthDucc(
                expectedForward,
                workerThreads: 4);
        Complex32[] actualBackward =
            PocketFftComplex32.BackwardAnyLengthDuccOwned(
                (Complex32[])expectedForward.Clone(),
                workerThreads: 4);
        Assert.True(
            MemoryMarshal.AsBytes(expectedBackward.AsSpan())
                .SequenceEqual(
                    MemoryMarshal.AsBytes(actualBackward.AsSpan())));
    }

    [Fact(DisplayName = "Warm large multipass FFT reuses Plan and packet workspaces")]
    public void WarmLargeMultipassFftReusesPlanAndPacketWorkspaces()
    {
        const int Length = 239_580;
        float[] values = DeterministicInput(2 * Length);
        var original = new Complex32[Length];
        for (int index = 0; index < original.Length; index++)
        {
            original[index] = new Complex32(
                values[2 * index],
                values[(2 * index) + 1]);
        }

        Complex32[] expected = PocketFftComplex32.ForwardAnyLengthDucc(
            original,
            workerThreads: 1);
        var source = new Complex32[Length];
        var scratch = new Complex32[Length];
        original.CopyTo(source, 0);
        _ = PocketFftComplex32.ForwardAnyLengthDuccOwned(
            source,
            scratch,
            workerThreads: 1);

        original.CopyTo(source, 0);
        Array.Fill(
            scratch,
            new Complex32(float.NaN, float.NegativeInfinity));
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        Complex32[] actual = PocketFftComplex32.ForwardAnyLengthDuccOwned(
            source,
            scratch,
            workerThreads: 1);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

        Assert.Equal(
            Sha256(MemoryMarshal.AsBytes(expected.AsSpan())),
            Sha256(MemoryMarshal.AsBytes(actual.AsSpan())));
        Assert.True(
            allocated < 64 * 1024,
            $"Warm multipass FFT allocated {allocated:N0} bytes.");
    }

    [Theory(DisplayName = "Real FFT plan threshold matches SciPy")]
    [InlineData(512, "2D112631FB98F4C92AA42FB93FC7356FDBEE1F4C688CED08D27E6147E3456B40")]
    [InlineData(1_024, "39C3C9BE39CC952600AFDB1ECF002BE654FEDA2A207C3E5F0D6DA943E7644F3B")]
    [InlineData(2_048, "80D6660EA5BA1AB710EF2CE70CAED9D75EC1D4FF4262DB0BE6985134A5A0350F")]
    [InlineData(4_096, "D827C1CFFF57238F89E900EC000E8BE618E6E6526BE3A7309B0F1F7CA06A639B")]
    [InlineData(8_192, "A0F868516C651230F5628C9613FD4F34F48224181F863AC8862C6F8DF674D7DD")]
    public void RealFftPlanThresholdMatchesScipy(
        int length,
        string expectedSha256)
    {
        Complex32[] spectrum = PocketFftReal32.ForwardAnyLength(
            DeterministicInput(length));

        Assert.Equal(
            expectedSha256,
            Sha256(MemoryMarshal.AsBytes(spectrum.AsSpan())));
    }

    [Theory(DisplayName = "Mixed-radix factor staging matches SciPy")]
    [InlineData(24, "9698C6EF39261F497975EF2D9C84F11C8BC4DD0E2ED4CCB4D4B72304B2065CD3")]
    [InlineData(60, "1F470B52EDE758A3898C20377A1717E06202A15381BE578341F46754B8F0DB25")]
    [InlineData(120, "9D64F8A2EFD9CDEF534C9CB3B2BF088EE4C97EA32FC5D1C7DD91C85655CCF832")]
    [InlineData(240, "59AB0441FAC021D151D72B4B80B0AEC2809EEED156EF7E4389C7EAA026AD9D88")]
    [InlineData(960, "A93F1947B58F8256AC9E4E9AB8B57AF26877DB4D50CF368C3E2BB824F087B486")]
    [InlineData(1_920, "7BE31D757081018D3789C7EF40D065A6CDC219124F03CCDE2FD13C3F68014027")]
    [InlineData(3_840, "F17B62DE44BFCE3A6AA2A55F71DA3F9FF52D97F8B79557B352B2F7827365C6BD")]
    [InlineData(7_680, "7397BA408A93CD7600ACA784AE20887B0165C7358734CD6EA7ED85558802F8CB")]
    [InlineData(15_360, "F548F56F6580DF55FF912B9CB743B8C498536EEC3C714914BC55A046F69145E4")]
    [InlineData(30_720, "1067713F881E07A3D5C60042DFC2F7502D5CD6865A880B1ABE83A161E9D3B277")]
    [InlineData(61_440, "CF1871509523C34DD4AE8E4F0B325FEE1E21D5725C0CA98878D28016A129CC4E")]
    [InlineData(120_000, "7C6C53747C9C71C1F6C2AE13357F9BAF4E4F06802E4BA0BF8004C7990F85BFF7")]
    public void MixedRadixFactorStagingMatchesScipy(
        int length,
        string expectedSha256)
    {
        Complex32[] spectrum = PocketFftReal32.ForwardAnyLength(
            DeterministicInput(length));

        Assert.Equal(
            expectedSha256,
            Sha256(MemoryMarshal.AsBytes(spectrum.AsSpan())));
    }

    [Theory(DisplayName = "Mixed-radix float32 real FFT matches SciPy")]
    [InlineData(
        608,
        1_120,
        "36CEAEFC02A0C72AF6D2B9380ED3643C885980079DA669F5ED132BB40D737042")]
    [InlineData(
        239_330,
        240_000,
        "0BBC3D396F6469D252701CE17ED01153268AC81DBC96B94887109FB38923D0F8")]
    [InlineData(
        355_255,
        356_400,
        "EDC4D804EE7B46B07600B7DE6EC4A133E11634CFF8B1107E75017B29BCEFED19")]
    public void MixedRadixFloat32RealFftMatchesScipy(
        int rawLength,
        int paddedLength,
        string expectedSha256)
    {
        float[] input = ReflectPad(
            DeterministicInput(rawLength),
            paddedLength);

        Complex32[] spectrum = PocketFftReal32.ForwardAnyLength(input);

        Assert.Equal(
            expectedSha256,
            Sha256(MemoryMarshal.AsBytes(spectrum.AsSpan())));
    }

    [Theory(DisplayName = "Super-Gaussian final filter matches SciPy")]
    [InlineData(
        608,
        1_120,
        4_433_618.75,
        626_953.0,
        "54EF9364ACFB40F76270792A5258DE4491D29831CD78BEEAA01EB5F42B4115C4")]
    [InlineData(
        239_330,
        240_000,
        3_579_545.0,
        629_921.0,
        "DF4A6290D9E1001C327E0C039B67DC9F667A8F61522A61BC81E096B43FD59BDE")]
    [InlineData(
        355_255,
        356_400,
        4_433_618.75,
        626_953.0,
        "DD592B3E0697AFE8953BD894FA484135EA5EACF155FA99D84C285ECC19C8ECCA")]
    [InlineData(
        239_067,
        239_580,
        3_575_611.888111,
        629_370.6293706294,
        "E021C6F7613A6ED98350163B0C9E55F3F0099879770E1B531BF29509DD8B5038")]
    [InlineData(
        239_067,
        239_580,
        3_575_611.888111,
        631_337.0,
        "80CCA2E7EFB0795058AD6264E392FC8C6156028B148F62EA6DD6DD849928E0E4")]
    [InlineData(
        355_255,
        356_400,
        4_406_000.0,
        732_400.0,
        "1397DDCCC72568724B7FAEEA908ED8C51EE2A8A4752A7F2D9F935631F78BF866")]
    public void SuperGaussianFinalFilterMatchesScipy(
        int rawLength,
        int paddedLength,
        double fscHz,
        double colorUnderHz,
        string expectedSha256)
    {
        var filter = new ChromaSuperGaussianFinalFilter(
            rawLength,
            fscHz,
            colorUnderHz);
        double[] input = DeterministicInput(rawLength)
            .Select(static value => (double)value)
            .ToArray();
        double[] actual = filter.Apply(input);
        float[] actualFloat32 = actual
            .Select(static value => (float)value)
            .ToArray();

        Assert.Equal(paddedLength, filter.PaddedLength);
        Assert.Equal(
            expectedSha256,
            Sha256(MemoryMarshal.AsBytes(actualFloat32.AsSpan())));
    }

    [Fact(DisplayName = "Super-Gaussian final filter is bit-exact across worker counts")]
    public void SuperGaussianFinalFilterCanReuseInputBuffer()
    {
        const int RawLength = 239_067;
        var filter = new ChromaSuperGaussianFinalFilter(
            RawLength,
            3_575_611.888111,
            629_370.6293706294);
        double[] input = DeterministicInput(RawLength)
            .Select(static value => (double)value)
            .ToArray();
        double[] expected = filter.Apply(input);
        long[] expectedBits = expected
            .Select(BitConverter.DoubleToInt64Bits)
            .ToArray();

        foreach (int workerThreads in new[] { 1, 4, 5, 8, 20 })
        {
            double[] actual = (double[])input.Clone();
            double[] returned = filter.ApplyInPlace(actual, workerThreads);

            Assert.Same(actual, returned);
            Assert.Equal(
                expectedBits,
                actual.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Fact(DisplayName = "Super-Gaussian final filter retains one reusable FFT workspace")]
    public void SuperGaussianFinalFilterRetainsOneReusableFftWorkspace()
    {
        const int RawLength = 239_067;
        var filter = new ChromaSuperGaussianFinalFilter(
            RawLength,
            3_575_611.888111,
            629_370.6293706294);
        double[] source = DeterministicInput(RawLength)
            .Select(static value => (double)value)
            .ToArray();
        double[] first = (double[])source.Clone();
        double[] second = (double[])source.Clone();

        filter.ApplyInPlace(first, workerThreads: 4);

        Assert.Equal(1, filter.WorkspaceCreationCount);
        Assert.Equal(1, filter.RetainedWorkspaceCount);

        filter.ApplyInPlace(second, workerThreads: 4);

        Assert.Equal(1, filter.WorkspaceCreationCount);
        Assert.Equal(1, filter.RetainedWorkspaceCount);
        Assert.Equal(
            first.Select(BitConverter.DoubleToInt64Bits),
            second.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact(DisplayName = "Concurrent Super-Gaussian calls use isolated bounded workspaces")]
    public async Task ConcurrentSuperGaussianCallsUseIsolatedBoundedWorkspaces()
    {
        const int RawLength = 239_067;
        const double FscHz = 3_575_611.888111;
        const double CarrierHz = 629_370.6293706294;
        double[] firstSource = DeterministicInput(RawLength)
            .Select(static value => (double)value)
            .ToArray();
        double[] secondSource = firstSource
            .Select(static (value, index) =>
                value + (((index % 17) - 8) * 0.0001))
            .ToArray();
        double[] expectedFirst = new ChromaSuperGaussianFinalFilter(
                RawLength,
                FscHz,
                CarrierHz)
            .ApplyInPlace((double[])firstSource.Clone(), workerThreads: 8);
        double[] expectedSecond = new ChromaSuperGaussianFinalFilter(
                RawLength,
                FscHz,
                CarrierHz)
            .ApplyInPlace((double[])secondSource.Clone(), workerThreads: 8);
        var sharedFilter = new ChromaSuperGaussianFinalFilter(
            RawLength,
            FscHz,
            CarrierHz);
        using var startGate = new Barrier(participantCount: 2);

        Task<double[]> firstTask = StartConcurrentApply(firstSource);
        Task<double[]> secondTask = StartConcurrentApply(secondSource);
        double[][] actual = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(
            expectedFirst.Select(BitConverter.DoubleToInt64Bits),
            actual[0].Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(
            expectedSecond.Select(BitConverter.DoubleToInt64Bits),
            actual[1].Select(BitConverter.DoubleToInt64Bits));
        Assert.InRange(sharedFilter.WorkspaceCreationCount, 1, 2);
        Assert.Equal(1, sharedFilter.RetainedWorkspaceCount);

        Task<double[]> StartConcurrentApply(double[] source)
            => Task.Factory.StartNew(
                () =>
                {
                    startGate.SignalAndWait();
                    return sharedFilter.ApplyInPlace(
                        (double[])source.Clone(),
                        workerThreads: 8);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
    }

    [Fact(DisplayName = "Large real FFT workspace path matches allocating API after dirty reuse")]
    public void LargeRealFftWorkspacePathMatchesAllocatingApiAfterDirtyReuse()
    {
        const int Length = 239_580;
        float[] input = DeterministicInput(Length);
        Complex32[] expectedSpectrum =
            PocketFftReal32.ForwardAnyLength(input, workerThreads: 4);
        float[] expectedOutput = PocketFftReal32.InverseAnyLength(
            expectedSpectrum,
            Length,
            workerThreads: 4);
        var complexInput = new Complex32[Length / 2];
        var transformScratch = new Complex32[Length / 2];
        var actualSpectrum = new Complex32[(Length / 2) + 1];
        var actualOutput = new float[Length];

        AssertWorkspaceTransformMatches();
        Array.Fill(
            complexInput,
            new Complex32(float.NaN, float.NegativeInfinity));
        Array.Fill(
            transformScratch,
            new Complex32(float.PositiveInfinity, float.NaN));
        Array.Fill(
            actualSpectrum,
            new Complex32(float.NaN, float.NaN));
        Array.Fill(actualOutput, float.NaN);
        AssertWorkspaceTransformMatches();

        void AssertWorkspaceTransformMatches()
        {
            PocketFftReal32.ForwardAnyLength(
                input,
                complexInput,
                transformScratch,
                actualSpectrum,
                workerThreads: 4);
            Assert.Equal(
                Sha256(MemoryMarshal.AsBytes(expectedSpectrum.AsSpan())),
                Sha256(MemoryMarshal.AsBytes(actualSpectrum.AsSpan())));

            PocketFftReal32.InverseAnyLength(
                actualSpectrum,
                Length,
                complexInput,
                transformScratch,
                actualOutput,
                workerThreads: 4);
            Assert.Equal(
                Sha256(MemoryMarshal.AsBytes(expectedOutput.AsSpan())),
                Sha256(MemoryMarshal.AsBytes(actualOutput.AsSpan())));
        }
    }

    [Theory(DisplayName = "Next fast FFT length matches SciPy")]
    [InlineData(1, 1)]
    [InlineData(13, 14)]
    [InlineData(1_120, 1_120)]
    [InlineData(239_842, 240_000)]
    [InlineData(355_767, 356_400)]
    public void NextFastFftLengthMatchesScipy(
        int minimumLength,
        int expected)
    {
        Assert.Equal(
            expected,
            ChromaSuperGaussianFinalFilter.NextFastLength(minimumLength));
    }

    [Fact(DisplayName = "Complexified inverse matches SciPy")]
    public void ComplexifiedInverseMatchesScipy()
    {
        float[] input = DeterministicInput(32_768);
        Complex32[] spectrum = PocketFftReal32.ForwardDucc(input);
        float[] actual = PocketFftReal32.InverseAnyLength(
            spectrum,
            input.Length);

        Assert.Equal(
            "EAD0CE62E4AAB1ADC2BD7F2D2A5489443EFC1160F97B1044BE8609E98940DC5F",
            Sha256(MemoryMarshal.AsBytes(actual.AsSpan())));
    }

    private static float[] DeterministicInput(int length)
    {
        var output = new float[length];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = (float)((((long)i * 7_919L) + 104_729L)
                % 65_521L - 32_760L);
        }

        return output;
    }

    private static float[] ReflectPad(
        float[] input,
        int paddedLength)
    {
        int padLeft = (paddedLength - input.Length) / 2;
        int padRight = paddedLength - input.Length - padLeft;
        var output = new float[paddedLength];
        for (int i = 0; i < padLeft; i++)
        {
            output[i] = input[padLeft - i];
        }

        input.CopyTo(output, padLeft);
        for (int i = 0; i < padRight; i++)
        {
            output[padLeft + input.Length + i] =
                input[input.Length - i - 2];
        }

        return output;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));
}
