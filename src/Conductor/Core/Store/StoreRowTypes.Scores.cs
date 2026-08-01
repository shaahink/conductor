namespace Conductor.Core.Store;

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
