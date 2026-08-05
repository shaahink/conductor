using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Http;

/// <summary>
/// Engine snapshot in, wire contract out. The one place a <see cref="DashboardSnapshot"/>, a
/// <see cref="TaskItem"/> or a <see cref="PidRow"/> becomes something the control plane may send.
/// </summary>
/// <remarks>
/// K2.3: this was called <c>ControlPlaneDto</c> and lived in a file of the same name alongside
/// <see cref="StateDto"/> and <see cref="ControlPlaneJsonContext"/> — three responsibilities and a name
/// that described none of them. Twenty-nine sibling files then wore the <c>ControlPlaneDto.</c> prefix
/// without declaring any part of this type; the prefix was a filing convention pretending to be a type.
/// The contracts live in <c>Http/Contracts/&lt;feature&gt;/</c> now, and this is a mapper, so it is named
/// one.
/// </remarks>
public static class ControlPlaneMapper
{
    public static StateDto FromSnapshot(DashboardSnapshot snap, string runId, string repo, string planDir,
        long? maxSessionTokensThisRun = null, string tracker = "", string stateDir = "") => new(
        PlanName: snap.PlanName,
        Status: snap.Status,
        AttentionReason: snap.AttentionReason,
        StageId: snap.StageId,
        StageTitle: snap.StageTitle,
        Persona: snap.Persona,
        DoneCount: snap.DoneCount,
        TotalCount: snap.TotalCount,
        TotalCostUsd: snap.TotalCostUsd,
        OverheadCostUsd: snap.OverheadCostUsd,
        TokensInput: snap.TokensInput,
        TokensOutput: snap.TokensOutput,
        TokensReasoning: snap.TokensReasoning,
        CurrentCheckpoint: snap.CurrentCheckpoint,
        CurrentCheckpointTitle: snap.CurrentCheckpointTitle,
        GateSummary: snap.GateSummary,
        Stages: [.. snap.Stages.Select(FromStage)],
        RunId: runId,
        Repo: repo,
        PlanDir: planDir,
        SessionNumber: snap.SessionNumber,
        SessionKind: snap.SessionKind,
        Attempt: snap.Attempt,
        MaxAttempts: snap.MaxAttempts,
        SessionElapsedSec: snap.SessionElapsed.TotalSeconds,
        AgentActive: snap.AgentActive,
        SessionCostUsd: snap.SessionCostUsd,
        SessionTokensInput: snap.SessionTokensInput,
        SessionTokensOutput: snap.SessionTokensOutput,
        SessionTokensReasoning: snap.SessionTokensReasoning,
        Gates: [.. snap.Gates.Select(g => new GateDto(g.Name, g.State, g.LiveElapsed(DateTime.UtcNow).TotalSeconds))],
        MaxSessionTokensThisRun: maxSessionTokensThisRun,
        Tracker: tracker,
        StateDir: stateDir,
        AttentionSinceUtc: snap.AttentionSinceUtc,
        BlockedUntilUtc: snap.BlockedUntilUtc,
        BlockedReason: snap.BlockedReason);

    private static StageDto FromStage(StageProgress s) => new(
        Id: s.Id, Title: s.Title, Done: s.Done, Total: s.Total, State: s.State,
        Attempts: s.Attempts, LastOutcome: s.LastOutcome, CostUsd: s.CostUsd,
        ParentId: s.ParentId, Depth: s.Depth,
        Checkpoints: [.. s.Checkpoints.Select(c => new CheckpointDto(c.Id, c.Title, c.Status))]);

    public static TasksDto FromTasks(IReadOnlyList<TaskItem> tasks) => new(
        [.. tasks.Select(t => new TaskDto(t.TaskId, t.CheckpointId, t.Title, t.Status, t.Source, t.Order, t.Context, t.Paths,
            Kind: t.Kind, StageId: t.StageId, Confirmed: t.Confirmed, Qa: t.Qa,
            SessionNumber: t.SessionNumber, StatusSinceUtc: t.StatusSinceUtc?.ToString("O"),
            Attempts: t.Attempts))]);

    public static ProcessDto FromPid(PidRow p, bool alive, string? lastOutputLine) => new(
        Pid: p.Pid, Purpose: p.Purpose, StageId: p.StageId, SessionNumber: p.SessionNumber,
        StartedUtc: p.StartedUtc.ToString("O"), ExitedUtc: p.ExitedUtc?.ToString("O"), ExitCode: p.ExitCode,
        Alive: alive, LastOutputLine: lastOutputLine);
}
