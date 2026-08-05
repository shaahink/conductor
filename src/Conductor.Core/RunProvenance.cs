using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// K3.3 — the limits that actually governed a run, or one session of it, frozen at the moment they
/// governed it.
/// <para>Limits are editable in flight: the Plan tab writes <c>LimitsConfig</c> and triggers a live
/// reload, so the plan file on disk describes the LAST limits, not the ones session 3 ran under. The
/// Sarban run raised its session cap at session 9 and the only surviving trace is the shape of a
/// token curve — which is a guess, and this record is the reason it stops being one.</para>
/// <para>Deliberately a flat snapshot of six numbers and not the whole <see cref="LimitsConfig"/>:
/// this is written once per session for the life of the database, and the four watchdog timeouts do
/// not change what a run cost.</para>
/// </summary>
public sealed record RunLimitsSnapshot
{
    /// <summary>Per-session hard token ceiling (<c>MaxSessionTokens</c>). Null = no ceiling.</summary>
    public long? SessionTokenCap { get; init; }

    /// <summary>The EFFECTIVE soft-break ratio, not the raw nullable field — an unset ratio still
    /// nudges at 0.8, so recording null here would read as "was never nudged". Null only when there
    /// is no ceiling for a ratio to be a fraction of.</summary>
    public double? NudgeRatio { get; init; }

    /// <summary>Where the nudge actually fires, in tokens. Derived, but stored: the ratio alone
    /// forces a reader to remember which cap it applied to.</summary>
    public long? NudgeTokens { get; init; }

    /// <summary>Whole-run cost cap in USD (<c>MaxRunCostUsd</c>). Null = uncapped.</summary>
    public decimal? RunCostCapUsd { get; init; }

    /// <summary>Whole-run token cap (<c>MaxRunTokens</c>). Null = uncapped.</summary>
    public long? RunTokenCap { get; init; }

    /// <summary>Session count cap (<c>MaxSessions</c>), the one that parks the loop. Null = uncapped.</summary>
    public int? SessionCap { get; init; }

    /// <summary>Concurrent analysis lanes (<c>MaxConcurrentLanes</c>).</summary>
    public int LaneConcurrency { get; init; }

    /// <summary>Reads the six numbers off a live config. The ratio fallback is the same expression
    /// <c>PromptBuilder.Budget.cs:25</c> and <c>SessionRunner.Mcp.cs:120</c> use — one wrong default
    /// here and the record would disagree with the rail it describes.</summary>
    public static RunLimitsSnapshot From(LimitsConfig limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var cap = limits.MaxSessionTokens is { } c and > 0 ? c : (long?)null;
        double? ratio = cap is null
            ? null
            : (limits.SoftBreakRatio is { } r and > 0 and <= 1.0 ? r : 0.8);
        return new RunLimitsSnapshot
        {
            SessionTokenCap = cap,
            NudgeRatio = ratio,
            NudgeTokens = cap is { } cc && ratio is { } rr ? (long)(cc * rr) : null,
            RunCostCapUsd = limits.MaxRunCostUsd,
            RunTokenCap = limits.MaxRunTokens,
            SessionCap = limits.MaxSessions is { } s and > 0 ? s : null,
            LaneConcurrency = limits.MaxConcurrentLanes,
        };
    }

    /// <summary>One line for a human: what was capped, and where the nudge sat.</summary>
    public string Describe()
    {
        var parts = new List<string>(4)
        {
            SessionTokenCap is { } cap
                ? $"cap {Millions(cap)} · nudge {NudgeRatio?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-"}"
                  + (NudgeTokens is { } n ? $" ({Millions(n)})" : "")
                : "cap none",
        };
        if (RunCostCapUsd is { } cost) parts.Add($"run ≤ ${cost.ToString("0.##", CultureInfo.InvariantCulture)}");
        if (RunTokenCap is { } rt) parts.Add($"run ≤ {Millions(rt)}");
        if (SessionCap is { } sc) parts.Add($"{sc.ToString(CultureInfo.InvariantCulture)} sessions");
        parts.Add($"lanes {LaneConcurrency.ToString(CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }

    private static string Millions(long tokens) => tokens >= 1_000_000
        ? (tokens / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M"
        : (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a stored snapshot. Tolerant by design — a row written by a future engine with
    /// an extra field, or a torn one, must not take down the history browser.</summary>
    public static RunLimitsSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RunLimitsSnapshot>(json, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// K3.3 — which engine produced a run: version, commit, and whether the tree it was built from was
/// clean. <see cref="BuildInfo"/> already computes all three for <c>conductor doctor</c>; this is the
/// shape that gets persisted, so a run record can answer the question after the binary is gone.
/// </summary>
/// <param name="Version">Informational version, e.g. <c>0.3.1-alpha.0.6</c>.</param>
/// <param name="Commit">Commit sha the binary was built from, or <c>unknown</c>.</param>
/// <param name="Dirty">The working tree carried uncommitted changes at build time.</param>
public readonly record struct EngineStamp(string Version, string Commit, bool Dirty)
{
    /// <summary>The running engine's stamp.</summary>
    public static EngineStamp Current => From(BuildInfo.Current);

    public static EngineStamp From(BuildInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new EngineStamp(info.Version, info.CommitSha, info.Dirty);
    }

    /// <summary>The single-string form stored per session and printed by <c>conductor history</c>:
    /// <c>0.3.1-alpha.0.6+98a426af63d6.dirty</c>.</summary>
    public string Full => string.IsNullOrEmpty(Commit) || Commit == BuildInfo.UnknownCommit
        ? Version
        : Dirty ? $"{Version}+{Commit}.dirty" : $"{Version}+{Commit}";

    /// <summary>The inverse of <see cref="Full"/>: turns a stored stamp string back into its three
    /// parts. Used to read the one-string form the <c>sessions.engine</c> column keeps, and to accept
    /// a bare version from a caller that has nothing better (which is honest — it yields
    /// <c>unknown</c> for the commit rather than inventing one).</summary>
    public static EngineStamp Parse(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return new EngineStamp("", BuildInfo.UnknownCommit, false);
        var text = full.Trim();
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return new EngineStamp(text, BuildInfo.UnknownCommit, false);
        var suffix = text[(plus + 1)..];
        var dirty = suffix.EndsWith(".dirty", StringComparison.OrdinalIgnoreCase);
        var sha = dirty ? suffix[..^".dirty".Length] : suffix;
        return new EngineStamp(text[..plus],
            string.IsNullOrWhiteSpace(sha) ? BuildInfo.UnknownCommit : sha, dirty);
    }

    /// <summary>Renders a stored triple the same way, so a row read back from the database and a
    /// live stamp print identically. Null version → null, which prints as "unrecorded".</summary>
    public static string? Format(string? version, string? commit, bool? dirty)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (string.IsNullOrWhiteSpace(commit) || commit == BuildInfo.UnknownCommit) return version;
        return dirty == true ? $"{version}+{commit}.dirty" : $"{version}+{commit}";
    }
}
