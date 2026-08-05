using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class RunLoop
{
    /// <summary>The (write-time, length) of the plan file as it was last LOADED. Null until the first
    /// stamp is taken, which happens on the first boundary check.</summary>
    private (DateTime Utc, long Length)? _planStamp;

    /// <summary>B13.1: true when the plan file on disk differs from the one this run is executing.
    /// Checked at the session boundary so an edit applies by itself.</summary>
    /// <remarks>Before this existed, editing the plan file did nothing until someone remembered to run
    /// <c>conductor plan reload</c>, and NOTHING said so: the file said <c>maxSessionTokens: 6000000</c>,
    /// the engine ran with the cap it had loaded hours earlier, and the operator watched sessions sail
    /// past a ceiling they had already set. A budget you can set but not apply is worse than no budget —
    /// it is a setting that lies. Stamp-based rather than a FileSystemWatcher: the boundary is the only
    /// safe point to swap a plan anyway, so a watcher would only buy latency this loop cannot use, and
    /// a stamp survives an engine restart where a watcher's pending event does not.</remarks>
    private bool PlanFileChangedOnDisk()
    {
        var path = _ctx.Plan.PlanFilePath;
        if (string.IsNullOrWhiteSpace(path)) return false;
        (DateTime, long) now;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            now = (fi.LastWriteTimeUtc, fi.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        if (_planStamp is not { } was) { _planStamp = now; return false; }
        if (was.Utc == now.Item1 && was.Length == now.Item2) return false;
        _planStamp = now;
        _ctx.Log("plan file changed on disk — reloading at the session boundary");
        return true;
    }

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
        // Re-stamp from the file we just read, so an explicit `plan reload` does not leave the
        // boundary check thinking the file is still ahead and reloading it a second time.
        try { var fi = new FileInfo(path); if (fi.Exists) _planStamp = (fi.LastWriteTimeUtc, fi.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        _ctx.SwapPlan(fresh);
        _gates.SwapPlan(fresh);
        _lanes.SwapPlan(fresh);
        Dispatcher.SwapPlan(fresh);
        _onPlanSwapped?.Invoke(fresh);
        _ctx.Events.Emit(new PlanReloaded { PlanVersion = fresh.PlanVersion, Stages = fresh.Stages.Count, Gates = fresh.Gates.Count });
        // The budget is named in the reload line on purpose: it is the setting most often edited mid-run
        // and the one whose silence was most expensive. "reloaded" alone never told the operator whether
        // the ceiling they had just typed was the ceiling now being enforced.
        var tokenCap = _ctx.EffectiveMaxSessionTokens is { } mt
            ? $"{mt / 1_000_000.0:0.##}M tokens/session"
            : "no per-session cap";
        _ctx.Log($"plan reloaded at session boundary — v{fresh.PlanVersion}, {fresh.Stages.Count} stages, {fresh.Gates.Count} gates, {tokenCap}");

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
