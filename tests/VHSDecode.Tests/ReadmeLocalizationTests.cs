using System.Text.RegularExpressions;
using Xunit;

namespace VHSDecode.Tests;

public sealed partial class ReadmeLocalizationTests
{
    private const string LatestPerformanceRunsMarker =
        "<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 " +
        "dotnet-matrix-runs=60 dotnet-current-runs=30 " +
        "python-reference-runs=30 dotnet-repeats=3 " +
        "python-reference-date=2026-08-12 dotnet-v040-date=2026-08-14 " +
        "dotnet-current-date=2026-08-14 phase22-200-ab-pairs=20 " +
        "phase22-long-ab-pairs=8 phase22-thread-backend-runs=60 " +
        "phase22-gc-traces=2 phase22-tests=1438 " +
        "phase24-short-ab-pairs=6 phase24-long-ab-pairs=4 " +
        "phase24-thread-gate-runs=12 phase24-tests=1442 " +
        "phase25-public-cell-runs=15 phase25-public-ab-pairs=15 " +
        "phase25-long-ab-pairs=3 phase25-thread-gate-runs=12 " +
        "phase25-tests=1446 phase26-kernel-ab-pairs=8 " +
        "phase26-long-ab-pairs=4 phase26-thread-backend-runs=36 " +
        "phase26-public-cell-runs=30 phase26-tests=1447 " +
        "phase27-kernel-ab-pairs=8 phase27-long-ab-pairs=8 " +
        "phase27-thread-backend-runs=24 phase27-public-cell-runs=60 " +
        "phase27-tests=1448 phase28-kernel-ab-pairs=8 " +
        "phase28-long-ab-pairs=6 phase28-thread-backend-runs=24 " +
        "phase28-intrinsic-runs=3 phase28-public-cell-runs=60 " +
        "phase28-tests=1448 phase30-burst-kernel-runs=14 " +
        "phase30-long-ab-pairs=3 phase30-thread-gate-runs=6 " +
        "phase30-memory-runs=2 phase30-public-cell-runs=60 " +
        "phase30-tests=1448 " +
        "python-v040-runs=15 python-v040-hashes=15 " +
        "python-pr341-runs=15 python-pr341-hashes=1 -->";

    private const string FullCiTestCommand =
        "run: dotnet test --solution VHSDecodeDotNet.slnx --configuration Release " +
        "--no-build --no-restore --minimum-expected-tests 1448";

    private const string FinalRealRadix4AvxTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-method " +
        "'*RealInverseFftFinalRadix4AvxStagePreservesScalarSpecialValueBits*' " +
        "--minimum-expected-tests 1";

    private const string FinalRealRadix4AvxRequirement =
        "VHSDECODE_REQUIRE_AVX_REAL_RADIX4: \"1\"";

    private const string FinalRealRadix4ScalarFallbackTestName =
        "Run final real PocketFFT radix-4 scalar fallback test";

    private const string SuperGaussianMaskAvxTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-method " +
        "'*SuperGaussianMaskSimdPreservesScalarComplexBits*' " +
        "--minimum-expected-tests 1";

    private const string SuperGaussianMaskAvxRequirement =
        "VHSDECODE_REQUIRE_AVX_SUPER_GAUSSIAN_MASK: \"1\"";

    private const string SuperGaussianMaskAvxDisabledTestName =
        "Run managed Super-Gaussian mask AVX-disabled equivalence test";

    private const string CurrentCtiAvx2GatherTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-method " +
        "'*PinnedCurrentCtiAvx2ReciprocalGatherPreservesScalarBits*' " +
        "--minimum-expected-tests 1";

    private const string CurrentCtiAvx2GatherRequirement =
        "VHSDECODE_REQUIRE_AVX_CTI_GATHER: \"1\"";

    private const string CurrentCtiAvx2DisabledTestName =
        "Run current CTI AVX2-disabled tests";

    private const string CurrentCtiAvx2DisabledRequirement =
        "DOTNET_EnableAVX2: \"0\"";

    private const string CurrentCtiScalarFallbackTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-class " +
        "VHSDecode.Tests.ChromaTransientImprovementCurrentTests " +
        "--minimum-expected-tests 19";

    private const string CurrentVhsSyncScalarFallbackTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-class " +
        "VHSDecode.Tests.VhsSyncDetectorCurrentTests " +
        "--minimum-expected-tests 36";

    private const string CurrentChromaAccAvxDisabledTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-class " +
        "VHSDecode.Tests.VhsChromaAutomaticGainCurrentTests " +
        "--minimum-expected-tests 15";

    private const string PocketFftRadix8AvxDisabledTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-method " +
        "'*AvxRadix8StagesPreserveScalarSpecialValueBits*' " +
        "--minimum-expected-tests 1";

    private const string PocketFftRadix5AvxDisabledTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-method " +
        "'*AvxRadix5StagesPreserveScalarSpecialValueBits*' " +
        "--minimum-expected-tests 1";

    private const string PocketFftRadix5ScalarFallbackTestName =
        "Run PocketFFT radix-5 scalar fallback equivalence test";

    private const string PocketFftRadix3AvxDisabledTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-method " +
        "'*AvxRadix3StagesPreserveScalarSpecialValueBits*' " +
        "--minimum-expected-tests 1";

    private const string PocketFftRadix3AvxTestName =
        "Run PocketFFT radix-3 AVX equivalence test";

    private const string PocketFftRadix3AvxRequirement =
        "VHSDECODE_REQUIRE_AVX_RADIX3: \"1\"";

