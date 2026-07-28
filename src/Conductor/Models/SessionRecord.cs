using System.Text.Json.Serialization;

namespace Conductor.Models;

public sealed class SessionRecord
{
    public int Number { get; set; }
    public string Stage { get; set; } = "";
    public SessionKind Kind { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public SessionOutcome? Outcome { get; set; }
    public string ClaudeSessionId { get; set; } = "";
    public int ResumeCount { get; set; }
    public List<string> NewCommits { get; set; } = new();
    public List<string> NewlyDone { get; set; } = new();
    public string GateSummary { get; set; } = "";
    public decimal? CostUsd { get; set; }
    public decimal? OverheadCostUsd { get; set; }
    public int? NumTurns { get; set; }
    public long? TokensInput { get; set; }
    public long? TokensOutput { get; set; }
    public long? TokensReasoning { get; set; }
    public long? TokensCacheRead { get; set; }
    public int Attempt { get; set; }
    public string ResultSummary { get; set; } = "";

    [JsonIgnore] public long TokensTotal =>
        (TokensInput ?? 0) + (TokensOutput ?? 0) + (TokensReasoning ?? 0) + (TokensCacheRead ?? 0);
}
