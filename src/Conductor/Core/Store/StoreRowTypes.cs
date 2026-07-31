namespace Conductor.Core.Store;

// Core row types used by IRunStore query methods.

public sealed record LedgerRow(
    long Id,
    string RunId,
    int? SessionNumber,
    string? StageId,
    string Kind,
    string Content,
    string CreatedAt
);

/// <summary>SF1.1: one verifier verdict as stored by <c>WriteScore</c>. <c>Findings</c> is the raw
/// stored blob — the engine joins the verdict's findings with "\n" on the way in, so the split back
/// into a list belongs to the reader, not to every caller.</summary>
public sealed record ScoreRow(
    long Id,
    int SessionNumber,
    string? StageId,
    int Score,
    string? Verdict,
    string? Findings
);

public sealed record SessionSummaryRow(
    int Number,
    string StageId,
    string Kind,
    string? StartedUtc,
    string? EndedUtc,
    string? Outcome,
    int Attempt,
    int ResumeCount,
    string? GateSummary,
    string? ResultSummary,
    int CommitCount,
    // U2.2/U2.3: per-session cost + tokens, aggregated from the `costs` table (which is keyed by
    // session_number and holds MANY rows per session — one per category: agent | gate | advisor).
    // The sessions table itself stores neither, so these are summed, never joined.
    double CostUsd = 0,
    long TokensIn = 0,
    long TokensOut = 0,
    long TokensThink = 0,
    long TokensCache = 0,
    // SC7.2: the session digest as stored JSON (Core.Events.SessionDigest). Null for a session
    // recorded before the column existed, or one that produced no captured tool calls.
    string? Digest = null
);

public sealed record SessionDetailRow(
    int Number,
    string StageId,
    string Kind,
    string? StartedUtc,
    string? EndedUtc,
    string? Outcome,
    string? AgentSessionId,
    int ResumeCount,
    int Attempt,
    string? GateSummary,
    string? ResultSummary,
    int CommitCount,
    string? NewlyDone,
    string? Digest = null
);
