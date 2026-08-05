using System.Text.Json.Serialization;

namespace Conductor.Core.Http;

// CostUsd/Tokens* (U2.2/U2.3) are SUMMED from the `costs` table per session — the sessions table
// stores neither. Zero tokens against a non-zero cost is truthful for sessions recorded before the
// provider learned to read `usage` (bug #5, fixed in 71fa214), not a rendering bug.
public sealed record SessionRowDto(
    int Number, string StageId, string Kind, string StartedUtc, string? EndedUtc, string? Outcome,
    int Attempt, int ResumeCount, string? GateSummary, string? ResultSummary, int CommitCount,
    double CostUsd = 0, long TokensIn = 0, long TokensOut = 0,
    // K1.3: NULL means "this backend does not report reasoning tokens", not "it reported zero".
    // `costs.tokens_think` is 0 on every row this project has written because every row came from
    // claude, whose usage object has no thinking field — a 0 rendered in a money column claims no
    // thinking happened. The engine sends null for a run whose provider says it cannot report one
    // (IAgentProvider.ReportsReasoningTokens) and the number for one that can (opencode).
    // Serialised EXPLICITLY as null (the context otherwise drops nulls): "not applicable" is the
    // answer here, and an absent key would leave a reader guessing whether the engine is just old.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? TokensThink = null,
    long TokensCache = 0,
    // SC7.2: what the session actually DID, from its structured tool events. Null when the session
    // predates the digest or captured no tool calls — never an empty digest standing in for one.
    SessionDigestDto? Digest = null,
    // SF3.3: the session's own commits as `<short sha> <subject>` lines — the repo's and any
    // declared satellite's, in the order the verdict saw them. CommitCount alone answered "did
    // anything land"; reading WHAT landed meant leaving the tool for a terminal. Empty = a session
    // that committed nothing, or one whose SessionFinished event predates the field.
    IReadOnlyList<string>? Commits = null);

public sealed record SessionsDto(IReadOnlyList<SessionRowDto> Sessions);
