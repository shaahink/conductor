using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>The body of a code-scanning SARIF upload. <c>Sarif</c> is gzip-then-base64, never the
/// JSON itself.</summary>
public sealed record GithubSarifRequest
{
    [JsonPropertyName("commit_sha")] public string CommitSha { get; init; } = "";
    [JsonPropertyName("ref")] public string Ref { get; init; } = "";
    [JsonPropertyName("sarif")] public string Sarif { get; init; } = "";
    [JsonPropertyName("checkout_uri")] public string? CheckoutUri { get; init; }
    [JsonPropertyName("validate")] public bool? Validate { get; init; }
}

/// <summary>GitHub's 202: the receipt, and where to ask what became of it.</summary>
public sealed record GithubSarifUpload
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

/// <summary>What became of an upload. <c>ProcessingStatus</c> is pending / complete / failed;
/// <c>Errors</c> is what a failed document got wrong, and it is the only place that says so.</summary>
public sealed record GithubSarifStatus
{
    [JsonPropertyName("processing_status")] public string ProcessingStatus { get; init; } = "";
    [JsonPropertyName("analyses_url")] public string? AnalysesUrl { get; init; }
    [JsonPropertyName("errors")] public List<string>? Errors { get; init; }
}
