using Conductor.Core;
using Conductor.Core.Budget;
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

    /// <summary>SC2.3 / KS5.4 — the budget block, computed from the LIVE run state rather than from
    /// the fold. It lived in <c>ControlPlaneServer</c> until DV6.3 needed the same four numbers for a
    /// page rendered with no server in the process; the server still calls it, so there is one
    /// arithmetic and not two.</summary>
    public static StateDto WithBudget(StateDto dto, LimitsConfig? limits, RunState liveState)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(liveState);

        var inFlight = dto.AgentActive ? dto.SessionCostUsd : 0m;
        // KS5.2: BilledWindowCostUsd, not PerRunCostUsd — the same sum CheckBudgetCap parks on, so the
        // spend an operator reads and the spend the run stops at are one number. The lifetime figure
        // carries the run's side spend for the same reason: window may never exceed lifetime.
        var window = liveState.BilledWindowCostUsd + inFlight;
        var lifetime = dto.TotalCostUsd + liveState.TotalSideCostUsd;
        // KS5.4: the EFFECTIVE ceiling — the plan's cap plus everything an owner has approved on top of
        // it. Serving the plan's raw cap here would have put the wire back where the field log found it:
        // a run governed by $6.00 while every surface printed $3.00.
        var cap = BudgetCeiling.EffectiveCostCap(limits?.MaxRunCostUsd, liveState.BudgetGrantUsd);

        var priced = liveState.History.Where(h => h.EndedUtc != null && h.CostUsd is > 0).ToList();
        var mean = priced.Count > 0
            ? decimal.Round(priced.Sum(h => h.CostUsd!.Value) / priced.Count, 4, MidpointRounding.AwayFromZero)
            : 0m;

        return dto with
        {
            CostSpent = window,
            CostCap = cap,
            // No cap means no remaining — NOT an unbounded one. A surface must be able to tell
            // "this plan set no ceiling" apart from "there is plenty left".
            CostRemaining = cap is { } c ? c - window : null,
            MeanSessionCost = mean,
            CheckpointsRemaining = Math.Max(0, dto.TotalCount - dto.DoneCount),
            // KS5.4: spend SINCE THE LAST RAISE. costSpent/costCap/costRemaining are now one monotone
            // comparison for the life of the run — an approval widens the ceiling instead of zeroing the
            // spend — so this is the field that keeps answering SC2.3's question, "what has it spent
            // since I last approved". With no approval on file it is the whole run, exactly as before.
            WindowCostUsd = liveState.SpendSinceLastRaiseUsd + inFlight,
            LifetimeCostUsd = lifetime,
            BudgetWindowStartedUtc = liveState.BudgetWindowStartedUtc,
            BudgetApprovals = liveState.BudgetApprovals,
        };
    }

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

    /// <summary>K5.3's registry row on the wire. <c>Visual</c> is derived HERE — the question every
    /// consumer asks (can this be shown inline, or must it be sent as a file) answered once instead of
    /// re-derived from a kind string by each surface.</summary>
    public static EvidenceArtifactDto FromArtifact(Evidence.EvidenceArtifact a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new EvidenceArtifactDto(a.Path, a.Kind, a.CheckpointId, a.StageId, a.SessionNumber,
            a.Sha256, a.Bytes, a.CreatedUtc.ToString("O"), a.Source, Evidence.EvidenceKinds.IsVisual(a.Kind));
    }

    public static ProcessDto FromPid(PidRow p, bool alive, string? lastOutputLine) => new(
        Pid: p.Pid, Purpose: p.Purpose, StageId: p.StageId, SessionNumber: p.SessionNumber,
        StartedUtc: p.StartedUtc.ToString("O"), ExitedUtc: p.ExitedUtc?.ToString("O"), ExitCode: p.ExitCode,
        Alive: alive, LastOutputLine: lastOutputLine);
}
