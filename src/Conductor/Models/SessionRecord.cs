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
    /// <summary>SC4.3: commits this session landed in the plan's <c>satelliteRepos</c>, as
    /// <c>&lt;sha&gt; &lt;subject&gt; [&lt;label&gt;]</c> rows. Kept apart from <see cref="NewCommits"/>
    /// so every surface that reports "commits in this repo" keeps saying exactly that.</summary>
    public List<string> SatelliteCommits { get; set; } = new();
    /// <summary>SC4.3: each satellite's HEAD at session start, keyed by label — the marker the
    /// post-session diff is taken against.</summary>
    public Dictionary<string, string> SatelliteStartHeads { get; set; } = new(StringComparer.Ordinal);
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
