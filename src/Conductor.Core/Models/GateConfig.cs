using System.Text.Json.Serialization;

namespace Conductor.Models;

public sealed class GateConfig
{
    public string Name { get; set; } = "";
    /// <summary>Command line to execute with real exit-code capture. Shell is selected by
    /// <see cref="Shell"/> (default: <c>powershell</c> on Windows, <c>bash</c> on non-Windows).</summary>
    public string Command { get; set; } = "";
    /// <summary>Shell to execute the command (<c>powershell</c>, <c>bash</c>, <c>sh</c>).
    /// Default auto-detected: <c>powershell</c> on Windows, <c>bash</c> everywhere else.</summary>
    public string? Shell { get; set; }
    /// <summary>Working dir relative to repo root (default: repo root).</summary>
    public string? Cwd { get; set; }
    /// <summary>Optional gates report but never block.</summary>
    public bool Optional { get; set; }
    /// <summary>Skip the gate while this repo-relative path does not exist yet.</summary>
    public string? SkipIfMissing { get; set; }
    /// <summary>"fast" gates run per-session under perPhase policy; "full" gates run at phase end
    /// (and every session under perSession policy); "truth" gates are per-stage product-level
    /// assertions that run at phase confirmation only. Default "full".</summary>
    public string Tier { get; set; } = "full";
    /// <summary>KS4.1: <c>visible</c> (default) or <c>holdout</c>. A holdout gate runs only in the
    /// engine's own verdict-time battery and its name, command and output never reach the coding
    /// agent — see <see cref="GateVisibility"/> for the contract and why it is enforced in the
    /// runner rather than at each rendering surface.</summary>
    public string Visibility { get; set; } = GateVisibility.Visible;
    /// <summary>KS4.2: <c>standard</c> (default) or <c>regression</c>. A regression gate carries
    /// PASS-TO-PASS semantics on top of its exit code — see <see cref="GateClass"/>.</summary>
    public string Class { get; set; } = GateClass.Standard;
    /// <summary>KS4.2: how a <see cref="GateClass.Regression"/> gate's run is read for the set of
    /// checks that passed. Required for that class, refused at plan load without it.</summary>
    public PassSetConfig? PassSet { get; set; }
    /// <summary>Gates sharing a truthy parallel flag run concurrently within their battery.</summary>
    public bool Parallel { get; set; }
    /// <summary>If set, this gate only runs while the current stage id is in this list (doc-scoped
    /// gates, e.g. mcp-qa on MCP phases only). Empty/null = runs on every stage.</summary>
    public List<string>? Stages { get; set; }
    /// <summary>If set, this gate only runs when the current stage's Kind field matches one of
    /// these values (e.g. ["deliver"] to skip on docs-only stages). Empty/null = runs on every
    /// stage kind. Applies in addition to the Stages filter.</summary>
    public List<string>? StageKinds { get; set; }
    /// <summary>Repo-relative path to a file or directory. If it exists and its last-write time is
    /// newer than the most recent commit touching source files, the gate is skipped (cached
    /// freshness). E.g. "src/Conductor/bin/" — skips dotnet build if the output dir is fresh.</summary>
    public string? SkipIfFresh { get; set; }
    /// <summary>SC4.3: extra inputs whose newest write time joins this gate's result-cache key.
    /// The cache normally keys on the gate's working-directory HEAD, which is the right answer when
    /// the inputs are git-tracked; declare watch paths when they are not (a generated source tree, a
    /// vendored drop) so a change to them still invalidates the gate's cached pass. Paths are
    /// repo-relative or absolute. Empty/null = HEAD alone, unchanged.</summary>
    public List<string>? WatchPaths { get; set; }
    public int TimeoutMinutes { get; set; } = 20;

    /// <summary>KS4.1: engine-only. The predicate every gate-enumerating surface outside
    /// <see cref="Conductor.Core.GateRunner"/> must respect — use
    /// <see cref="GateVisibility.VisibleOnly"/> rather than testing this by hand.</summary>
    [JsonIgnore] public bool IsHoldout => Visibility.Equals(GateVisibility.Holdout, StringComparison.OrdinalIgnoreCase);

    /// <summary>KS4.2: this gate's exit code is not the whole of its verdict — the set of checks it
    /// reported passing is compared against the last set it reported. See <see cref="GateClass"/>.</summary>
    [JsonIgnore] public bool IsRegression => Class.Equals(GateClass.Regression, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public bool IsFast => Tier.Equals("fast", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsTruth => Tier.Equals("truth", StringComparison.OrdinalIgnoreCase);
    /// <summary>Truth gates are excluded from per-session fast-only batteries; they run at phase
    /// confirmation alongside full-tier gates.</summary>
    [JsonIgnore] public bool IsFullOrTruth => IsTruth || Tier.Equals("full", StringComparison.OrdinalIgnoreCase);

    public bool AppliesToStage(string? stageId)
        => Stages is not { Count: > 0 } || (stageId != null && Stages.Contains(stageId, StringComparer.OrdinalIgnoreCase));

    public bool AppliesToStageKind(string? stageKind)
        => StageKinds is not { Count: > 0 } || (stageKind != null && StageKinds.Contains(stageKind, StringComparer.OrdinalIgnoreCase));
}
