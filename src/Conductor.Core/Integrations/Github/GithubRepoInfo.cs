using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>A repository, read for one fact: private or not. Not a SARIF document — it is what the
/// upload path consults to say WHY code scanning refused, so it lives on its own.</summary>
public sealed record GithubRepoInfo
{
    [JsonPropertyName("full_name")] public string FullName { get; init; } = "";
    [JsonPropertyName("private")] public bool Private { get; init; }
    [JsonPropertyName("visibility")] public string? Visibility { get; init; }
    [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }
}
