using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using VHSDecode.Core.Dsp;
using VHSDecode.Core.Dsp.Ipp;
using Xunit;

namespace VHSDecode.Tests.Ipp;

public sealed class IppBackendInteropTests
{
    [Theory(DisplayName = "DSP backend parser recognizes exact and explicit IPP fast values")]
    [InlineData("exact", DspBackend.Exact)]
    [InlineData("EXACT", DspBackend.Exact)]
    [InlineData("ipp-fast", DspBackend.IppFast)]
    [InlineData("IPP-FAST", DspBackend.IppFast)]
    public void DspBackendParserRecognizesSupportedValues(
        string value,
        DspBackend expected)
    {
        Assert.Equal(expected, DspBackendParser.Parse(value));
        Assert.True(DspBackendParser.TryParse(value, out DspBackend parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(
            expected == DspBackend.Exact ? "exact" : "ipp-fast",
            DspBackendParser.ToCommandLineValue(expected));
    }

    [Fact(DisplayName = "DSP backend parser rejects values outside the stable CLI contract")]
    public void DspBackendParserRejectsUnsupportedValues()
    {
        Assert.False(DspBackendParser.TryParse(null, out _));
        Assert.False(DspBackendParser.TryParse("", out _));
        Assert.False(DspBackendParser.TryParse(" ipp-fast ", out _));
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => DspBackendParser.Parse("native"));
        Assert.Contains("exact", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ipp-fast", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Selecting exact in an isolated core assembly performs no native load")]
    public void SelectingExactPerformsNoNativeLoad()
    {
        var loadContext = new AssemblyLoadContext(
            $"ipp-exact-no-load-{Guid.NewGuid():N}",
            isCollectible: true);
        int nativeLoadAttempts = 0;
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(
                typeof(DspBackend).Assembly.Location);
            NativeLibrary.SetDllImportResolver(
                assembly,
                (_, _, _) =>
                {
                    Interlocked.Increment(ref nativeLoadAttempts);
                    throw new DllNotFoundException("Test resolver rejected a native load.");
                });

            Type parserType = assembly.GetType(
                "VHSDecode.Core.Dsp.DspBackendParser",
                throwOnError: true)!;
            MethodInfo parse = parserType.GetMethod(
                nameof(DspBackendParser.Parse),
                BindingFlags.Public | BindingFlags.Static)!;
            object parsed = parse.Invoke(null, ["exact"])!;

            Assert.Equal("Exact", parsed.ToString());

            Type demodulatorType = assembly.GetType(
                "VHSDecode.Core.Dsp.RfDemodulator",
                throwOnError: true)!;
            Type backendType = assembly.GetType(
                "VHSDecode.Core.Dsp.DspBackend",
                throwOnError: true)!;
            ConstructorInfo constructor = demodulatorType.GetConstructor(
                [typeof(double), backendType])!;
            object demodulator = constructor.Invoke([40_000_000.0, parsed]);
            ((IDisposable)demodulator).Dispose();
            ((IDisposable)demodulator).Dispose();

            Assert.Equal(0, Volatile.Read(ref nativeLoadAttempts));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    [Fact(DisplayName = "Managed IPP ABI structs have the native v1 layout")]
    public void ManagedAbiStructsHaveNativeLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<IppComplex32>());
        Assert.Equal(4, Marshal.OffsetOf<IppComplex32>(nameof(IppComplex32.Imaginary)).ToInt32());
        Assert.Equal(16, Marshal.SizeOf<IppComplex64>());
        Assert.Equal(8, Marshal.OffsetOf<IppComplex64>(nameof(IppComplex64.Imaginary)).ToInt32());
        Assert.Equal(16, Marshal.SizeOf<Complex>());
        Complex sample = new(1.25, -2.5);
        ReadOnlySpan<double> components = MemoryMarshal.Cast<Complex, double>(
            MemoryMarshal.CreateReadOnlySpan(ref sample, 1));
        Assert.Equal(2, components.Length);
        Assert.Equal(1.25, components[0]);
        Assert.Equal(-2.5, components[1]);
        Assert.Equal(IppRuntimeInfoNative.ExpectedSize, Marshal.SizeOf<IppRuntimeInfoNative>());
    }

