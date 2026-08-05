namespace Conductor.Core.Store;

public sealed record StageOutcomeRow(
    string StageId,
    string Outcome,
    int Count
);

public sealed record GateFailureRow(
    string Name,
    string? StageId,
    string Tier
);

public sealed record GateDetailRow(
    string Name,
    string? Tier,
    bool Passed,
    bool Skipped,
    string? Scope
);
