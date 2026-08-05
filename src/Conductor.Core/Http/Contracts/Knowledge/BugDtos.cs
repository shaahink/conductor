namespace Conductor.Core.Http;

// M7.2: tracked bugs, surfaced to the Face (GET /bugs).

// SF0.4: CarriedFromPlan is the plan of the EARLIER run that filed this bug, or null when this run
// filed it. Without it the Face showed a clean ledger to a repo with eleven open bugs in it.
public sealed record BugDto(
    long Id, string Title, string? Detail, string Severity, string Status,
    string? StageId, int? FoundSession, int? FixedSession, string CreatedAt, string UpdatedAt,
    string? CarriedFromPlan = null);

public sealed record BugsDto(IReadOnlyList<BugDto> Bugs);

// Shared reply for the write-side knowledge endpoints (POST /note, /bug, /bug/resolve); colocated
// here (rather than in ControlPlaneMapper.KnowledgeWrite.cs) to keep each DTO file at ≤3 types.
public sealed record KnowledgeWriteResultDto(bool Ok, long? Id, string? Error);
