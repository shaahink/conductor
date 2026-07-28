using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// M5.6: folds <c>run.db</c>'s event log into a <see cref="StatusReport"/> in-process — no LLM, no
/// network — so <c>conductor status</c> answers from the database in well under a second. It reuses the
/// exact projection the control plane's <c>/state</c> endpoint uses
/// (<see cref="RunStateProjection.Fold"/> + <see cref="SnapshotBuilder.Build"/>), so the CLI verdict and
/// the Face never disagree.
/// </summary>
/// <remarks>
/// The live <see cref="RunState.Status"/> is a transient control field that is not on the event spine —
/// <see cref="RunStateProjection.Fold"/> never sets it — so the headline verdict is derived directly
/// from the event stream (last major event + interrupted-session detection) rather than from a folded
/// status that would always read <c>Idle</c>.
/// </remarks>
public static class StatusReportBuilder
{
    // SessionOutcomes that are not, on their own, a sign of trouble worth surfacing under "what hurt".
    private static readonly HashSet<string> HealthyOutcomes =
        new(StringComparer.OrdinalIgnoreCase) { "Advanced", "Progress", "RolledOver" };

    public static StatusReport Build(PlanConfig plan, IRunStore store,
        Func<int, bool>? isProcessAlive = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        isProcessAlive ??= IsProcessAlive;

        var runId = store.GetLatestRunId(plan.Name);
        if (string.IsNullOrEmpty(runId))
            return new StatusReport(plan.Name, "", "no run recorded yet — run `conductor run` at least once",
                "norun", 0, 0, 0, 0m, 0m, null, null, [], []);

        var events = store.ReadAllEvents(runId);
        var state = RunStateProjection.Fold(events);
        var track = ReadTrackerSafe(plan, ct);
        var snap = SnapshotBuilder.Build(plan, state, track);

        var (verdict, kind) = DeriveVerdict(events, store, runId, isProcessAlive);
        var whatHurt = DeriveWhatHurt(events);

        var stages = snap.Stages
            .Select(s => new StatusStageLine(s.Id, s.Title, s.Done, s.Total, s.State))
            .ToList();

        var sessions = state.History
            .OrderBy(h => h.Number)
            .TakeLast(8)
            .Select(h => new StatusSessionLine(
                h.Number, h.Stage, h.Kind.ToString(),
                h.Outcome?.ToString() ?? "running", h.CostUsd ?? 0m))
            .ToList();

        return new StatusReport(
            plan.Name, runId, verdict, kind,
            snap.DoneCount, snap.TotalCount, state.History.Count,
            snap.TotalCostUsd, snap.OverheadCostUsd,
            whatHurt, state.CurrentStage, stages, sessions);
    }

    private static (string verdict, string kind) DeriveVerdict(
        IReadOnlyList<ConductorEvent> events, IRunStore store, string runId, Func<int, bool> isAlive)
    {
        var last = events.LastOrDefault(e => e is not TokenDelta);

        // Deliberate terminal / park states win over crash-detection: the engine was alive and chose to
        // finish or wait, so an unmatched session underneath is not a crash.
        switch (last)
        {
            case RunFinished f:
                return ($"{f.Status} — {f.CheckpointsDone}/{f.CheckpointsTotal} checkpoints over {f.Sessions} sessions", "ok");
            case AttentionRequested a:
                return ($"needs human — {a.Reason}", "attention");
        }

        // A crash leaves a SessionStarted with no matching SessionFinished and no deliberate park after it.
        // If a process for this run is still alive it is genuinely running; if not, it was interrupted
        // (kill -9 / power loss / Ctrl-C).
        var interrupted = RunStateProjection.FindInterruptedSession(events);
        if (interrupted != null)
        {
            return RunHasLiveProcess(store, runId, isAlive)
                ? ($"running — session #{interrupted.Number} in {interrupted.StageId}", "active")
                : ($"interrupted mid-session — #{interrupted.Number} in {interrupted.StageId} never finished; resume with `conductor run`",
                    "interrupted");
        }

        return last switch
        {
            SessionFinished sf => ($"idle — last was session #{sf.Number} in {sf.StageId}: {sf.Outcome}", "idle"),
            StageConfirmed sc => ($"idle — stage {sc.StageId} confirmed", "idle"),
            null => ("idle — no activity recorded yet", "idle"),
            _ => ($"idle — last event was {last.GetType().Name}", "idle"),
        };
    }

    private static bool RunHasLiveProcess(IRunStore store, string runId, Func<int, bool> isAlive)
    {
        foreach (var p in store.GetAllPids(runId))
            if (p.ExitedUtc == null && isAlive(p.Pid))
                return true;
        return false;
    }

    private static string? DeriveWhatHurt(IReadOnlyList<ConductorEvent> events)
    {
        // Most recent hard signal of trouble, in priority order.
        for (var i = events.Count - 1; i >= 0; i--)
        {
            switch (events[i])
            {
                case AttentionRequested a:
                    return a.Reason;
                case GateFinished g when !g.Passed && !g.Skipped && !g.Optional:
                    return $"gate '{g.Name}' failed{(g.Scope is { Length: > 0 } sc ? $" ({sc})" : "")}";
            }
        }
        // No hard failure — surface a non-advancing last session, if any.
        var lastFinished = events.OfType<SessionFinished>().LastOrDefault();
        if (lastFinished != null && !HealthyOutcomes.Contains(lastFinished.Outcome))
            return $"session #{lastFinished.Number} ended {lastFinished.Outcome}";
        return null;
    }

    private static TrackerSnapshot ReadTrackerSafe(PlanConfig plan, CancellationToken ct)
    {
        try { return ProgressProviderFactory.Create(plan).Read(plan, ct); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
