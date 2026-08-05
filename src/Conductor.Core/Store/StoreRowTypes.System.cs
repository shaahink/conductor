namespace Conductor.Core.Store;

public sealed record OrphanPidRow(
    int Pid,
    string Purpose
);

public sealed record CheckpointRow(
    string Id,
    string StageId,
    string Title,
    string Status,
    string Commit,
    string Evidence,
    bool Confirmed = false
);

/// <summary>BLOB stored in run_state table — serialised RunState JSON.</summary>
public sealed record RunStateBlob(string PlanName, string StateJson, DateTime UpdatedUtc);
