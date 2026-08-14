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

    /// <summary>W5.1: satellites OUTSIDE the run loop that cache a plan reference — today the HTTP
    /// control plane, which every Face surface reads. It was missing from the reload's swap list, so
    /// a plan edit reached the engine and the tracker while the TUI kept serving the pre-edit plan
    /// for the rest of the run.</summary>
    private readonly Action<PlanConfig>? _onPlanSwapped;

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
        Action saveAndReport,
        Action<PlanConfig>? onPlanSwapped = null)
    {
        _ctx = ctx;
        _sessions = sessions;
        _verdicts = verdicts;
        _gates = gates;
        _lanes = lanes;
        _dispatcher = dispatcher;
        _saveAndReport = saveAndReport;
        _onPlanSwapped = onPlanSwapped;
    }

    // ---------------------------------------------------------------- main loop

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(_ctx.Plan.StateDir, "logs"));
        EnsureStateDirGitignore();
        if (!AcquireLock()) return 4;
        try
        {
            // KS0.3, bug #27: the runs row is written before anything that can save — it is the FK
            // target of run_state, and this loop used to save twice (start-paused, crash recovery)
            // before it got here. Save() ensures the row too, so the ordering is no longer load-bearing.
            _ctx.EnsureRunRow();
            PurgeStaleControlFile();
            RecoverFromCrash();
            if (ApplyStartPause(_ctx.State, _ctx.Options))
            {
                _ctx.Log("started paused (--paused) — dashboard + control plane are up, no session will spawn; press R or run `conductor resume` to start");
                _ctx.Save();
            }
            _ctx.Log($"conductor start — plan '{_ctx.Plan.Name}', repo {_ctx.Plan.Repo}, branch {Git.Branch(_ctx.Plan.Repo)}");
            LogNotificationReadiness();
            _ctx.Events.Emit(new RunStarted
            {
                Plan = _ctx.Plan.Name,
                Repo = _ctx.Plan.Repo,
                Branch = Git.Branch(_ctx.Plan.Repo),
                DriverVersion = typeof(RunLoop).Assembly.GetName().Version?.ToString(),
                Resumed = _ctx.State.SessionCounter > 0,
            });
            WarnOnDirtyEngine();
            NotifyRunStart();
            // SF5.4: two engines on one machine are two identical entries in a task manager until one of
            // them says which run it is. Set once here, refreshed on every stage entry below.
            Core.Fleet.ProcessTitle.Set(_ctx.Plan.Repo, _ctx.Plan.Name, _ctx.State.RunId, _ctx.State.CurrentStage);
            _ctx.ProcessSupervisor?.ReapOrphans();
            SyncWorkGraphFromDeclared();
            WarnOnBranchPattern();
            WarnOnUnboundedSpend();

            var sessionsThisRun = 0;
            // KS5.2: restored BEFORE the auth preflight, which now records what its probe was billed —
            // RestoreBudget overwrites the live counters from state, so a probe accrued first was wiped.
            _ctx.RestoreBudget();
            if (_ctx.RunCostUsd > 0 || _ctx.RunTokens > 0 || _ctx.RunOverheadUsd > 0 || _ctx.RunSideCostUsd > 0)
                _ctx.Log($"restored budget: ${_ctx.RunCostUsd:0.00} agent / ${_ctx.RunSideCostUsd:0.00} lanes+advisor / ${_ctx.RunOverheadUsd:0.00} overhead / {_ctx.RunTokens / 1000.0:0.#}k tokens (from prior process)");
            await AuthPreflightAsync(ct).ConfigureAwait(false);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await HandleControlAsync(ct: ct).ConfigureAwait(false);
                    if (_ctx.State.Status == RunStatus.Aborted) { _ctx.Store?.RecordRunEnd(_ctx.State.RunId, _ctx.State.Status.ToString()); _saveAndReport(); return 2; }

                    // G3.2: the top of this loop is the session boundary — the only safe point to swap
                    // the live plan (no agent is running here; paused/idle iterations pass through too,
                    // so an edit made while parked is live before the next resume).
                    // KS5.4: and the ceiling is compared against the plan that just landed, not the one
                    // it replaced (ReloadThenCheckCap).
                    if (ReloadThenCheckCap()) continue;

                    if (_ctx.State.Status is RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)
                    {
                        // KS2.6: the guard used to be `!DryRun && …`, so a preview run that reached a
                        // park did not idle — it re-parked and re-notified at full speed (the
                        // 2026-08-02 flood). A dry run is a preview and never waits, so it SAYS what
                        // it found and stops. A live run idles as before.
                        if (_ctx.Options.DryRun) { LogDryRunPark(); return 0; }
                        PushIdleSnapshot();
                        await Task.Delay(800, ct).ConfigureAwait(false);
                        continue;
                    }

                    // The declared wait, the backoffs and the DNS park — the timers that hold this
                    // loop at the session boundary without changing what runs next. One method
                    // (RunLoop.Waits.cs); true means "still held, go around".
                    if (!_ctx.Options.DryRun && await HeldAtSessionBoundaryAsync(ct).ConfigureAwait(false)) continue;

                // W5.1: the graph's status, not the declaration's — an imported plan declares TODO
                // for the life of the run, so scheduling on the declaration re-picked delivered work
                // and never completed. See Planning.WorkSnapshot.
                var track = _ctx.ReadWork();

                // KS3.4: WHICH branch fires next — phase gate, completion, a park, or a composed
                // session of a given kind on a given stage — is StageSelection.NextAction's answer,
                // and `preflight`'s compose leg reads the SAME function. This loop owns only the side
                // effects of executing that answer; it holds no private copy of the decision.
                var next = StageSelection.NextAction(_ctx.Plan, _ctx.State, track);

                if (next.Step == LaunchStep.ParkedStatus)
                {
                    // A live run never reaches this decision parked — the idle check above `continue`s
                    // first. Dry run skips that check by design, so the truthful narration is the
                    // park itself, not the session a real launch would never spawn.
                    if (_ctx.Options.DryRun)
                    {
                        _ctx.Sink.Log($"--- DRY RUN: saved status is {_ctx.State.Status} — `conductor run` idles at the session boundary and spawns nothing; resolve, then `conductor resume` (nothing executed) ---");
                        return 0;
                    }
                    PushIdleSnapshot();
                    await Task.Delay(800, ct).ConfigureAwait(false);
                    continue;
                }

                if (next.Step == LaunchStep.EmptyTracker)
                {
                    _verdicts.NeedsHuman($"tracker {_ctx.Plan.Tracker} has no parseable checkpoint rows — check the table format");
                    continue;
                }

                if (next.Step == LaunchStep.PhaseGate)
                {
                    if (_ctx.Options.DryRun)
                    {
                        _ctx.Sink.Log($"--- DRY RUN: would run the FULL-battery phase gate for stage {_ctx.State.PendingPhaseGate!.StageId} (nothing executed) ---");
                        return 0;
                    }
                    await _verdicts.RunPhaseGateAsync(_ctx.State.PendingPhaseGate!, ct).ConfigureAwait(false);
                    if (_ctx.Options.Once && _ctx.State.PendingAudit == null && _ctx.State.PendingFix == null) return 0;
                    continue;
                }

                // W5.1: a queued verification or audit is work this run still owes. The guard only
                // named fix and resume, which was harmless while done-ness lagged a tracker
                // regeneration behind the claim — the queued verify always got a turn first. Reading
                // the graph directly removes that lag, so the LAST checkpoint's verification would be
                // skipped by completion: the one card in the plan nobody checked. Consume what is
                // queued, then close.
                if (next.Step == LaunchStep.ConfirmCompletion)
                {
                    if (await _verdicts.ConfirmCompletionAsync(ct).ConfigureAwait(false)) { _verdicts.CompletePlan(track); return 0; }
                    continue;
                }

                if (next.Step == LaunchStep.NothingRunnable)
                {
                    _verdicts.NeedsHuman("no runnable stage left (remaining stages are skipped) — review skipped stages");
                    continue;
                }

                var stage = next.Stage!;
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
                    Core.Fleet.ProcessTitle.Set(_ctx.Plan.Repo, _ctx.Plan.Name, _ctx.State.RunId, stage.Id);
                    _ctx.Save();
                }

                if (stage.PreHook is { } preHook
                    && !_ctx.State.PreHookRunStages.Contains(stage.Id))
                {
                    await _verdicts.RunStageHookAsync(stage.Id, "pre-hook", preHook, ct).ConfigureAwait(false);
                    _ctx.Save();
                    if (_ctx.State.Status == RunStatus.NeedsHuman) continue;
                }

                if (next.Step == LaunchStep.HandoffEscalation)
                {
                    _verdicts.NeedsHuman("agent asked for a human in the tracker handoff (HUMAN: line) — resolve, then run `conductor resume`");
                    continue;
                }

                // SF0.2 (bug #3): PendingVerify belongs in this guard for the same reason it belongs
                // in the completion guard above — a queued verification is work this run still owes,
                // and this branch `continue`s, so anything it does not stand aside for NEVER GETS A
                // TURN. Without it a CONFIRMED last stage with a verify queued spun the run loop
                // forever at full speed: completion declined (PendingVerify != null), the stage read
                // done here, a phase gate was re-scheduled for a stage already in ConfirmedStages,
                // the gate reused its green signature and re-confirmed, and round again — no session,
                // no delay, no exit. The only outright hang the core run filed against itself.
                // A stage already confirmed has nothing left to gate either; re-scheduling one is at
                // best a wasted battery, and it is what made the loop tight rather than merely wrong.
                // (The guard itself, PendingVerify included, lives in StageSelection.NextAction.)
                if (next.Step == LaunchStep.ScheduleGateOrAudit)
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
                if (next.Step == LaunchStep.ExhaustedAttempts)
                {
                    // KS3.4 round 3: the branch is StageSelection's now, so `preflight` sees it too.
                    // A dry run reports it instead of escalating — the escalation consults the
                    // advisor (a model call) and parks the run, both of which are spend and state a
                    // dry run promises not to touch.
                    if (_ctx.Options.DryRun)
                    {
                        _ctx.Sink.Log($"--- DRY RUN: stage {stage.Id} has used all {maxAttempts} attempts — would consult the advisor and, with none configured, park at NeedsHuman (nothing executed) ---");
                        return 0;
                    }
                    // Whichever way the escalation went — skip, more attempts granted, NeedsHuman —
                    // the state changed under this decision, so take the decision again.
                    await _verdicts.EscalateExhaustedStageAsync(stage, track, maxAttempts).ConfigureAwait(false);
                    continue;
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
                if (next.Step == LaunchStep.SessionCap && _ctx.Plan.Limits.MaxSessions is { } liveCap)
                {
                    _ctx.State.Status = RunStatus.Paused;
                    _ctx.State.ParkedBySessionCap = true;
                    _ctx.State.SetAttention($"session cap reached ({_ctx.State.SessionCounter}/{liveCap}) — raise or clear limits.maxSessions (Plan tab → Settings) to continue");
                    _ctx.Log($"session cap reached ({_ctx.State.SessionCounter}/{liveCap}) — parking at the session boundary");
                    _saveAndReport();
                    continue;
                }

                if (_ctx.Options.DryRun)
                {
                    if (next.SleepUntilUtc is { } wakes)
                        _ctx.Sink.Log($"--- DRY RUN: state.blockedUntilUtc {wakes:yyyy-MM-dd HH:mm:ss}Z is still in the future — a live run sleeps at the session boundary until then before spawning ---");
                    var kind = next.Kind;
                    string prompt;
                    // SC3.3: --dry-run exists to find exactly this before a run spends anything.
                    // next.AttemptNumber == State.NextAttemptNumber here (the stage-entry block above
                    // already reset the counter when the stage changed) — read off the decision so the
                    // number every surface renders is the decision's, not a private re-derivation.
                    try { prompt = BuildPrompt(kind, stage, _ctx.State.SessionCounter + 1, next.AttemptNumber, maxAttempts); }
                    catch (PromptCompositionException ex) { _ctx.Sink.Log($"--- DRY RUN: prompt for stage {stage.Id} REFUSED: {ex.Message} ---"); return 1; }
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

                // KS5.4: through PreflightAsync, so the ceiling this probe compares against is the one
                // an approval has actually raised — see RunLoop.Budget.cs.
                var preflightResults = await PreflightAsync(_ctx).ConfigureAwait(false);
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
                    NotifyPreflightPark(_ctx.PreflightConsecutiveFailures, backoff, failures);
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

                _ctx.Notifier.Resolve();   // KS2.6: work is happening — the open park incident is over
                try { await _sessions.RunAsync(stage, track, ct).ConfigureAwait(false); }
                catch (PromptCompositionException ex) { ParkOnPromptRefusal(stage, ex); continue; }
                sessionsThisRun++;
                var rec = _ctx.State.History[^1];

                await _lanes.CollectLaneArtifactsAsync(stage.Id, ct).ConfigureAwait(false);

                _ctx.RunCostUsd += rec.CostUsd ?? 0;
                // B13.5: TokensTotal, which counts cache reads too. Summing only the other three made
                // the run-level total disagree with the per-session one by roughly forty times on real
                // work — a run that had actually read 79M tokens reported 2.9M — because a long agent
                // session is almost entirely cache read. Every surface fed from here (the ledger, the
                // report, doctor's headroom, and `limits.maxRunTokens`) inherited that, so a run cap
                // set from observed numbers could never be reached. The two rails now count the same
                // thing. Runs carried over from an older engine step up once when this first lands;
                // that discontinuity is the correction, not a new error.
                _ctx.RunTokens += rec.TokensTotal;
                _ctx.PersistBudget();
                EmitSessionFinished(rec);
                // K5.3: awaited rather than folded into EmitSessionFinished, because reading and
                // hashing an artifact is real I/O — a screenshot, not a status line — and this is the
                // one place in the loop that can wait for it without blocking a thread.
                await RegisterEvidenceAsync(rec, ct).ConfigureAwait(false);
                // SC5.1: the park lands AFTER the session's finish event, so it is the last thing in
                // the log and every reader that asks "what is happening now" is told "waiting", not
                // "idle — last session finished".
                _verdicts.EmitBlockedUntilPark(rec);

                _ctx.AbsorbOutOfProcessSpend(); // KS5.2: a second writer's rows become cap-visible here.
                // KS5.4: a queued reload may be raising the very ceiling this would park on — the
                // comparison waits one turn for the swap only the top of the loop may make.
                if (!Dispatcher.ReloadPending && CheckBudgetCap()) continue;

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

    // G3.2's live plan reload lives in RunLoop.Reload.cs, the spend rail in RunLoop.Budget.cs, and the
    // startup/control machinery in RunLoop.Control.cs — one responsibility, one file, and this one was
    // over the architecture ratchet's 500-line ceiling.
}
