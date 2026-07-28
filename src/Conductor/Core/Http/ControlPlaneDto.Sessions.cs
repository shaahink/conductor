namespace Conductor.Core.Http;

// CostUsd/Tokens* (U2.2/U2.3) are SUMMED from the `costs` table per session — the sessions table
// stores neither. Zero tokens against a non-zero cost is truthful for sessions recorded before the
// provider learned to read `usage` (bug #5, fixed in 71fa214), not a rendering bug.
public sealed record SessionRowDto(
    int Number, string StageId, string Kind, string StartedUtc, string? EndedUtc, string? Outcome,
    int Attempt, int ResumeCount, string? GateSummary, string? ResultSummary, int CommitCount,
    double CostUsd = 0, long TokensIn = 0, long TokensOut = 0, long TokensThink = 0,
    long TokensCache = 0);

public sealed record SessionsDto(IReadOnlyList<SessionRowDto> Sessions);

public sealed record QueryRowDto(IReadOnlyList<string> Values);