    private const string PocketFftRadix3ScalarFallbackTestName =
        "Run PocketFFT radix-3 scalar fallback equivalence test";

    private const string PocketFftRadix11Avx2DisabledTestCommand =
        "run: dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj " +
        "--configuration Release --no-build --no-restore --filter-method " +
        "'*Avx2Radix11PacketStagesPreserveScalarSpecialValueBits*' " +
        "--minimum-expected-tests 1";

    private const string PocketFftRadix11Avx2TestName =
        "Run PocketFFT radix-11 AVX2 equivalence test";

    private const string PocketFftRadix11Avx2DisabledTestName =
        "Run PocketFFT radix-11 AVX2-disabled equivalence test";

    private const string PocketFftRadix11AvxDisabledTestName =
        "Run PocketFFT radix-11 AVX-disabled equivalence test";

    private const string PocketFftRadix11Avx2Requirement =
        "VHSDECODE_REQUIRE_AVX_RADIX11: \"1\"";

    private const string PocketFftRadix11ScalarFallbackTestName =
        "Run PocketFFT radix-11 scalar fallback equivalence test";

    private static readonly string[] OverviewReadmeFiles =
    [
        "README.md",
        "README.zh-CN.md",
        "README.ja.md"
    ];

    private static readonly string[] DetailedReadmeFiles =
    [
        Path.Combine("docs", "README.detailed.md"),
        Path.Combine("docs", "README.detailed.zh-CN.md"),
        Path.Combine("docs", "README.detailed.ja.md")
    ];

    private static readonly string[] ExpectedOverviewSections =
    [
        "overview",
        "start",
        "profiles",
        "performance",
        "compatibility",
        "build",
        "detail",
        "license"
    ];

    private static readonly string[] ExpectedDetailedSections =
    [
        "scope",
        "status",
        "coverage",
        "performance",
        "build",
        "usage",
        "preview",
        "verification",
        "remaining",
        "evidence",
        "license"
    ];

