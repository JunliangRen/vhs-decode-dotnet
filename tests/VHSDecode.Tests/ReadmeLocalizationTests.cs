using System.Text.RegularExpressions;
using Xunit;

namespace VHSDecode.Tests;

public sealed partial class ReadmeLocalizationTests
{
    private static readonly string[] ReadmeFiles =
    [
        "README.md",
        "README.zh-CN.md",
        "README.ja.md"
    ];

    private static readonly string[] ExpectedSections =
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
        IReadOnlyDictionary<string, string> readmes = ReadReadmes();
        string expectedMarker = SingleCapture(
            SyncMarkerRegex(),
            readmes["README.md"],
            "version");

        foreach ((string filename, string content) in readmes)
        {
            Assert.Equal(
                expectedMarker,
                SingleCapture(SyncMarkerRegex(), content, "version"));
            Assert.True(
                ExpectedSections.SequenceEqual(Captures(SectionRegex(), content, "id")),
                $"{filename} does not contain the synchronized section sequence.");
            Assert.Contains("[English](README.md)", content, StringComparison.Ordinal);
            Assert.Contains("[简体中文](README.zh-CN.md)", content, StringComparison.Ordinal);
            Assert.Contains("[日本語](README.ja.md)", content, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "Localized READMEs share commands and release facts")]
    public void LocalizedReadmesShareCommandsAndReleaseFacts()
    {
        IReadOnlyDictionary<string, string> readmes = ReadReadmes();
        string[] expectedCommands = Captures(
            PowerShellBlockRegex(),
            readmes["README.md"],
            "body");
        Assert.Equal(3, expectedCommands.Length);

        string[] synchronizedFacts =
        [
            "43155200da87c0d49eb37d8ec09b1372075ee8e4",
            "11.0.100-preview.6.26359.118",
            "**814**",
            "4.325",
            "1.720 GiB",
            "1,524.33 MiB",
            "1.508/1.534 GiB",
            "4.646 s",
            "13.112 s",
            "2.82x",
            "3.779 s",
            "14.046 s",
            "3.72x",
            "64C518A03B208F7CF950916BC01A997021CB0F76B3D6F131FBEE74E9035FD30C",
            "70112719879FB64FA95DC8F3ED6E5FA335D4F8B62C50FC2AF3C26D2C2098F26F",
            "C223671830D0105271F24172923B280A96C8D0D427567C49E9C0E562D38FA881",
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
            "--use_saved_levels",
            "docs/COMPATIBILITY_EVIDENCE.md"
        ];

        foreach ((string filename, string content) in readmes)
        {
            Assert.True(
                expectedCommands.SequenceEqual(Captures(PowerShellBlockRegex(), content, "body")),
                $"{filename} does not contain the synchronized PowerShell command blocks.");
            foreach (string fact in synchronizedFacts)
            {
                Assert.Contains(fact, content, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("**719**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**736**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**793**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**740**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**741**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**742**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**744**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**745**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**746**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**750**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**759**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("**768**", content, StringComparison.Ordinal);
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
            Assert.DoesNotContain("13.88", content, StringComparison.Ordinal);
            Assert.DoesNotContain("39.80", content, StringComparison.Ordinal);
            Assert.DoesNotContain("53.23", content, StringComparison.Ordinal);
        }

        Assert.True(
            File.Exists(Path.Combine(RepositoryRoot(), "docs", "COMPATIBILITY_EVIDENCE.md")),
            "The shared compatibility evidence document is missing.");
    }

    private static IReadOnlyDictionary<string, string> ReadReadmes()
    {
        string root = RepositoryRoot();
        return ReadmeFiles.ToDictionary(
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
