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

    /// <summary>K4.1: how full the context window ran, per turn — high water, mean and the number of
    /// API calls the sample is over. Null when the provider reported no per-turn usage (an old record,
    /// a fake agent, a provider without usage on the wire), which must not read as a measured zero.
    /// Every other token field here is an integral 30-50x larger than any window; this is the figure
    /// that actually drives the cache-read share of the bill. Persisted to <c>sessions.context_*</c>.</summary>
    public Conductor.Core.Events.ContextWindowStats? Context { get; set; }

    /// <summary>SC7.1 (devcontext #11): absolute paths this session wrote OUTSIDE the plan's repo and
    /// outside every declared satellite, deduped, capped. Collected from the structured tool events —
    /// impossible before SC7.1, because a <c>file_path</c> past the old 150-character argument cut was
    /// never captured at all. The session verdict reports the count; the paths are kept so it can name
    /// the first few.</summary>
    public List<string> OutsideRepoWrites { get; set; } = new();

    /// <summary>SC7.2 (devcontext #10): what this session did, folded live from its structured tool
    /// events — tool mix, files written with counts, board claims, bg-start purposes as a storyline,
    /// notable build/test commands. Accumulated as the session runs, so a session killed mid-flight
    /// still carries a digest of what it managed; persisted to <c>sessions.digest</c> in run.db and
    /// served on <c>/sessions</c>.</summary>
    public Conductor.Core.Events.SessionDigest Digest { get; set; } = new();

    /// <summary>K1.2: whether this session's cooperative soft break was raised, how many times the
    /// notice was actually put in front of the agent, and whether the agent obeyed it (ended under
    /// its ceiling without the engine's hard stop). Null when the session had no token ceiling or
    /// never crossed the threshold — which is a different fact from "was nudged and ignored it", and
    /// the two must not read the same. Persisted to <c>sessions.soft_break</c>.</summary>
    public Conductor.Core.SoftBreak.Outcome? SoftBreak { get; set; }

    [JsonIgnore] public long TokensTotal =>
        (TokensInput ?? 0) + (TokensOutput ?? 0) + (TokensReasoning ?? 0) + (TokensCacheRead ?? 0);
}
