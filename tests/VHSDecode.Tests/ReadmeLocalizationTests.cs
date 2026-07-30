using System.Text.RegularExpressions;
using Xunit;

namespace VHSDecode.Tests;

public sealed partial class ReadmeLocalizationTests
{
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

        string[] overviewFacts =
        [
            "43155200da87c0d49eb37d8ec09b1372075ee8e4",
            "11.0.100-preview.6.26359.118",
            "**1,126**",
            "--compat-version",
            "current",
            "--dsp-backend",
            "ipp-fast",
            "Exact + v0.4.0",
            "Exact + current",
            "IPP-fast + v0.4.0",
            "IPP-fast + current",
            "16.983 s",
            "5.504 s",
            "7.299 s",
            "4.887x",
            "3.524x",
            "g4315520",
            "--threads 0"
        ];

        string[] synchronizedFacts =
        [
            "43155200da87c0d49eb37d8ec09b1372075ee8e4",
            "11.0.100-preview.6.26359.118",
            "**1,126**",
            "2626A0F82B89D7F4025F41600C034048E61F89F399B08746071968B6E3E619B5",
            "FFCB821C0E46885B7735A9ADCA1AA1ACD6454DC43C84ED671EC7B2EB31DA261C",
            "4,994,031,752",
            "4,611,843,896",
            "7.65%",
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
            "98ADB0ED3F5EF086AC2A189F302101C751C68F0390123817EAF5452DD83BE7A1",
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
            "30.253",
            "4.586x",
            "3.845x",
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
            "--use_saved_levels",
            "COMPATIBILITY_EVIDENCE.md"
        ];

        foreach ((string filename, string content) in overviews)
        {
            Assert.True(
                expectedOverviewCommands.SequenceEqual(
                    Captures(PowerShellBlockRegex(), content, "body")),
                $"{filename} does not contain the synchronized overview command blocks.");
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
            Assert.DoesNotContain("13.88", content, StringComparison.Ordinal);
            Assert.DoesNotContain("39.80", content, StringComparison.Ordinal);
            Assert.DoesNotContain("53.23", content, StringComparison.Ordinal);
        }

        Assert.True(
            File.Exists(Path.Combine(RepositoryRoot(), "docs", "COMPATIBILITY_EVIDENCE.md")),
            "The shared compatibility evidence document is missing.");
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

    [GeneratedRegex(@"<!-- README_SYNC: (?<version>[^ ]+) -->", RegexOptions.CultureInvariant)]
    private static partial Regex SyncMarkerRegex();

    [GeneratedRegex(@"<!-- SECTION: (?<id>[a-z]+) -->", RegexOptions.CultureInvariant)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(
        @"```powershell\r?\n(?<body>.*?)\r?\n```",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PowerShellBlockRegex();
}
