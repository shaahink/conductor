using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Orchestration;

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
public sealed partial class RunLoop
{
    private readonly RunContext _ctx;
    private readonly SessionRunner _sessions;
    private readonly VerdictEngine _verdicts;
    private readonly GateOrchestrator _gates;
    private readonly LaneCoordinator _lanes;
    private readonly Action _saveAndReport;

    private readonly ControlDispatcher? _dispatcher;
    private ControlDispatcher? _dispatcherLazy;
    private ControlDispatcher Dispatcher => _dispatcherLazy ??= _dispatcher ?? new ControlDispatcher(
        _ctx.Plan, _ctx.State, _ctx.Sink, _ctx.Events, _ctx.Log, _ctx.Save, DeleteControlFile,
        _verdicts.SkipStage, _verdicts.ApproveAwaitingOwnerAsync);

    public RunLoop(
        RunContext ctx,
        SessionRunner sessions,
        VerdictEngine verdicts,
        GateOrchestrator gates,
        LaneCoordinator lanes,
        ControlDispatcher? dispatcher,
        Action saveAndReport)
    {
        _ctx = ctx;
        _sessions = sessions;
        _verdicts = verdicts;
        _gates = gates;
        _lanes = lanes;
        _dispatcher = dispatcher;
        _saveAndReport = saveAndReport;
    }

