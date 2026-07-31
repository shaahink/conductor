namespace Conductor.Core;

/// <summary>
/// M5.6: the one-verdict answer to "where are we, how did it go, what hurt", built purely from
/// <c>run.db</c> (the append-only event log) — never from <c>state.json</c> or the hand-edited tracker
/// markdown. Produced by <see cref="StatusReportBuilder"/> and rendered by
/// <see cref="Conductor.Commands.StatusCommand"/>. The same fold backs the control plane's <c>/state</c>
/// endpoint, so the CLI verdict and the Face can never disagree.
/// </summary>
public sealed record StatusReport(
    string PlanName,
    string RunId,
    string Verdict,
    // ok | active | attention | interrupted | idle | waiting | norun — drives the header colour only.
    string Kind,
    int DoneCount,
    int TotalCount,
    int SessionCount,
    decimal TotalCostUsd,
    decimal OverheadCostUsd,
    string? WhatHurt,
    string? CurrentStageId,
    IReadOnlyList<StatusStageLine> Stages,
    IReadOnlyList<StatusSessionLine> RecentSessions);

public sealed record StatusStageLine(string Id, string Title, int Done, int Total, string State);

public sealed record StatusSessionLine(int Number, string Stage, string Kind, string Outcome, decimal CostUsd);
