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
    int CommitCount
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
    string? NewlyDone
);
