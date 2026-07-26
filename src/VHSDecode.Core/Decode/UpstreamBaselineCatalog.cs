using System.Reflection;
using System.Text.Json;

namespace VHSDecode.Core.Decode;

public sealed record UpstreamBehaviorBaseline(
    string Name,
    string? Release,
    string Commit,
    string? Source,
    IReadOnlyDictionary<string, string> Algorithms);

public sealed class UpstreamBaselineCatalog
{
    private const string ResourceName = "VHSDecode.Core.Decode.Resources.upstream-baseline.json";
    private static readonly Lazy<UpstreamBaselineCatalog> LazyDefault = new(Load);
    private readonly IReadOnlyDictionary<string, UpstreamBehaviorBaseline> _profiles;

    private UpstreamBaselineCatalog(BaselineCatalogDto catalog)
    {
        Repository = catalog.Repository;
        _profiles = catalog.Profiles.ToDictionary(
            profile => profile.Name,
            profile => new UpstreamBehaviorBaseline(
                profile.Name,
                profile.Release,
                profile.Commit,
                profile.Source,
                profile.Algorithms),
            StringComparer.Ordinal);
    }

    public static UpstreamBaselineCatalog Default => LazyDefault.Value;

    public string Repository { get; }

    public UpstreamBehaviorBaseline Get(UpstreamBehaviorProfile profile)
    {
        string name = UpstreamBehaviorProfileParser.ToCommandLineValue(profile);
        return _profiles.TryGetValue(name, out UpstreamBehaviorBaseline? baseline)
            ? baseline
            : throw new InvalidOperationException($"Embedded upstream baseline '{name}' was not found.");
    }

    private static UpstreamBaselineCatalog Load()
    {
        Assembly assembly = typeof(UpstreamBaselineCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        BaselineCatalogDto catalog = JsonSerializer.Deserialize<BaselineCatalogDto>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The embedded upstream baseline catalog was empty.");
        if (string.IsNullOrWhiteSpace(catalog.Repository) || catalog.Profiles.Count == 0)
        {
            throw new InvalidOperationException("The embedded upstream baseline catalog was incomplete.");
        }

        return new UpstreamBaselineCatalog(catalog);
    }

    private sealed class BaselineCatalogDto
    {
        public string Repository { get; set; } = string.Empty;

        public List<BaselineDto> Profiles { get; set; } = [];
    }

    private sealed class BaselineDto
    {
        public string Name { get; set; } = string.Empty;

        public string? Release { get; set; }

        public string Commit { get; set; } = string.Empty;

        public string? Source { get; set; }

        public Dictionary<string, string> Algorithms { get; set; } = new(StringComparer.Ordinal);
    }
}
