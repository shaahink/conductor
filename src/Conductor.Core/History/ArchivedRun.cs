namespace Conductor.Core.History;

/// <summary>One run as the archive sees it. Timestamps are the raw stored strings.</summary>
public sealed record ArchivedRun(
    string RunId, string PlanName, string Repo, string? Branch, string? EngineVersion,
    string Status, string? StartedUtc, string? EndedUtc, string? LastActivityUtc,
    int Sessions, decimal CostUsd, long Tokens,
    string? EngineCommit = null, bool? EngineDirty = null, string? LimitsJson = null,
    // KS1.1 — the launch snapshot and the provenance of the current one. Trailing and defaulted so
    // every caller written against the K3.3 shape still compiles and still means what it meant.
    string? LimitsAtLaunchJson = null, int LimitsReloads = 0, string? LimitsReloadedUtc = null)
{
    /// <summary>First eight of the run id — the form every other surface prints.</summary>
    public string ShortRunId => RunId.Length >= 8 ? RunId[..8] : RunId;

    /// <summary>K3.3: version, commit and dirty flag as one string, or null when the run predates the
    /// stamp. <c>EngineVersion</c> alone is what a v1..v10 row carries, and on those rows it is the
    /// assembly version (<c>2.0.0.0</c>) — true of every build ever made, which is why it is printed
    /// as-is rather than dressed up as provenance it never had.</summary>
    public string? EngineStampText => EngineStamp.Format(EngineVersion, EngineCommit, EngineDirty)
        ?? EngineVersion;

    /// <summary>The limits in force NOW — at the run's last start, or at its last applied plan
    /// reload, whichever came later. Null on a row that never recorded any.</summary>
    public RunLimitsSnapshot? Limits => RunLimitsSnapshot.FromJson(LimitsJson);

    /// <summary>KS1.1: the limits the run was LAUNCHED under. Null on a database older than v13, and
    /// on a run whose row this engine inherited rather than created — neither can say where it began,
    /// and guessing from <see cref="Limits"/> would put a resume's value in a launch's column.</summary>
    public RunLimitsSnapshot? LimitsAtLaunch => RunLimitsSnapshot.FromJson(LimitsAtLaunchJson);

    /// <summary>True when the run demonstrably finished under different limits than it started with.
    /// False when either end is unrecorded: "we cannot tell" is not "they were the same", and a
    /// surface that renders one line for both is saying only what it knows.</summary>
    public bool LimitsChangedInFlight =>
        LimitsAtLaunch is { } launch && Limits is { } now && launch != now;
}
