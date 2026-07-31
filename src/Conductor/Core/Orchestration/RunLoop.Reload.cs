using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class RunLoop
{
    /// <summary>G3.2 live plan reload, applied ONLY from the top of the run loop (the session
    /// boundary). Re-reads the plan file the run was started from, validates it (PlanConfig.Load
    /// throws on an invalid plan → reload is skipped, old plan stays), and swaps it into the context
    /// plus every satellite that caches a plan reference. A stale or deleted file never kills the
    /// run — reload is best-effort and loud in the log either way.</summary>
    private void ApplyPlanReload()
    {
        var path = _ctx.Plan.PlanFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _ctx.Log("plan reload skipped — this run's plan was not loaded from a file it can re-read");
            return;
        }
        PlanConfig fresh;
        try { fresh = PlanConfig.Load(path); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            _ctx.Log($"plan reload skipped — the plan file did not load cleanly: {ex.Message}");
            return;
        }
        _ctx.SwapPlan(fresh);
        _gates.SwapPlan(fresh);
        _lanes.SwapPlan(fresh);
        Dispatcher.SwapPlan(fresh);
        _onPlanSwapped?.Invoke(fresh);
        _ctx.Events.Emit(new PlanReloaded { PlanVersion = fresh.PlanVersion, Stages = fresh.Stages.Count, Gates = fresh.Gates.Count });
        _ctx.Log($"plan reloaded at session boundary — v{fresh.PlanVersion}, {fresh.Stages.Count} stages, {fresh.Gates.Count} gates");

        // W1.2: a reloaded plan re-declares the work — sync the graph (and, when anything changed,
        // the tracker view) so an added stage is schedulable and on the board before the next
        // session, not after a restart.
        if (_ctx.Store is { } db)
            WorkGraphSync.Sync(fresh, db, _ctx.State.RunId, _ctx.Log);

        // P2: the session-scoped stage flags (skip-gates/commit/verification) were computed from
        // the OLD plan at stage entry and have no other writer — recompute them from the fresh
        // plan, or a QA-dial/override edit would silently wait for the next stage transition.
        if (_ctx.State.CurrentStage is { Length: > 0 } cur
            && fresh.Stages.FirstOrDefault(s => s.Id.Equals(cur, StringComparison.OrdinalIgnoreCase)) is { } liveStage)
            ApplyStageOverrides(liveStage);

        // G3.3: if this reload raised/cleared the session cap that parked the run, un-park it —
        // the operator's Plan-tab edit IS the resume. Only a cap-park is auto-resumed; an operator
        // pause stays paused.
        if (_ctx.State.ParkedBySessionCap
            && (fresh.Limits.MaxSessions is not { } cap || cap <= 0 || _ctx.State.SessionCounter < cap))
        {
            _ctx.State.ParkedBySessionCap = false;
            _ctx.State.SetAttention(null);
            if (_ctx.State.Status == RunStatus.Paused)
            {
                _ctx.State.Status = RunStatus.Idle;
                _ctx.Log("session cap raised/cleared by the reloaded plan — resuming");
            }
        }
        _saveAndReport();
    }
}