    [Fact(DisplayName = "Explicit IPP fast probe reports a deterministic missing-DLL failure")]
    public void ExplicitIppFastProbeReportsMissingDll()
    {
        const string MissingLibrary = "vhsdecode_ipp_deliberately_missing_for_test_72E88F58";

        IppBackendUnavailableException exception = Assert.Throws<IppBackendUnavailableException>(
            () => IppRuntime.ProbeRequiredForLibraryName(MissingLibrary));

        Assert.Equal(IppBackendFailureKind.NativeLibraryNotFound, exception.FailureKind);
        Assert.Contains("ipp-fast", exception.Message, StringComparison.Ordinal);
        Assert.Contains(MissingLibrary, exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP real FFT rejects invalid lengths before probing the optional runtime")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(32_769)]
    public void IppRealFftRejectsInvalidLengthBeforeNativeProbe(int length)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new IppRealFft(length));
        Assert.Contains("power of two", exception.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "IPP real FFT agrees numerically and round-trips finite data when present")]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(256)]
    [InlineData(4_096)]
    [InlineData(32_768)]
    public void IppRealFftAgreesNumericallyAndRoundTripsFiniteData(int length)
    {
        if (!IppRuntime.TryProbe(out IppRuntimeInfo? runtimeInfo))
        {
            return;
        }

        Assert.NotNull(runtimeInfo);
        Assert.Equal(0x0001_0000U, runtimeInfo.AbiVersion);

        double[] input = BuildFiniteInput(length);
        Complex[] expectedSpectrum = PocketFftReal.Forward(input);
        var actualSpectrum = new Complex[expectedSpectrum.Length];
        var reconstructed = new double[length];

        using var fft = new IppRealFft(length);
        Assert.Equal(length, fft.Length);
        Assert.Equal(expectedSpectrum.Length, fft.SpectrumLength);
        fft.Forward(input, actualSpectrum);
        fft.Inverse(actualSpectrum, reconstructed);

        AssertSpectrumClose(expectedSpectrum, actualSpectrum);
        AssertRealClose(input, reconstructed);
    }

    [Fact(DisplayName = "IPP real FFT accepts NaN and infinity as a no-crash smoke case")]
    public void IppRealFftAcceptsNonFiniteSmokeInput()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 256;
        double[] input = BuildFiniteInput(Length);
        input[7] = double.NaN;
        input[31] = double.PositiveInfinity;
        input[127] = double.NegativeInfinity;
        var spectrum = new Complex[(Length / 2) + 1];
        var reconstructed = new double[Length];

        using var fft = new IppRealFft(Length);
        fft.Forward(input, spectrum);
        fft.Inverse(spectrum, reconstructed);

        Assert.Contains(
            spectrum,
            value => !double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary));
        Assert.Contains(reconstructed, value => !double.IsFinite(value));
    }

    [Fact(DisplayName = "IPP real FFT serializes parallel calls on one context")]
    public void IppRealFftSerializesParallelCallsOnOneContext()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 4_096;
        double[] input = BuildFiniteInput(Length);
        Complex[] expectedSpectrum = PocketFftReal.Forward(input);

        using var fft = new IppRealFft(Length);
        Parallel.For(
            0,
            12,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            _ =>
            {
                var actualSpectrum = new Complex[fft.SpectrumLength];
                var reconstructed = new double[Length];
                fft.Forward(input, actualSpectrum);
                fft.Inverse(actualSpectrum, reconstructed);
                AssertSpectrumClose(expectedSpectrum, actualSpectrum);
                AssertRealClose(input, reconstructed);
            });
    }

    [Fact(DisplayName = "Separate IPP real FFT contexts execute correctly in parallel")]
    public void SeparateIppRealFftContextsExecuteInParallel()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        const int Length = 4_096;
        const int ContextCount = 6;
        double[] input = BuildFiniteInput(Length);
        Complex[] expectedSpectrum = PocketFftReal.Forward(input);
        IppRealFft[] contexts = Enumerable.Range(0, ContextCount)
            .Select(_ => new IppRealFft(Length))
            .ToArray();
        try
        {
            Parallel.For(
                0,
                contexts.Length,
                new ParallelOptions { MaxDegreeOfParallelism = contexts.Length },
                index =>
                {
                    var actualSpectrum = new Complex[contexts[index].SpectrumLength];
                    var reconstructed = new double[Length];
                    contexts[index].Forward(input, actualSpectrum);
                    contexts[index].Inverse(actualSpectrum, reconstructed);
                    AssertSpectrumClose(expectedSpectrum, actualSpectrum);
                    AssertRealClose(input, reconstructed);
                });
        }
        finally
        {
            foreach (IppRealFft context in contexts)
            {
                context.Dispose();
            }
        }
    }

    [Fact(DisplayName = "Disposed IPP real FFT rejects further calls")]
    public void DisposedIppRealFftRejectsCalls()
    {
        if (!IppRuntime.TryProbe(out _))
        {
            return;
        }

        using var fft = new IppRealFft(8);
        var input = new double[fft.Length];
        var spectrum = new Complex[fft.SpectrumLength];
        fft.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fft.Forward(input, spectrum));
        Assert.Throws<ObjectDisposedException>(() => fft.Inverse(spectrum, input));
    }

    private static double[] BuildFiniteInput(int length)
    {
        double[] result = Enumerable.Range(0, length)
            .Select(index =>
                Math.Sin(index * 0.017)
                + (0.25 * Math.Cos(index * 0.031))
                + ((index % 17) * 0.0001))
            .ToArray();

        if (length >= 8)
        {
            result[0] = 0.0;
            result[1] = -0.0;
            result[2] = BitConverter.Int64BitsToDouble(1);
            result[3] = BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0001UL));
            result[4] = BitConverter.Int64BitsToDouble(0x0010_0000_0000_0000L);
            result[5] = BitConverter.Int64BitsToDouble(unchecked((long)0x8010_0000_0000_0000UL));
            Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(result[1]));
            Assert.True(double.IsSubnormal(result[2]));
            Assert.True(double.IsSubnormal(result[3]));
        }

        return result;
    }

    private static void AssertSpectrumClose(
        ReadOnlySpan<Complex> expected,
        ReadOnlySpan<Complex> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            double tolerance = 1e-10 * Math.Max(1.0, expected[index].Magnitude);
            Assert.InRange(
                Complex.Abs(expected[index] - actual[index]),
                0.0,
                tolerance);
        }
    }

    private static void AssertRealClose(
        ReadOnlySpan<double> expected,
        ReadOnlySpan<double> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            double tolerance = 1e-10 * Math.Max(1.0, Math.Abs(expected[index]));
            Assert.InRange(Math.Abs(expected[index] - actual[index]), 0.0, tolerance);
        }
    }
}
