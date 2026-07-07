using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Builds the immutable <see cref="DashboardSnapshot"/> from run state + tracker. Shared by the
/// live orchestrator and the offline <c>preview</c> command so both render identically.
/// </summary>
public static class SnapshotBuilder
{
    public static DashboardSnapshot Build(PlanConfig plan, RunState state, TrackerSnapshot track,
        string gateSummary = "", DateTime? backoffUntil = null)
    {
        var stage = plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage);
        var currentCp = state.CurrentStage != null
            ? track.ForStage(state.CurrentStage).FirstOrDefault(c => !c.IsDone)
            : null;
        return new DashboardSnapshot
        {
            PlanName = plan.Name,
            Status = state.Status.ToString(),
            AttentionReason = state.AttentionReason,
            StageId = state.CurrentStage ?? "-",
            StageTitle = stage?.Title ?? "",
            DoneCount = track.Checkpoints.Count(c => c.IsDone),
            TotalCount = track.Checkpoints.Count,
            TotalCostUsd = state.TotalCostUsd,
            UntrackedSessions = state.History.Count(h => h.EndedUtc != null && h.CostUsd == null),
            TokensInput = state.TotalTokensInput,
            TokensOutput = state.TotalTokensOutput,
            TokensReasoning = state.TotalTokensReasoning,
            CurrentCheckpoint = currentCp?.Id ?? "",
            CurrentCheckpointTitle = currentCp?.Title ?? "",
            GateSummary = gateSummary,
            Branch = "",
            BackoffUntilUtc = backoffUntil,
            SessionNumber = state.SessionCounter,
            SessionKind = state.Status.ToString(),
            StageCheckpoints = state.CurrentStage != null
                ? track.ForStage(state.CurrentStage).Select(c => (c.Id, c.Title, c.Status)).ToList()
                : new List<(string, string, string)>(),
            StageOverview = plan.Stages.Select(s =>
            {
                var rows = track.ForStage(s.Id).ToList();
                var st = state.SkippedStages.Contains(s.Id) ? "skipped"
                    : state.ConfirmedStages.Contains(s.Id) ? "confirmed"
                    : rows.Count > 0 && rows.All(r => r.IsDone) ? (plan.PerPhaseGates ? "gating" : "done")
                    : s.Id == state.CurrentStage ? "active" : "todo";
                return (s.Id, rows.Count(r => r.IsDone), rows.Count, st);
            }).ToList(),
        };
    }
}
