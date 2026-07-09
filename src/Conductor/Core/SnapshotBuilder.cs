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

        // FU-B10-4: pre-compute depths once (O(n) instead of O(n^2) per-stage).
        var depths = PreComputeDepths(plan.Stages);
        return new DashboardSnapshot
        {
            PlanName = plan.Name,
            Status = state.Status.ToString(),
            AttentionReason = state.AttentionReason,
            StageId = state.CurrentStage ?? "-",
            StageTitle = stage?.Title ?? "",
            Persona = stage != null ? plan.ResolvePersona(stage) : null,
            HeartbeatOn = plan.Report.HeartbeatMinutes > 0,
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
                return (s.Id, rows.Count(r => r.IsDone), rows.Count, StageState(plan, state, s.Id, rows));
            }).ToList(),
            Stages = plan.Stages.Select(s =>
            {
                var rows = track.ForStage(s.Id).ToList();
                var sessions = state.History.Where(h => h.Stage == s.Id).ToList();
                var lastDone = sessions.Where(h => h.EndedUtc != null).OrderBy(h => h.EndedUtc).LastOrDefault();
                return new StageProgress
                {
                    Id = s.Id,
                    Title = s.Title,
                    Done = rows.Count(r => r.IsDone),
                    Total = rows.Count,
                    State = StageState(plan, state, s.Id, rows),
                    Attempts = sessions.Count,
                    LastOutcome = lastDone?.Outcome?.ToString() ?? "",
                    CostUsd = sessions.Sum(h => h.CostUsd ?? 0m),
                    ParentId = s.ParentId,
                    Depth = depths.GetValueOrDefault(s.Id, 0),
                    Checkpoints = rows.Select(c => (c.Id, c.Title, c.Status)).ToList(),
                };
            }).ToList(),
        };
    }

    private static string StageState(PlanConfig plan, RunState state, string stageId, IReadOnlyList<CheckpointRow> rows)
        => state.SkippedStages.Contains(stageId) ? "skipped"
            : state.ConfirmedStages.Contains(stageId) ? "confirmed"
            : rows.Count > 0 && rows.All(r => r.IsDone) ? (plan.PerPhaseGates ? "gating" : "done")
            : stageId == state.CurrentStage ? "active" : "todo";

    /// <summary>B10.2: compute nesting depth by walking parentId chain. Guarded against cycles (already
    /// validated at load) with a visited set; returns 0 for root or untracked parents.</summary>
    internal static int ComputeDepth(string stageId, IReadOnlyList<StageConfig> stages, int maxDepth = 20)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        var current = stageId;
        while (depth < maxDepth)
        {
            var stage = stages.FirstOrDefault(s => s.Id.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (stage?.ParentId is not { Length: > 0 } parent) break;
            if (!visited.Add(parent)) break; // cycle guard
            current = parent;
            depth++;
        }
        return depth;
    }

    /// <summary>FU-B10-4: pre-compute all stage depths in one pass (O(n·d)) instead of allocating a
    /// HashSet per stage in the hot Build path.</summary>
    private static Dictionary<string, int> PreComputeDepths(IReadOnlyList<StageConfig> stages)
    {
        var depths = new Dictionary<string, int>(stages.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var s in stages)
            depths[s.Id] = ComputeDepth(s.Id, stages);
        return depths;
    }
}
