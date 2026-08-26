using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>A milestone as GitHub returns it — a stage, on the mirror.</summary>
public sealed record GithubMilestoneRef
{
    [JsonPropertyName("number")] public int Number { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "open";
}

/// <summary>
/// KS9.1 — source-generated (de)serialization for every GitHub payload, on the
/// <c>ReleaseJsonContext</c> pattern. Reflection-based <c>JsonSerializer</c> is what an AOT/trimmed
/// publish silently loses; a context makes the shapes a build-time fact.
///
/// <para><c>WhenWritingNull</c> is not cosmetic here: it is what makes a PATCH that only changes
/// <c>state</c> send only <c>state</c>, and therefore what makes upsert-never-clobber true on the
/// wire rather than in a comment.</para>
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GithubIssue))]
[JsonSerializable(typeof(List<GithubIssue>))]
[JsonSerializable(typeof(GithubComment))]
[JsonSerializable(typeof(List<GithubComment>))]
[JsonSerializable(typeof(GithubMilestoneRef))]
[JsonSerializable(typeof(List<GithubMilestoneRef>))]
[JsonSerializable(typeof(GithubIssueRequest))]
[JsonSerializable(typeof(GithubCommentRequest))]
[JsonSerializable(typeof(GithubMilestoneRequest))]
[JsonSerializable(typeof(GithubSarifRequest))]
[JsonSerializable(typeof(GithubSarifUpload))]
[JsonSerializable(typeof(GithubSarifStatus))]
[JsonSerializable(typeof(GithubRepoInfo))]
public sealed partial class GithubJsonContext : JsonSerializerContext;
