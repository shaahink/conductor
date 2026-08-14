using System.Text.Json;

using Conductor.Core.Budget;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Data.Sqlite;

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
    /// a stamp survives an engine restart where a watcher's pending event does not.
    /// <para>KS5.3: internal rather than private, on KS1.1's terms — the reload is what fires the
    /// budget disagreement, and "it fires once per reload, not on every turn of a parked loop" is a
    /// claim about THIS predicate. A test that re-implemented the stamp would be asserting against its
    /// own copy of the rule.</para></remarks>
    internal bool PlanFileChangedOnDisk()
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
    /// run — reload is best-effort and loud in the log either way.
    /// <para>KS1.1: internal rather than private so the boundary itself can be driven from a test.
    /// What the reload persists is now part of the run record, and a test that reproduced the write
    /// instead of calling this would be asserting against its own copy of the rule.</para></summary>
    internal void ApplyPlanReload()
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
        ReportBudgetDisagreement(fresh);

        if (_ctx.Store is { } db)
        {
            // KS1.1: the run row learns the new limits here, at the boundary, and nowhere else.
            // EnsureRunRow first because the row is this write's target and the reload can reach a
            // boundary before anything has saved; it is guarded by its own once-flag, so on the normal
            // path — where the loop wrote the row before its first session — this costs nothing and,
            // being a no-op, could not have carried the new limits by itself.
            _ctx.EnsureRunRow();
            db.RecordLimitsReload(_ctx.State.RunId, RunLimitsSnapshot.From(fresh.Limits).ToJson());

            // W1.2: a reloaded plan re-declares the work — sync the graph (and, when anything changed,
            // the tracker view) so an added stage is schedulable and on the board before the next
            // session, not after a restart.
            WorkGraphSync.Sync(fresh, db, _ctx.State.RunId, _ctx.Log);
        }

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

    /// <summary>
    /// KS5.3 — the reloaded ceiling, checked against what THIS run's own sessions measured, and said
    /// out loud when the two disagree.
    /// <para><c>doctor</c> has made this comparison since K4.2, and doctor runs before a run starts.
    /// The setting is the one most often edited mid-run: an operator parks the run, types a new
    /// <c>maxSessionTokens</c> into the plan file, reloads — and the only answer was the line above,
    /// which reads the number back. A ceiling under this run's floor is not a tighter budget, it is a
    /// run that can no longer land a checkpoint in one session, and the boundary is the last moment
    /// before it starts spending under it. Same function as doctor's
    /// (<see cref="BudgetDisagreement.Compare"/>), so the two surfaces cannot word it differently.</para>
    /// <para>Everything here is best-effort and quiet by default. It runs on the loop thread, so it is
    /// one read-only open of a database the engine is writing (<c>Mode=ReadOnly;Cache=Private</c>, its
    /// own connection, closed before this returns) and it is tied to an actual reload — this method is
    /// reached only from <see cref="ApplyPlanReload"/>, never from the idle turn that a parked run
    /// takes every 800ms. Agreement says nothing. Not being able to measure says nothing. A throw here
    /// must never cost the run its reload, so every read is inside the guard.</para>
    /// </summary>
    private void ReportBudgetDisagreement(PlanConfig fresh)
    {
        var cap = _ctx.EffectiveMaxSessionTokens;
        if (cap is null) return;                             // no ceiling: nothing to disagree with
        // The live store is the only thing that knows where this run's database is; without one there
        // is nothing to measure, and a dry run has no sessions to measure anyway.
        if (_ctx.Store is not SqliteRunStore live) return;
        try
        {
            var archive = RunArchive.TryOpen(live.DbPath);
            if (archive is null) return;                     // deleted, or not a run database: silence
            var verdict = BudgetDisagreement.Compare(
                cap, fresh.Limits.SoftBreakRatio,
                BudgetDisagreement.MeasureRun(archive, _ctx.State.RunId),
                measurable: true);

            if (verdict.Disagrees)
                _ctx.Log($"the reloaded budget disagrees with this run's own sessions: {verdict.Sentence}");
            else if (verdict.Agreement == BudgetAgreement.NoFloor)
                _ctx.Log($"the reloaded budget cannot be checked yet: {verdict.Sentence}");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // "Cannot measure" is a valid answer and the only one available here. The reload has
            // already happened; a measurement that could not be taken must not undo it or delay it.
        }
    }
}
