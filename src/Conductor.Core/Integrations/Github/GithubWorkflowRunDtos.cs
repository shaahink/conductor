using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>CH1.3 — one run of one workflow. <see cref="HeadSha"/> is the point of the whole call:
/// "CI is green on this branch" and "CI is green on THIS COMMIT" are different claims, and only the
/// second one says anything about the tree a run is building on.</summary>
public sealed record GithubWorkflowRun
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("head_sha")] public string HeadSha { get; init; } = "";
    [JsonPropertyName("head_branch")] public string HeadBranch { get; init; } = "";
    /// <summary><c>queued</c>, <c>in_progress</c>, <c>completed</c>.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    /// <summary><c>success</c>, <c>failure</c>, <c>cancelled</c>, … and <c>null</c> while the run has
    /// not finished — which is why it is nullable here rather than defaulted to a word.</summary>
    [JsonPropertyName("conclusion")] public string? Conclusion { get; init; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; init; } = "";
}

/// <inheritdoc cref="GithubWorkflowRun"/>
public sealed record GithubWorkflowRunList
{
    [JsonPropertyName("total_count")] public int TotalCount { get; init; }
    [JsonPropertyName("workflow_runs")] public List<GithubWorkflowRun> Runs { get; init; } = [];
}
