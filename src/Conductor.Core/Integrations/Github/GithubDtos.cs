using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>One issue as GitHub returns it. Only the fields the mirror decides from — an issue
/// document is large and every field deserialized is a field that can change shape under us.</summary>
public sealed record GithubIssue
{
    [JsonPropertyName("number")] public int Number { get; init; }

    /// <summary>DV6.2 — the GraphQL global id. Projects v2 adds an item by CONTENT id, and REST is
    /// the only place this integration ever learns one: every issue document GitHub returns carries
    /// it, so the project half costs no extra request to find out what it is adding.</summary>
    [JsonPropertyName("node_id")] public string NodeId { get; init; } = "";

    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "open";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = "";
    [JsonPropertyName("labels")] public List<GithubLabelRef> Labels { get; init; } = [];
    [JsonPropertyName("milestone")] public GithubMilestoneRef? Milestone { get; init; }

    /// <summary>A pull request is served by <c>GET /issues</c> too, and is not a card of ours.
    /// Present-and-non-null is GitHub's own way of saying "this row is a PR".</summary>
    [JsonPropertyName("pull_request")] public object? PullRequest { get; init; }

    public bool IsOpen => string.Equals(State, "open", StringComparison.Ordinal);

    public IReadOnlyList<string> LabelNames => Labels.ConvertAll(l => l.Name);
}

/// <summary>A label on an issue. GitHub returns objects here, not strings, and accepts strings on
/// the way in — the asymmetry is why this type exists at all.</summary>
public sealed record GithubLabelRef
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

/// <summary>One comment on an issue — the diary's unit.</summary>
public sealed record GithubComment
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
}
