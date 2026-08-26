using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>CH1.3 — one workflow as GitHub lists it. <see cref="State"/> is the field that decides
/// whether its verdict counts: a <c>disabled_manually</c> or <c>disabled_inactivity</c> workflow has
/// stale runs that mean nothing, and reading them as a verdict is a way to believe a green that is
/// a year old.</summary>
public sealed record GithubWorkflow
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "";
}

/// <inheritdoc cref="GithubWorkflow"/>
public sealed record GithubWorkflowList
{
    [JsonPropertyName("total_count")] public int TotalCount { get; init; }
    [JsonPropertyName("workflows")] public List<GithubWorkflow> Workflows { get; init; } = [];
}
