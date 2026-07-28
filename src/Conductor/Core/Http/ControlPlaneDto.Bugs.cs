namespace Conductor.Core.Http;

// M7.2: tracked bugs, surfaced to the Face (GET /bugs).

public sealed record BugDto(
    long Id, string Title, string? Detail, string Severity, string Status,
    string? StageId, int? FoundSession, int? FixedSession, string CreatedAt, string UpdatedAt);

public sealed record BugsDto(IReadOnlyList<BugDto> Bugs);

// Shared reply for the write-side knowledge endpoints (POST /note, /bug, /bug/resolve); colocated
// here (rather than in ControlPlaneDto.KnowledgeWrite.cs) to keep each DTO file at ≤3 types.
public sealed record KnowledgeWriteResultDto(bool Ok, long? Id, string? Error);
