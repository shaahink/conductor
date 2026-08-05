using System.Text.Json.Serialization;

namespace Conductor.Core.Update;

/// <summary>
/// SC8.3 — the shape of GitHub's "latest release" document, narrowed to the five fields an update
/// actually needs. Deliberately a plain DTO with explicit <see cref="JsonPropertyNameAttribute"/>
/// names rather than a naming policy: the wire is snake_case, the rest of this codebase is camelCase,
/// and a policy set in the wrong direction fails as a silent null rather than a parse error.
/// </summary>
public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; } = [];

    /// <summary>The first asset whose name matches exactly, or null. Case-insensitive because the
    /// workflow's matrix names and a hand-uploaded asset have disagreed on case before.</summary>
    public GithubAsset? Asset(string name) =>
        Assets.Find(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One file attached to a release. <c>browser_download_url</c> is the only URL that works
/// unauthenticated for a public repo; the <c>url</c> API form needs an Accept header and a token.</summary>
public sealed class GithubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>Source-generated, so the update path keeps working under trimming/AOT publish — which is
/// how the release archives are built (<c>PublishSingleFile</c>, self-contained).</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GithubRelease))]
public sealed partial class ReleaseJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
