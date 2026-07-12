namespace Conductor.Core.Http;

public sealed record SessionRowDto(
    int Number, string StageId, string Kind, string StartedUtc, string? EndedUtc, string? Outcome,
    int Attempt, int ResumeCount, string? GateSummary, string? ResultSummary, int CommitCount);

public sealed record SessionsDto(IReadOnlyList<SessionRowDto> Sessions);

public sealed record QueryRowDto(IReadOnlyList<string> Values);
