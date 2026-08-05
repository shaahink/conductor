namespace Conductor.Core.History;

/// <summary>One run as the archive sees it. Timestamps are the raw stored strings.</summary>
public sealed record ArchivedRun(
    string RunId, string PlanName, string Repo, string? Branch, string? EngineVersion,
    string Status, string? StartedUtc, string? EndedUtc, string? LastActivityUtc,
    int Sessions, decimal CostUsd, long Tokens,
    string? EngineCommit = null, bool? EngineDirty = null, string? LimitsJson = null)
{
    /// <summary>First eight of the run id — the form every other surface prints.</summary>
    public string ShortRunId => RunId.Length >= 8 ? RunId[..8] : RunId;

    /// <summary>K3.3: version, commit and dirty flag as one string, or null when the run predates the
    /// stamp. <c>EngineVersion</c> alone is what a v1..v10 row carries, and on those rows it is the
    /// assembly version (<c>2.0.0.0</c>) — true of every build ever made, which is why it is printed
    /// as-is rather than dressed up as provenance it never had.</summary>
    public string? EngineStampText => EngineStamp.Format(EngineVersion, EngineCommit, EngineDirty)
        ?? EngineVersion;

    /// <summary>The limits in force at the run's last start, parsed; null on an older row.</summary>
    public RunLimitsSnapshot? Limits => RunLimitsSnapshot.FromJson(LimitsJson);
}
