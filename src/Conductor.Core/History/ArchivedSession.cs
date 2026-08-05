namespace Conductor.Core.History;

/// <summary>One session of an archived run.</summary>
public sealed record ArchivedSession(
    int Number, string StageId, string Kind, string? StartedUtc, string? EndedUtc,
    string? Outcome, int Attempt, int ResumeCount, int Commits, decimal CostUsd, long Tokens,
    string? ResultSummary, string? GateSummary,
    string? Engine = null, string? LimitsJson = null,
    long? ContextHighWater = null, long? ContextMeanTurn = null, int? ContextTurns = null)
{
    /// <summary>The limits that governed THIS session — the answer to "the cap was raised at session
    /// 9", which before K3.3 had to be inferred from the shape of a token curve.</summary>
    public RunLimitsSnapshot? Limits => RunLimitsSnapshot.FromJson(LimitsJson);

    /// <summary>K4.1: how full the context window ran, per turn. Null when neither the session columns
    /// nor the event log could answer — the session predates the measurement and kept no deltas.</summary>
    public Conductor.Core.Events.ContextWindowStats? Context =>
        ContextTurns is > 0 ? new Conductor.Core.Events.ContextWindowStats(
            ContextHighWater ?? 0, ContextMeanTurn ?? 0, ContextTurns.Value) : null;
}
