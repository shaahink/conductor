namespace Conductor.Core.Http;

// M7.2: tracked bugs, surfaced to the Face (GET /bugs).

public sealed record BugDto(
    long Id, string Title, string? Detail, string Severity, string Status,
    string? StageId, int? FoundSession, int? FixedSession, string CreatedAt, string UpdatedAt);

public sealed record BugsDto(IReadOnlyList<BugDto> Bugs);