    // ---------------------------------------------------------------- main loop

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(_ctx.Plan.StateDir, "logs"));
        EnsureStateDirGitignore();
        if (!AcquireLock()) return 4;
        try
        {
            PurgeStaleControlFile();
            RecoverFromCrash();
            if (ApplyStartPause(_ctx.State, _ctx.Options))
            {
                _ctx.Log("started paused (--paused) — dashboard + control plane are up, no session will spawn; press R or run `conductor resume` to start");
                _ctx.Save();
            }
            _ctx.Log($"conductor start — plan '{_ctx.Plan.Name}', repo {_ctx.Plan.Repo}, branch {Git.Branch(_ctx.Plan.Repo)}");
            _ctx.Events.Emit(new RunStarted
            {
                Plan = _ctx.Plan.Name,
                Repo = _ctx.Plan.Repo,
                Branch = Git.Branch(_ctx.Plan.Repo),
                DriverVersion = typeof(RunLoop).Assembly.GetName().Version?.ToString(),
                Resumed = _ctx.State.SessionCounter > 0,
            });
            _ctx.Store?.InitializeRun(_ctx.State.RunId, _ctx.Plan.Name, _ctx.Plan.Repo, Git.Branch(_ctx.Plan.Repo),
                typeof(RunLoop).Assembly.GetName().Version?.ToString());
            _ctx.ProcessSupervisor?.ReapOrphans();
            SeedCheckpointsFromTracker();
            WarnOnBranchPattern();

            var sessionsThisRun = 0;
            _ctx.RestoreBudget();
            if (_ctx.RunCostUsd > 0 || _ctx.RunTokens > 0 || _ctx.RunOverheadUsd > 0)
                _ctx.Log($"restored budget: ${_ctx.RunCostUsd:0.00} agent / ${_ctx.RunOverheadUsd:0.00} overhead / {_ctx.RunTokens / 1000.0:0.#}k tokens (from prior process)");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await HandleControlAsync(ct: ct).ConfigureAwait(false);
                    if (_ctx.State.Status == RunStatus.Aborted) { _ctx.Store?.RecordRunEnd(_ctx.State.RunId, _ctx.State.Status.ToString()); _saveAndReport(); return 2; }

                    // G3.2: the top of this loop is the session boundary — the only safe point to swap
                    // the live plan (no agent is running here; paused/idle iterations pass through too,
                    // so an edit made while parked is live before the next resume).
                    if (Dispatcher.ConsumeReloadPending()) ApplyPlanReload();

                    if (!_ctx.Options.DryRun && _ctx.State.Status is RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)
                    {
                        PushIdleSnapshot();
                        await Task.Delay(800, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!_ctx.Options.DryRun && _ctx.BackoffUntil is { } until)
                    {
                        if (DateTime.UtcNow < until) { PushIdleSnapshot(); await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                        _ctx.BackoffUntil = null;
                        _ctx.State.Status = RunStatus.Idle;
                        _ctx.Log("backoff over — resuming");
                    }

                    if (!_ctx.Options.DryRun && _ctx.StallBackoffUntil is { } sbUntil)
                    {
                        if (DateTime.UtcNow < sbUntil)
                        {
                            PushIdleSnapshot();
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }
                        _ctx.StallBackoffUntil = null;
                        _ctx.Log("stall backoff over — resuming");
                    }

                    if (!_ctx.Options.DryRun && _ctx.DnsParkedUntil is { } dpUntil)
                    {
                        if (DateTime.UtcNow < dpUntil)
                        {
                            PushIdleSnapshot();
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }
                        _ctx.DnsParkedUntil = null;
                        var recheckResults = await PreflightHealth.RunAllAsync(
                            _ctx.Plan.Limits.DnsHealthCheck, _ctx.Plan.Repo, _ctx.RunCostUsd,
                            _ctx.Plan.Limits.MaxRunCostUsd).ConfigureAwait(false);
                        if (PreflightHealth.AllPassed(recheckResults))
                        {
                            _ctx.PreflightConsecutiveFailures = 0;
                            _ctx.Log("preflight recovered — resuming session");
                        }
                        else
                        {
                            _ctx.PreflightConsecutiveFailures++;
                            var backoff = PreflightHealth.ComputeBackoff(
                                _ctx.PreflightConsecutiveFailures,
                                _ctx.Plan.Limits.DnsHealthCheck?.IntervalSeconds ?? 60,
                                _ctx.Plan.Limits.DnsHealthCheck?.BackoffMultiplier ?? 2.0,
                                _ctx.Plan.Limits.DnsHealthCheck?.MaxBackoffSeconds ?? 3600);
                            _ctx.DnsParkedUntil = DateTime.UtcNow.AddSeconds(backoff);
                            _ctx.Log($"preflight still failing (×{_ctx.PreflightConsecutiveFailures}) — parking {backoff}s");
                            PushIdleSnapshot();
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }
                    }

                var track = _ctx.Progress.Read(_ctx.Plan, ct);
                if (track.Checkpoints.Count == 0)
                {
                    _verdicts.NeedsHuman($"tracker {_ctx.Plan.Tracker} has no parseable checkpoint rows — check the table format");
                    continue;
                }

                if (_ctx.Plan.PerPhaseGates && _ctx.State.PendingPhaseGate != null)
                {
                    if (_ctx.Options.DryRun)
                    {
                        _ctx.Sink.Log($"--- DRY RUN: would run the FULL-battery phase gate for stage {_ctx.State.PendingPhaseGate.StageId} (nothing executed) ---");
                        return 0;
                    }
                    await _verdicts.RunPhaseGateAsync(_ctx.State.PendingPhaseGate, ct).ConfigureAwait(false);
                    if (_ctx.Options.Once && _ctx.State.PendingAudit == null && _ctx.State.PendingFix == null) return 0;
                    continue;
                }

                var allDone = AllEffectivelyDone(track);
                if (allDone && _ctx.State.PendingFix == null && _ctx.State.PendingResume == null)
                {
                    if (await _verdicts.ConfirmCompletionAsync(ct).ConfigureAwait(false)) { _verdicts.CompletePlan(track); return 0; }
                    continue;
                }

                var stage = allDone
                    ? _ctx.Plan.Stages.FirstOrDefault(s => s.Id == _ctx.State.CurrentStage) ?? _ctx.Plan.Stages[^1]
                    : SelectStage(track);
                if (stage == null)
                {
                    _verdicts.NeedsHuman("no runnable stage left (remaining stages are skipped) — review skipped stages");
                    continue;
                }
                if (stage.Id != _ctx.State.CurrentStage)
                {
                    _ctx.State.CurrentStage = stage.Id;
                    _ctx.State.CurrentStageStartHead = Git.Head(_ctx.Plan.Repo);
                    _ctx.State.AttemptsThisStage = 0;
                    _ctx.State.PendingFix = null;
                    _ctx.State.WorkflowStepIndices.Remove(stage.Id); // reset workflow step for new stage

                    // M3.2: apply per-stage overrides
                    ApplyStageOverrides(stage);

                    _ctx.Log($"stage → {stage.Id} {stage.Title}");
                    _ctx.Events.Emit(new StageEntered { StageId = stage.Id, Title = stage.Title, StartHead = _ctx.State.CurrentStageStartHead });
                    _ctx.Store?.InitializeStage(_ctx.State.RunId, stage.Id, stage.Title);
                    _ctx.Save();
                }

                if (stage.PreHook is { } preHook
                    && !_ctx.State.PreHookRunStages.Contains(stage.Id))
                {
                    await _verdicts.RunStageHookAsync(stage.Id, "pre-hook", preHook, ct).ConfigureAwait(false);
                    _ctx.Save();
                    if (_ctx.State.Status == RunStatus.NeedsHuman) continue;
                }

                if (HandoffWantsHuman(track))
                {
                    _verdicts.NeedsHuman("agent asked for a human in the tracker handoff (HUMAN: line) — resolve, then run `conductor resume`");
                    continue;
                }

                if (_ctx.Plan.PerPhaseGates && track.StageDone(stage.Id)
                    && _ctx.State.PendingFix == null && _ctx.State.PendingResume == null && _ctx.State.PendingAudit == null)
                {
                    if (_ctx.Options.DryRun)
                    {
                        _ctx.Sink.Log($"--- DRY RUN: stage {stage.Id} checkpoints all DONE — would schedule the audit / full-battery phase gate next (nothing executed) ---");
                        return 0;
                    }
                    _verdicts.ScheduleGateOrAudit(stage.Id, _ctx.State.CurrentStageStartHead ?? Git.Head(_ctx.Plan.Repo));
                    _ctx.Save();
                    continue;
                }

                var maxAttempts = MaxAttempts(stage);
                if (_ctx.State.AttemptsThisStage >= maxAttempts && _ctx.State.PendingAudit == null)
                {
                    if (!await _verdicts.EscalateExhaustedStageAsync(stage, track, maxAttempts).ConfigureAwait(false)) continue;
                }

                if (_ctx.Options.MaxSessions > 0 && sessionsThisRun >= _ctx.Options.MaxSessions)
                {
                    _ctx.Log($"--max-sessions {_ctx.Options.MaxSessions} reached — stopping");
                    return 0;
                }

                // G3.3: the LIVE session cap (limits.maxSessions) parks instead of stopping — the run
                // stays up (dashboard + control plane), and raising/clearing the cap from the Plan tab
                // triggers a reload that un-parks it (see ApplyPlanReload). Counts the run's total
                // sessions (SessionCounter), not this process's, so a cap set below work already done
                // parks immediately at the boundary.
                if (_ctx.Plan.Limits.MaxSessions is { } liveCap && liveCap > 0 && _ctx.State.SessionCounter >= liveCap)
                {
                    _ctx.State.Status = RunStatus.Paused;
                    _ctx.State.ParkedBySessionCap = true;
                    _ctx.State.AttentionReason = $"session cap reached ({_ctx.State.SessionCounter}/{liveCap}) — raise or clear limits.maxSessions (Plan tab → Settings) to continue";
                    _ctx.Log($"session cap reached ({_ctx.State.SessionCounter}/{liveCap}) — parking at the session boundary");
                    _saveAndReport();
                    continue;
                }

                if (_ctx.Options.DryRun)
                {
                    var kind = _ctx.State.PendingResume != null ? SessionKind.Resume
                        : _ctx.State.PendingAudit != null ? SessionKind.Audit
                        : _ctx.State.PendingFix != null ? SessionKind.Fix : SessionKind.Deliver;
                    var prompt = BuildPrompt(kind, stage, _ctx.State.SessionCounter + 1, _ctx.State.AttemptsThisStage + 1, maxAttempts);
                    var batterySection = _ctx.Prompts.BatterySection(_ctx.State, _ctx.Store);
                    if (batterySection.Length > 0)
                        prompt = prompt.TrimEnd() + "\n\n" + batterySection;
                    _ctx.Sink.Log($"--- DRY RUN: would start session #{_ctx.State.SessionCounter + 1} ({kind}, stage {stage.Id}) with prompt: ---");
                    _ctx.Sink.Log(prompt);
                    return 0;
                }

                if (_ctx.Plan.Limits.ApprovalMode && !_ctx.SessionApproved)
                {
                    _ctx.Events.Emit(new OwnerApprovalRequested { StageId = stage.Id });
                    _ctx.State.Status = RunStatus.AwaitingOwner;
                    _ctx.State.AwaitingOwnerReason = AwaitingOwnerReason.ApprovalMode;
                    _ctx.Log($"approval mode: park before session #{_ctx.State.SessionCounter + 1} on stage {stage.Id} — approve with R or `conductor approve`");
                    _saveAndReport();
                    continue;
                }
                _ctx.SessionApproved = false;

                var preflightResults = await PreflightHealth.RunAllAsync(
                    _ctx.Plan.Limits.DnsHealthCheck, _ctx.Plan.Repo, _ctx.RunCostUsd,
                    _ctx.Plan.Limits.MaxRunCostUsd).ConfigureAwait(false);
                if (PreflightHealth.AnyFailed(preflightResults))
                {
                    _ctx.PreflightConsecutiveFailures++;
                    var backoff = PreflightHealth.ComputeBackoff(
                        _ctx.PreflightConsecutiveFailures,
                        _ctx.Plan.Limits.DnsHealthCheck?.IntervalSeconds ?? 60,
                        _ctx.Plan.Limits.DnsHealthCheck?.BackoffMultiplier ?? 2.0,
                        _ctx.Plan.Limits.DnsHealthCheck?.MaxBackoffSeconds ?? 3600);
                    _ctx.DnsParkedUntil = DateTime.UtcNow.AddSeconds(backoff);
                    var failures = string.Join("; ", preflightResults.Where(r => !r.Passed).Select(r => $"{r.Name}:{r.Message}"));
                    _ctx.Log($"preflight FAILED (×{_ctx.PreflightConsecutiveFailures}): {failures} — parking {backoff}s");
                    _ = _ctx.Telegram.PushWithKeyboardAsync(
                        $"Conductor {_ctx.Plan.Name}: preflight failed — {failures}",
                        [("Resume", "resume"), ("Skip", "skip")], CancellationToken.None);
                    _ctx.Save();
                    continue;
                }
                _ctx.PreflightConsecutiveFailures = 0;

                _lanes.StartAnalysisLanes(stage, track.HandoffBlock, ct);

                if (_ctx.State.PendingParallelAudit != null && _ctx.State.PendingFix == null && _ctx.State.PendingResume == null)
                {
                    _lanes.StartParallelAudit(_ctx.State.PendingParallelAudit, ct);
                }
                if (_ctx.State.ParallelAuditOutcome is { Completed: true } outcome && _ctx.State.PendingFix == null)
                {
                    if (outcome.MaxSeverity == AuditFindingSeverity.High)
                    {
                        var fixNote = $"prior parallel audit found HIGH-severity issues in stage {outcome.StageId}:\n{Trunc(outcome.Findings, 2000)}";
                        _ctx.State.PendingFix = new PendingFix
                        {
                            FromSession = _ctx.State.History.LastOrDefault()?.Number ?? 0,
                            GateFailures = "",
                            ProgressSummary = fixNote,
                        };
                        _ctx.State.ParallelAuditOutcome = null;
                        _ctx.State.Status = RunStatus.Idle;
                        _ctx.Log($"parallel audit: HIGH findings from stage {outcome.StageId} — queuing fix session");
                        _ctx.Save();
                        continue;
                    }
                }

                await _sessions.RunAsync(stage, track, ct).ConfigureAwait(false);
                sessionsThisRun++;
                var rec = _ctx.State.History[^1];

                await _lanes.CollectLaneArtifactsAsync(stage.Id, ct).ConfigureAwait(false);

                _ctx.RunCostUsd += rec.CostUsd ?? 0;
                _ctx.RunTokens += (rec.TokensInput ?? 0) + (rec.TokensOutput ?? 0) + (rec.TokensReasoning ?? 0);
                _ctx.PersistBudget();
                EmitSessionFinished(rec);

                if (CheckBudgetCap()) continue;

                if (Dispatcher.ConsumePendingSkip())
                {
                    _verdicts.SkipStage(stage, "skipped by user control");
                }
                if (Dispatcher.ConsumePausePending())
                {
                    if (_ctx.State.Status is not (RunStatus.NeedsHuman or RunStatus.Aborted)) _ctx.State.Status = RunStatus.Paused;
                    _ctx.Log("paused after session as requested — press R or run `conductor resume` to continue");
                    _saveAndReport();
                }
                if (_ctx.State.StopAfterSession)
                {
                    _ctx.State.StopAfterSession = false;
                    if (_ctx.State.Status is not (RunStatus.NeedsHuman or RunStatus.Aborted)) _ctx.State.Status = RunStatus.Paused;
                    _ctx.Log("quitting after session as requested — run `conductor run` to continue later");
                    _saveAndReport();
                    return 0;
                }
                if (_ctx.Options.Once)
                {
                    _ctx.Log("--once: stopping after one session");
                    return 0;
                }
            }
            }
            catch (OperationCanceledException) { /* cancellation requested — fall through to cleanup */ }
            _ctx.Log("cancelled — saving state");
            try { _saveAndReport(); } catch { /* best-effort */ }
            if (_ctx.State.Status == RunStatus.Running && _ctx.State.History.LastOrDefault() is { EndedUtc: null } last)
            {
                last.EndedUtc = DateTime.UtcNow;
                last.Outcome = SessionOutcome.Interrupted;
                _verdicts.QueueResume(last, "conductor cancelled mid-session");
            }
            _ctx.Log("state saved; run `conductor run` again to resume");
            _ctx.Save();
            return 130;
        }
        finally
        {
            _ctx.DisposeTranscript();
            ReleaseLock();
        }
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
        _ctx.SwapPlan(fresh);
        _gates.SwapPlan(fresh);
        _lanes.SwapPlan(fresh);
        Dispatcher.SwapPlan(fresh);
        _ctx.Events.Emit(new PlanReloaded { PlanVersion = fresh.PlanVersion, Stages = fresh.Stages.Count, Gates = fresh.Gates.Count });
        _ctx.Log($"plan reloaded at session boundary — v{fresh.PlanVersion}, {fresh.Stages.Count} stages, {fresh.Gates.Count} gates");

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
            _ctx.State.AttentionReason = null;
            if (_ctx.State.Status == RunStatus.Paused)
            {
                _ctx.State.Status = RunStatus.Idle;
                _ctx.Log("session cap raised/cleared by the reloaded plan — resuming");
            }
        }
        _saveAndReport();
    }

    /// <summary>G3.1 `run --paused`: park the run before the first session so the operator can author
    /// the plan / pre-seed the kanban with the control plane up. Pure so the flag→status wiring is
    /// unit-testable. Never masks a state that needs attention (NeedsHuman/Aborted keep their reason),
    /// and dry runs ignore it (nothing spawns anyway).</summary>
    internal static bool ApplyStartPause(RunState state, RunOptions opts)
    {
        if (!opts.StartPaused || opts.DryRun) return false;
        if (state.Status is RunStatus.NeedsHuman or RunStatus.Aborted) return false;
        state.Status = RunStatus.Paused;
        return true;
    }
}
