using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>The body of a create-or-patch issue call. Every field is nullable and null fields are
/// omitted (<c>JsonIgnoreCondition.WhenWritingNull</c> on the context), so a PATCH that only closes
/// an issue sends only <c>state</c> — an "upsert" that resent the whole document would CLOBBER a
/// human's edits to the title or body, which is precisely the semantics KS9.1 rules out.</summary>
public sealed record GithubIssueRequest
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("labels")] public List<string>? Labels { get; init; }
    [JsonPropertyName("milestone")] public int? Milestone { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
}

/// <summary>The body of a create-comment call.</summary>
public sealed record GithubCommentRequest
{
    [JsonPropertyName("body")] public string Body { get; init; } = "";
}

/// <summary>The body of a create-milestone call. Milestones are how a stage becomes visible on the
/// mirror, and they are created lazily — the first card of a stage mints its stage.</summary>
public sealed record GithubMilestoneRequest
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
}
