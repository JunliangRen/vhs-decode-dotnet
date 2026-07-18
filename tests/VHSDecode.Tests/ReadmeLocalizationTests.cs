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
            "**719**",
            "2.346 s",
            "7.193 s",
            "1.651 s",
            "5.865 s",
            "5.12 GiB",
            "1.96 GiB",
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

            Assert.DoesNotContain("**717**", content, StringComparison.Ordinal);
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
