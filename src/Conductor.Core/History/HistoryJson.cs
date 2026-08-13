namespace Conductor.Core.History;

/// <summary>
/// K3.2: the machine-readable shape of <c>conductor history</c>. A separate contract from the
/// records the archive returns, for the same reason <c>FleetRunDto</c> is separate from
/// <c>FleetRun</c>: what goes to stdout is a promise to whoever parses it, and it must not change
/// shape because an internal record grew a field.
/// </summary>
public sealed record RunHistoryListJson(IReadOnlyList<RunHistoryItemJson> Runs);

/// <summary>One run in the listing. <paramref name="Readable"/> false means the catalogue names a
/// database that is no longer there; every other field but the provenance ones is then empty.</summary>
public sealed record RunHistoryItemJson(
    string RunId, string Repo, string Plan, string Status,
    string? Engine, string? Branch,
    string? StartedUtc, string? EndedUtc, string? LastActivityUtc,
    int Sessions, int CheckpointsDone, int CheckpointsTotal,
    decimal CostUsd, long Tokens,
    string RunDb, string Slug, string? ImportedFrom, bool Readable,
    // K3.3. Optional with defaults so a v11 field never breaks a caller written against K3.2's
    // shape: `engine` still carries the printable stamp, and these three add the parts of it.
    string? EngineCommit = null, bool? EngineDirty = null, RunLimitsSnapshot? Limits = null,
    // KS1.1, on the same terms. `limits` keeps its name and its meaning gets sharper rather than
    // different — it was always the last thing written, it is now honestly labelled "now" — and the
    // launch value arrives beside it instead of replacing it. A consumer that only knows the K3.3
    // shape reads exactly what it read before.
    RunLimitsSnapshot? LimitsAtLaunch = null, int LimitsReloads = 0, string? LimitsReloadedUtc = null);

/// <summary>One run opened: the row, plus its spine.</summary>
public sealed record RunHistoryDetailJson(
    RunHistoryItemJson Run,
    IReadOnlyList<ArchivedStage> Stages,
    IReadOnlyList<ArchivedCheckpoint> Checkpoints,
    IReadOnlyList<ArchivedSession> Sessions);