    [Fact(DisplayName = "Localized READMEs share navigation, version, and sections")]
    public void LocalizedReadmesShareNavigationVersionAndSections()
    {
        IReadOnlyDictionary<string, string> overviews = ReadReadmes(OverviewReadmeFiles);
        IReadOnlyDictionary<string, string> details = ReadReadmes(DetailedReadmeFiles);
        string expectedMarker = SingleCapture(
            SyncMarkerRegex(),
            overviews["README.md"],
            "version");

        foreach ((string filename, string content) in overviews)
        {
            Assert.Equal(
                expectedMarker,
                SingleCapture(SyncMarkerRegex(), content, "version"));
            Assert.True(
                ExpectedOverviewSections.SequenceEqual(Captures(SectionRegex(), content, "id")),
                $"{filename} does not contain the synchronized section sequence.");
            Assert.Contains("[English](README.md)", content, StringComparison.Ordinal);
            Assert.Contains("[简体中文](README.zh-CN.md)", content, StringComparison.Ordinal);
            Assert.Contains("[日本語](README.ja.md)", content, StringComparison.Ordinal);
        }

        foreach ((string filename, string content) in details)
        {
            Assert.Equal(
                expectedMarker,
                SingleCapture(SyncMarkerRegex(), content, "version"));
            Assert.True(
                ExpectedDetailedSections.SequenceEqual(Captures(SectionRegex(), content, "id")),
                $"{filename} does not contain the synchronized detailed section sequence.");
            Assert.Contains("[English](README.detailed.md)", content, StringComparison.Ordinal);
            Assert.Contains(
                "[简体中文](README.detailed.zh-CN.md)",
                content,
                StringComparison.Ordinal);
            Assert.Contains("[日本語](README.detailed.ja.md)", content, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "Localized READMEs share commands and release facts")]
    public void LocalizedReadmesShareCommandsAndReleaseFacts()
    {
        IReadOnlyDictionary<string, string> overviews = ReadReadmes(OverviewReadmeFiles);
        IReadOnlyDictionary<string, string> details = ReadReadmes(DetailedReadmeFiles);
        string[] expectedOverviewCommands = Captures(
            PowerShellBlockRegex(),
            overviews["README.md"],
            "body");
        Assert.Equal(3, expectedOverviewCommands.Length);
        string[] expectedDetailedCommands = Captures(
            PowerShellBlockRegex(),
            details[Path.Combine("docs", "README.detailed.md")],
            "body");
        Assert.Equal(3, expectedDetailedCommands.Length);
        string[] expectedOverviewPerformanceRows =
        [
            "52.811 s | 54.243 s | 12.320 s | 4.287x | 10.717 s | 5.062x | 10.776 s | 4.901x | 8.822 s | 6.148x",
            "57.067 s | 56.762 s | 31.391 s | 1.818x | 34.196 s | 1.660x | 22.631 s | 2.522x | 24.331 s | 2.333x",
            "52.920 s | 55.722 s | 12.226 s | 4.328x | 10.690 s | 5.212x | 11.012 s | 4.805x | 8.607 s | 6.474x",
            "52.965 s | 54.949 s | 10.030 s | 5.281x | 8.164 s | 6.731x | 9.406 s | 5.631x | 6.936 s | 7.922x",
            "53.555 s | 54.842 s | 8.062 s | 6.643x | 6.810 s | 8.053x | 8.017 s | 6.680x | 5.290 s | 10.366x"
        ];
        string[] expectedDetailedPerformanceRows =
        [
            "52.811 s | 54.243 s | 12.320 s | 4.287x | 76.67% | 10.717 s | 5.062x | 80.24% | 10.776 s | 4.901x | 79.60% | 8.822 s | 6.148x | 83.74%",
            "57.067 s | 56.762 s | 31.391 s | 1.818x | 44.99% | 34.196 s | 1.660x | 39.76% | 22.631 s | 2.522x | 60.34% | 24.331 s | 2.333x | 57.14%",
            "52.920 s | 55.722 s | 12.226 s | 4.328x | 76.90% | 10.690 s | 5.212x | 80.81% | 11.012 s | 4.805x | 79.19% | 8.607 s | 6.474x | 84.55%",
            "52.965 s | 54.949 s | 10.030 s | 5.281x | 81.06% | 8.164 s | 6.731x | 85.14% | 9.406 s | 5.631x | 82.24% | 6.936 s | 7.922x | 87.38%",
            "53.555 s | 54.842 s | 8.062 s | 6.643x | 84.95% | 6.810 s | 8.053x | 87.58% | 8.017 s | 6.680x | 85.03% | 5.290 s | 10.366x | 90.35%"
        ];
        string[] expectedDetailedPerformanceRangeRows =
        [
            "default 5 | 52.583-62.222 s | 53.893-58.195 s | 12.112-12.408 s | 10.712-10.949 s | 10.767-11.013 s | 8.799-8.864 s",
            "threads 1 | 56.709-60.521 s | 56.335-58.991 s | 30.368-32.607 s | 33.439-34.554 s | 22.071-23.345 s | 23.849-25.567 s",
            "threads 5 | 52.845-53.977 s | 53.696-58.437 s | 12.183-12.232 s | 10.612-10.873 s | 10.802-11.149 s | 8.541-8.708 s",
            "threads 10 | 51.797-53.088 s | 52.649-56.775 s | 9.991-10.215 s | 7.893-8.435 s | 9.324-9.592 s | 6.871-7.179 s",
            "threads 20 | 52.967-55.987 s | 53.005-55.618 s | 8.029-8.072 s | 6.663-6.865 s | 7.723-8.087 s | 5.272-5.417 s"
        ];

        string[] overviewFacts =
        [
            "43155200da87c0d49eb37d8ec09b1372075ee8e4",
            "11.0.100-preview.6.26359.118",
            "**1,448**",
            "--compat-version",
            "current",
            "--dsp-backend",
            "ipp-fast",
            "Exact + v0.4.0",
            "Exact + current",
            "IPP-fast + v0.4.0",
            "IPP-fast + current",
            "--start 100",
            "2b35b9e",
            "52.811 s",
            "54.243 s",
            "12.320 s",
            "10.717 s",
            "8.053x",
            "10.366x",
            "29.576",
            "28.673",
            "3.05%",
            "1.031x",
            "273.375",
            "275.297",
            "+0.70%",
            "9.24",
            "9.60",
            "+3.87%",
            "398.9",
            "405.9",
            "g4315520",
            "--threads 0"
        ];

        string[] synchronizedFacts =
        [
            "43155200da87c0d49eb37d8ec09b1372075ee8e4",
            "2f21e8ed6018b14561396cc95f1f6828054470b8",
            "v0.4.0-40-g2f21e8ed",
            "11.0.100-preview.6.26359.118",
            "bdccd58",
            "1,210.850",
            "167.083",
            "86.20%",
            "7.247x",
            "35.879",
            "35.608",
            "298.148",
            "295.141",
            "352.5",
            "357.9",
            "697.821",
            "676.900",
            "3.00%",
            "39.174",
            "39.152",
            "0.06%",
            "315.130",
            "310.140",
            "1.58%",
            "32.971",
            "32.323",
            "278.195",
            "267.500",
            "394.2 MiB",
            "404.5 MiB",
            "912.073",
            "48.28%",
            "1.933x",
            "34.068",
            "33.462",
            "291.781",
            "286.734",
            "345.9",
            "347.4",
            "33.715",
            "1.063x",
            "284.063",
            "3.71%",
            "41.77",
            "41.04",
            "Phase 24",
            "1,048,576-frame",
            "2 MiB",
            "9.231",
            "9.223",
            "88.141",
            "84.906",
            "7.749",
            "7.757",
            "45.953",
            "45.406",
            "35.383/35.027",
            "293.797/290.023",
            "33.112/32.830",
            "197.367/196.844",
            "353.0-357.5 MiB",
            "366.9-371.5 MiB",
            "114,244,800",
            "67,256,448",
            "183,595,176",
            "137,502,920",
            "2,099",
            "1,998",
            "41.13%",
            "25.11%",
            "4.81%",
            "34.732",
            "34.000",
            "2.11%",
            "1.022x",
            "284.766",
            "279.500",
            "8.199",
            "8.221",
            "352.2/351.4 MiB",
            "7.137",
            "7.102",
            "0.49%",
            "ea1bb8e",
            "35.242",
            "35.179",
            "0.18%",
            "1.002x",
            "281.84",
            "286.22",
            "68.724",
            "68.473",
            "0.37%",
            "353.1 MiB",
            "359.6 MiB",
            "352.0/357.2 MiB",
            "2,522.737",
            "2,396.830",
            "4.99%",
            "1.053x",
            "36.603",
            "35.631",
            "2.66%",
            "1.027x",
            "297.333",
            "283.583",
            "4.62%",
            "69.119",
            "355.75 MiB",
            "353.52 MiB",
            "341.80/350.72 MiB",
            "103.415",
            "34.925",
            "66.23%",
            "37.328",
            "36.734",
            "1.59%",
            "1.016x",
            "295.823",
            "293.208",
            "0.88%",
            "7.925",
            "7.982",
            "70.139",
            "647.43 MiB",
            "604.34 MiB",
            "590.37/598.75/601.05 MiB",
            "-24.81 MiB/min",
            "712.391",
            "705.693",
            "0.94%",
            "740.479",
            "709.507",
            "4.18%",
            "37.288",
            "36.909",
            "1.02%",
            "301.984",
            "292.766",
            "3.05%",
            "443.4 MiB",
            "355.6 MiB",
            "986.10",
            "727.61",
            "26.2%",
            "1,000",
            "750",
            "25.0%",
            "39.867",
            "39.515",
            "0.88%",
            "315.260",
            "305.969",
            "7.91",
            "7.74",
            "650.6 MiB",
            "396.2 MiB",
            "616 bytes",
            "757.745",
            "600.026",
            "20.8%",
            "765.625",
            "671.875",
            "12.2%",
            "38.483",
            "37.800",
            "1.78%",
            "308.664",
            "308.188",
            "8.02",
            "8.15",
            "352.1",
            "450.5 MiB",
            "370.6",
            "370.2 MiB",
            "817.0 MiB",
            "3,840",
            "59 ",
            "34.782",
            "34.446",
            "295.266",
            "291.602",
            "35.255",
            "35.052",
            "2.20%",
            "31.764",
            "31.247",
            "202.539",
            "195.578",
            "3.44%",
            "715 MiB",
            "1.2953",
            "1.1646",
            "10.1%",
            "16.4905",
            "16.2279",
            "132.99",
            "127.28",
            "40.321",
            "39.842",
            "1.19%",
            "325.28",
            "317.73",
            "406 MiB",
            "eec3658",
            "11.945 s",
            "11.480 s",
            "1.041x",
            "95.359 s",
            "90.828 s",
            "7.98",
            "7.91",
            "ae3722d",
            "AAAB4B0A884D0F22B361E369A55A2C475DD2D042806043B25EAFB5DF188B7860",
            "845d8d1",
            "7BAC056495F42BF4327F6E9D99AF4168F0FA585319BC9CF9F9D09E84B4A3E632",
            "3740bf1",
            "8409b1f",
            "54.953",
            "53.732",
            "2.22%",
            "79.259",
            "78.562",
            "1.0089x",
            "0.63%",
            "441.6/443.7 MiB",
            "579.7/563.4 MiB",
            "--start 100",
            "--start 0",
            "39.770",
            "39.932",
            "320.23",
            "314.67",
            "405.25",
            "399.84",
            "1768.90",
            "1515.88",
            "14.30%",
            "1.167x",
            "1498.46",
            "1516.92",
            "1560.97",
            "20.72",
            "20.36",
            "1.73%",
            "84.20",
            "83.51",
            "0.83%",
            "370.5",
            "364.2",
            "41.10",
            "40.12",
            "2.38%",
            "336.77",
            "328.91",
            "2.33%",
            "439.8",
            "416.7",
            "8.391",
            "5.454",
            "35.0%",
            "7.72",
            "8.43",
            "9.83",
            "11.22",
            "11.499",
            "11.373",
            "1.10%",
            "1.12%",
            "92.133",
            "90.695",
            "1.56%",
            "21.651",
            "21.555",
            "0.44%",
            "42.463",
            "42.221",
            "0.57%",
            "351.297",
            "347.672",
            "1.03%",
            "439.2",
            "437.7 MiB",
            "61A8966C55B1331509E0A60D6B731FA125F9406185C2C4AAF4E4CBC42F16C80D",
            "73dd014",
            "c3aac76922ad525884cf26779b2035db76248547",
            "766e0926fec7c07bb2bc0a20c0c39824b8f05996",
            "154.540/151.793",
            "195.178/190.025",
            "278.516/275.540",
            "1.78%",
            "2.64%",
            "1.07%",
            "2.43%",
            "2.49%",
            "2.69%",
            "39.242",
            "37.908",
            "3.40%",
            "330.656",
            "310.141",
            "6.20%",
            "385.7/387.7 MiB",
            "380.9/384.2 MiB",
            "391.3 MiB",
            "387.8 MiB",
            "111.773",
            "107.062",
            "4.22%",
            "250.744",
            "248.181",
            "1.02%",
            "8.282",
            "8.237",
            "16.184",
            "16.010",
            "1.08%",
            "649.1/643.9 MiB",
            "644.3/641.5 MiB",
            "4.280",
            "2.976",
            "1.438x",
            "4.246",
            "2.833",
            "1.4984x",
            "38.198",
            "37.951",
            "48.146",
            "47.135",
            "462.4/462.9 MiB",
            "434.5/437.5 MiB",
            "437.804",
            "424.065",
            "3.14%",
            "40.030",
            "39.448",
            "1.45%",
            "323.492",
            "317.922",
            "1.72%",
            "444.3 MiB",
            "406.0 MiB",
            "**1,448**",
            "3.7935",
            "3.6182",
            "4.62%",
            "1.0485x",
            "4.27%",
            "12.49%",
            "80.222",
            "79.549",
            "0.84%",
            "651.172",
            "658.203",
            "1.08%",
            "415.3 MiB",
            "409.9 MiB",
            "2.3 MiB",
            "0F119B82507E8ACB5FF0CF8EE4C407436671828B1981CC9FCDC824B2F34ACD19",
            "83.678",
            "83.419",
            "1.0031x",
            "661.375",
            "660.375",
            "0.15%",
            "393.6 MiB",
            "711.718",
            "47.936",
            "14.847x",
            "1263.307",
            "1173.588",
            "16.98/16.47",
            "36.341",
            "35.822",
            "1.0145x",
            "222.078",
            "220.789",
            "0.58%",
            "6.11",
            "6.16",
            "1.675",
            "1.131",
            "32.5%",
            "1.481x",
            "6.541",
            "6.504",
            "0.57%",
            "0.89%",
            "6.83%",
            "71.280",
            "70.645",
            "1.0090x",
            "416.906",
            "415.891",
            "0.24%",
            "1.326",
            "1.319 GiB",
            "383.5 MiB",
            "384.6 MiB",
            "0.966511",
            "0.891624",
            "7.75%",
            "34.113",
            "33.114",
            "303.11",
            "278.11",
            "82.260",
            "80.387",
            "1.0233x",
            "644.594",
            "640.391",
            "0.65%",
            "387.5",
            "418.7 MiB",
            "1.169541",
            "1.034975",
            "11.51%",
            "1.043777",
            "0.971152",
            "6.96%",
            "72.913607",
            "71.789921",
            "1.0157x",
            "426.328125",
            "422.593750",
            "373 MiB",
            "4,095",
            "4,096",
            "4,097",
            "7.253",
            "6.542",
            "9.80%",
            "10.87%",
            "39.453",
            "39.469",
            "38.478",
            "35.487",
            "7.77%",
            "215.016",
            "212.422",
            "1.21%",
            "368.3",
            "368.1 MiB",
            "361.6/366.3 MiB",
            "14.876",
            "11.135",
            "25.15%",
            "803.4",
            "407.0 MiB",
            "53.230",
            "44.055",
            "17.24%",
            "6.76",
            "7.41",
            "773.2",
            "402.4 MiB",
            "10.030",
            "6.679",
            "33.41%",
            "6.80",
            "8.88",
            "13.901",
            "13.877",
            "22.24%",
            "24.64%",
            "416.746",
            "336.922",
            "19.16%",
            "2578.125",
            "1742.188",
            "32.42%",
            "54.42",
            "52.76",
            "3.05%",
            "377.59",
            "373.18",
            "6.94",
            "7.07",
            "+0.13%",
            "64.106",
            "63.742",
            "0.57%",
            "342.547",
            "336.578",
            "1.74%",
            "52.405",
            "52.259",
            "0.28%",
            "392.438",
            "374.859",
            "4.48%",
            "33F39E01AD16CB2053AB6A4AF1F27064D90981AD1661F315C51B86E99E9F6E79",
            "50.687",
            "50.793",
            "-0.21%",
            "43.730",
            "42.872",
            "1.96%",
            "255.273",
            "251.797",
            "1.36%",
            "736.8/734.6 MiB",
            "ced6afb",
            "44.924",
            "44.985",
            "0.999x",
            "579,283,536",
            "541,701,824",
            "6.49%",
            "391,712,736",
            "373,960,808",
            "4.53%",
            "332.672",
            "333.734",
            "391.9/396.5 MiB",
            "614.3 MiB",
            "1.95",
            "1.18",
            "39.5%",
            "1.653x",
            "9.064",
            "8.503",
            "6.19%",
            "48.312",
            "46.609",
            "3.52%",
            "5.33",
            "5.48",
            "49.195",
            "47.886",
            "2.66%",
            "43.986",
            "42.862",
            "2.55%",
            "28.758",
            "28.226",
            "1.85%",
            "23.415",
            "23.422",
            "-0.03%",
            "2.452",
            "2.359",
            "3.81%",
            "1.040x",
            "12.063",
            "11.031",
            "8.55%",
            "8.904",
            "8.777",
            "1.43%",
            "47.219",
            "44.891",
            "4.93%",
            "363.8 MiB",
            "9.347",
            "9.035",
            "3.33%",
            "1.034x",
            "4.22%",
            "402.1",
            "414.7 MiB",
            "12.5 MiB",
            "2.290",
            "2.180",
            "4.80%",
            "8.438x",
            "88.15%",
            "46.359",
            "5.27",
            "414.9 MiB",
            "400.8 MiB",
            "387.9 MiB",
            "82.23",
            "42.30",
            "48.6%",
            "1.944x",
            "11.855",
            "11.620",
            "1.020x",
            "55.297",
            "57.422",
            "4.66",
            "4.94",
            "**1,319**",
            "12.667",
            "12.512",
            "1.012x",
            "58.438",
            "58.141",
            "4.61",
            "4.65",
            "72.241",
            "44.311",
            "2.378",
            "7.735x",
            "9.740",
            "9.480",
            "52.160",
            "48.590",
            "281.2-707.4 MiB",
            "754",
            "8.319 s",
            "7.345 s",
            "1.133x",
            "797.0",
            "724.9 MiB",
            "2626A0F82B89D7F4025F41600C034048E61F89F399B08746071968B6E3E619B5",
            "FFCB821C0E46885B7735A9ADCA1AA1ACD6454DC43C84ED671EC7B2EB31DA261C",
            "4,994,031,752",
            "4,611,843,896",
            "4,598,751,544",
            "566,944,304",
            "4,031,807,240",
            "87.67%",
            "8.72",
            "8.58",
            "8.710",
            "8.617",
            "69.475",
            "3.844 GiB",
            "711.6 MiB",
            "627.5/703.7 MiB",
            "12,187,912",
            "7.65%",
            "69.862",
            "1,479.6 MiB",
            "126.226",
            "110.873",
            "12.16%",
            "1,400.8 MiB",
            "5.492",
            "5.440",
            "77.234 MiB",
            "2.863362 GiB",
            "2.790004 GiB",
            "0.993 GiB",
            "65.17",
            "310.49",
            "268.52",
            "22.248",
            "22.113",
            "26.229/26.403",
            "4.325",
            "1.720 GiB",
            "1,524.33 MiB",
            "1.508/1.534 GiB",
            "3.861 s",
            "12.021 s",
            "3.114x",
            "8.052 s",
            "13.700 s",
            "1.701x",
            "3.964 s",
            "11.924 s",
            "3.008x",
            "3.379 s",
            "12.344 s",
            "3.653x",
            "3.152 s",
            "12.649 s",
            "4.013x",
            "6F4DD4ABE1D05A5030846DEA550758A79E7737D680A2B06024CFA06C83BF5185",
            "BB91833B7575C003AEC9853ED75D4CFF82C1125690B226E0A79D539B6594169C",
            "2F4C27FB9F3A9F4E8467BB49E89D660132DA5A2DCCC99AE897A072B1DD099EE5",
            "405.63 s",
            "402.88 s",
            "76.78 s",
            "215.66 s",
            "60.58 s",
            "244.95 s",
            "5.28x",
            "6.70x",
            "5,132",
            "620421120",
            "2219612160",
            "NumPy 2.4.6",
            "SciPy 1.18.0",
            "Numba 0.66.0",
            "python-soxr 1.1.0",
            "5.12 GiB",
            "1.96 GiB",
            "11.60",
            "4.228",
            "63.6%",
            "4.434",
            "16.516",
            "15.328",
            "4.6%",
            "7.2%",
            "18.6%",
            "1.314 GiB",
            "1.069 GiB",
            "3.63",
            "1.23 GiB",
            "1.13 GiB",
            "21.50",
            "20.86",
            "21.67",
            "21.54",
            "1.39/1.35 GiB",
            "52.51",
            "455 MB",
            "400,000",
            "1 KiB",
            "16.772",
            "16.178",
            "651.68",
            "47.25",
            "5.541",
            "5.537",
            "19.438",
            "23.39",
            "1.755",
            "14.892",
            "12.316",
            "5.684",
            "5.571",
            "18.891",
            "1.869",
            "21.22",
            "20.57",
            "6.3%",
            "4.134",
            "3.861",
            "622.63",
            "340.02",
            "21.588",
            "18.741",
            "5.579",
            "5.330",
            "19.297",
            "17.922",
            "7.1%",
            "13.854",
            "12.579",
            "12,611.83",
            "11,311.73",
            "5.49",
            "5.30",
            "19.23",
            "18.05",
            "12.580",
            "12.147",
            "11,309.71",
            "10,871.59",
            "5.209",
            "5.175",
            "18.188",
            "17.094",
            "5.165",
            "4.878",
            "18.172",
            "18.875",
            "21.31",
            "20.35",
            "21.84",
            "20.18",
            "4.98",
            "4.87",
            "18.20",
            "19.50",
            "20.451",
            "20.181",
            "20.483",
            "20.353",
            "6.01",
            "5.02",
            "18.86",
            "17.45",
            "20.48",
            "20.28",
            "20.61",
            "19.87",
            "79.88",
            "68.91",
            "77.17",
            "72.44",
            "2.05-2.08 GiB",
            "1.58-1.67 GiB",
            "2.95",
            "2.89",
            "4.831",
            "4.769",
            "19.83",
            "19.87",
            "4.687",
            "4.610",
            "12.422",
            "12.188",
            "3.813",
            "3.743",
            "14.469",
            "13.109",
            "15.281",
            "14.993",
            "12.655",
            "46.297",
            "12.601",
            "46.156",
            "1.319 GiB",
            "1.198 GiB",
            "2.639",
            "2.466",
            "2,469.42",
            "2,291.86",
            "15.272",
            "15.113",
            "12.560",
            "12.378",
            "28.937",
            "106.984",
            "28.296",
            "105.344",
            "1.076/0.766/1.025/0.726 GiB",
            "1.463 GiB",
            "2.465",
            "2.434",
            "2,291.20",
            "2,266.54",
            "24.7 MiB",
            "4.508",
            "4.556",
            "3.719",
            "3.696",
            "14.847",
            "14.904",
            "12.319",
            "12.361",
            "28.015",
            "107.828",
            "27.865",
            "108.547",
            "1.481 GiB",
            "1.465 GiB",
            "2.440",
            "2.384",
            "2,267.10",
            "2,207.39",
            "59.629 MiB",
            "4.475",
            "4.433",
            "3.694",
            "3.638",
            "15.104",
            "14.732",
            "12.206",
            "49.312",
            "46.094",
            "28.039",
            "28.553",
            "28.224",
            "28.308",
            "1.474/1.475 GiB",
            "2.360",
            "2.322",
            "2,197.06",
            "2,147.33",
            "12.250",
            "4.455",
            "4.366",
            "12.125",
            "3.721",
            "3.657",
            "15.719",
            "14.094",
            "12.180",
            "12.064",
            "47.922",
            "44.031",
            "26.916",
            "27.468",
            "27.398",
            "27.664",
            "1.484/1.481 GiB",
            "15.20",
            "15.53",
            "12.45",
            "12.68",
            "2.320069",
            "2.266559",
            "2,147.315",
            "2,086.828",
            "29.815 MiB",
            "4.461",
            "4.403",
            "12.781",
            "12.047",
            "3.706",
            "3.665",
            "14.406",
            "12.906",
            "12.196",
            "11.985",
            "46.047",
            "45.625",
            "27.566/27.877",
            "107.531/105.828",
            "28.120/27.263",
            "105.422/107.594",
            "1.355/1.474 GiB",
            "14.71",
            "14.76",
            "12.05",
            "12.26",
            "28.353/27.647",
            "AVX2/SSE4.1",
            "33.690/97.734",
            "32.805/93.609",
            "26.713",
            "26.760",
            "106.563",
            "105.266",
            "1.411/1.445 GiB",
            "4a67ae9",
            "13.991/41.492",
            "13.595/39.773",
            "11.152/48.508",
            "10.838/47.180",
            "1.14 GiB",
            "c51f059",
            "14.060/40.164",
            "13.598/40.438",
            "10.907/45.039",
            "10.771/43.414",
            "711.35",
            "257.61",
            "63.8%",
            "1.13 GiB",
            "16.30",
            "16.52",
            "35.84/39.54",
            "1.459/1.231/0.820/1.422",
            "333D051E361FE425EA893EE819129BB1CFC9249CF77E29746C94252F263D19D0",
            "20.311490",
            "2.623486",
            "2,240,768",
            "30,400",
            "98.64%",
            "2,493",
            "2,346",
            "5.89%",
            "1.71%",
            "1.06%",
            "0.63%",
            "20.241",
            "17.580",
            "2.82%",
            "3.00%",
            "1.66%",
            "d526ef5",
            "7F3434744E2120282C9888CF66AF730A184A103465561DE5A2B3F63B0022202F",
            "72.405",
            "62.752",
            "15.38%",
            "706 MiB",
            "37.905",
            "38.002",
            "294.055",
            "288.711",
            "111.461",
            "88.022",
            "21.03%",
            "0.994",
            "0.791",
            "20.43%",
            "209.767",
            "72.00/69.11/68.50",
            "721.87/1,304.73/601.61/864.18 MiB",
            "1,462.11 MiB",
            "13.900",
            "49.14%",
            "161.302",
            "125.900",
            "21.95%",
            "25.76%",
            "73.267",
            "7.038",
            "7.152",
            "1,244",
            "11.94",
            "10.68",
            "64.36/65.15",
            "56.31/56.05",
            "393.8/515.0",
            "11.15",
            "10.47",
            "6.67",
            "7.58",
            "55.851",
            "53.003",
            "1.918 GiB",
            "382.9/383.0 MiB",
            "1,295",
            "--use_saved_levels",
            "2b35b9e",
            "478D4CA77777B569273CF2846E7383C3FD38AE5E9B4B5D497DAB1280D611DF7D",
            "197.780",
            "153.813",
            "22.2%",
            "617.3 MiB",
            "398.9/405.9 MiB",
            "29.576",
            "28.673",
            "1.031x",
            "273.375",
            "275.297",
            "9.24",
            "9.60",
            "COMPATIBILITY_EVIDENCE.md"
        ];

        foreach ((string filename, string content) in overviews)
        {
            Assert.True(
                expectedOverviewCommands.SequenceEqual(
                    Captures(PowerShellBlockRegex(), content, "body")),
                $"{filename} does not contain the synchronized overview command blocks.");
            Assert.True(
                expectedOverviewPerformanceRows.SequenceEqual(PerformanceMetricRows(content)),
                $"{filename} does not contain the expected profile-matched performance matrix.");
            Assert.Contains(LatestPerformanceRunsMarker, content, StringComparison.Ordinal);
            foreach (string fact in overviewFacts)
            {
                Assert.Contains(fact, content, StringComparison.Ordinal);
            }
            Assert.DoesNotContain(
                "PERFORMANCE_TABLE_PENDING",
                content,
                StringComparison.Ordinal);
        }

        foreach ((string filename, string content) in details)
        {
            Assert.True(
                expectedDetailedCommands.SequenceEqual(
                    Captures(PowerShellBlockRegex(), content, "body")),
                $"{filename} does not contain the synchronized detailed command blocks.");
            Assert.True(
                expectedDetailedPerformanceRows.SequenceEqual(PerformanceMetricRows(content)),
                $"{filename} does not contain the expected detailed performance matrix.");
            Assert.True(
                expectedDetailedPerformanceRangeRows.SequenceEqual(
                    PerformanceRangeRows(content)),
                $"{filename} does not contain the expected detailed performance ranges.");
            Assert.Contains(LatestPerformanceRunsMarker, content, StringComparison.Ordinal);
            foreach (string fact in synchronizedFacts)
            {
                Assert.Contains(fact, content, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("**719**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**736**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**793**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**817**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**740**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**741**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**742**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**744**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**745**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**746**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**750**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**759**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**768**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**1,090**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**1,109**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**778**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**779**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**781**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**782**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**783**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**784**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**786**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**808**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**809**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**810**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**811**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**812**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**821**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("39.80", content, StringComparison.Ordinal);
        }

        string compatibilityEvidencePath = Path.Combine(
            RepositoryRoot(),
            "docs",
            "COMPATIBILITY_EVIDENCE.md");
        Assert.True(
            File.Exists(compatibilityEvidencePath),
            "The shared compatibility evidence document is missing.");
        string compatibilityEvidence = File.ReadAllText(compatibilityEvidencePath);
        Assert.Contains(
            "1,448 independently discoverable tests",
            compatibilityEvidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "1,415 independently discoverable tests",
            compatibilityEvidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "1,429 independently discoverable tests",
            compatibilityEvidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "1,430 independently discoverable tests",
            compatibilityEvidence,
            StringComparison.Ordinal);
        string workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            ".github",
            "workflows",
            "release-build.yml"));
        Assert.Contains(FullCiTestCommand, workflow, StringComparison.Ordinal);
        Assert.Contains(FinalRealRadix4AvxTestCommand, workflow, StringComparison.Ordinal);
        Assert.Contains(FinalRealRadix4AvxRequirement, workflow, StringComparison.Ordinal);
        Assert.Contains(
            FinalRealRadix4ScalarFallbackTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            SuperGaussianMaskAvxTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            SuperGaussianMaskAvxRequirement,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            SuperGaussianMaskAvxDisabledTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentCtiAvx2GatherTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentCtiAvx2GatherRequirement,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentCtiAvx2DisabledTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentCtiAvx2DisabledRequirement,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentCtiScalarFallbackTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentVhsSyncScalarFallbackTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            CurrentChromaAccAvxDisabledTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix8AvxDisabledTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix5AvxDisabledTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix5ScalarFallbackTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix3AvxDisabledTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix3AvxTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix3AvxRequirement,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix3ScalarFallbackTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix11Avx2DisabledTestCommand,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix11Avx2TestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix11Avx2DisabledTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix11Avx2Requirement,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix11AvxDisabledTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            PocketFftRadix11ScalarFallbackTestName,
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--minimum-expected-tests 1376",
            workflow,
            StringComparison.Ordinal);
        foreach (string filename in DetailedReadmeFiles)
        {
            Assert.True(
                File.Exists(Path.Combine(RepositoryRoot(), filename)),
                $"The detailed README is missing: {filename}");
        }
    }

    private static IReadOnlyDictionary<string, string> ReadReadmes(
        IEnumerable<string> filenames)
    {
        string root = RepositoryRoot();
        return filenames.ToDictionary(
            filename => filename,
            filename => File.ReadAllText(Path.Combine(root, filename)),
            StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "VHSDecodeDotNet.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string SingleCapture(Regex regex, string input, string group)
    {
        string[] captures = Captures(regex, input, group);
        return Assert.Single(captures);
    }

    private static string[] Captures(Regex regex, string input, string group)
    {
        return regex.Matches(input)
            .Select(match => match.Groups[group].Value.ReplaceLineEndings("\n"))
            .ToArray();
    }

    private static string[] PerformanceMetricRows(string content)
    {
        string table = SingleCapture(LatestPerformanceRegex(), content, "body");
        return table.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => PerformanceMetricRegex().Matches(line))
            .Where(matches => matches.Count > 0)
            .Select(matches => string.Join(
                " | ",
                matches.Select(match => match.Value)))
            .ToArray();
    }

    private static string[] PerformanceRangeRows(string content)
    {
        string table = SingleCapture(LatestPerformanceRangesRegex(), content, "body");
        return table.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (Line: line, Metrics: PerformanceRangeMetricRegex().Matches(line)))
            .Where(row => row.Metrics.Count > 0)
            .Select(row =>
            {
                Match worker = WorkerCountRegex().Match(row.Line);
                string label;
                if (worker.Success)
                {
                    label = $"threads {worker.Groups["workers"].Value}";
                }
                else
                {
                    Match defaultWorker = DefaultWorkerCountRegex().Match(row.Line);
                    Assert.True(defaultWorker.Success, "The default performance row lacks a worker count.");
                    label = $"default {defaultWorker.Groups["workers"].Value}";
                }

                return $"{label} | {string.Join(" | ", row.Metrics.Select(match => match.Value))}";
            })
            .ToArray();
    }

    [GeneratedRegex(@"<!-- README_SYNC: (?<version>[^ ]+) -->", RegexOptions.CultureInvariant)]
    private static partial Regex SyncMarkerRegex();

    [GeneratedRegex(@"<!-- SECTION: (?<id>[a-z]+) -->", RegexOptions.CultureInvariant)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(
        @"```powershell\r?\n(?<body>.*?)\r?\n```",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PowerShellBlockRegex();

    [GeneratedRegex(
        @"<!-- LATEST_PERFORMANCE_BEGIN -->\r?\n(?<body>.*?)\r?\n<!-- LATEST_PERFORMANCE_END -->",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LatestPerformanceRegex();

    [GeneratedRegex(
        @"<!-- LATEST_PERFORMANCE_RANGES_BEGIN -->\r?\n(?<body>.*?)\r?\n<!-- LATEST_PERFORMANCE_RANGES_END -->",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LatestPerformanceRangesRegex();

    [GeneratedRegex(@"\d+\.\d+(?: s|x|%)", RegexOptions.CultureInvariant)]
    private static partial Regex PerformanceMetricRegex();

    [GeneratedRegex(@"\d+\.\d+-\d+\.\d+ s", RegexOptions.CultureInvariant)]
    private static partial Regex PerformanceRangeMetricRegex();

    [GeneratedRegex(@"--threads\s+(?<workers>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerCountRegex();

    [GeneratedRegex(@"^\|[^|]*?(?<workers>\d+)[)）]\s*\|", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultWorkerCountRegex();
}
