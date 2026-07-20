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
            "**793**",
            "4.908 s",
            "11.788 s",
            "2.40x",
            "4.159 s",
            "12.011 s",
            "2.89x",
            "857315FEC19C3F8D364896CDB4FC3AA26769D86D6E825DE095845EF6647C44A9",
            "CE54E7F6050E1445E0E205867CD8C3B912B4BB16708D31C303FABC8B04C3AA3B",
            "D0BFA50DD75ABACAE1BAD7E275BB2FD2159230F6FDF98461F045F3784BDF6DD8",
            "5.12 GiB",
            "1.96 GiB",
            "11.60",
            "4.97",
            "57.2%",
            "5.33",
            "17.11",
            "15.94",
            "6.8%",
            "3.2",
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
