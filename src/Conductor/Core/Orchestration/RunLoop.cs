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
            RecoverFromCrash();
            _ctx.Log($"conductor start — plan '{_ctx.Plan.Name}', repo {_ctx.Plan.Repo}, branch {Git.Branch(_ctx.Plan.Repo)}");
            _ctx.Events.Emit(new RunStarted
            {
                Plan = _ctx.Plan.Name,
                Repo = _ctx.Plan.Repo,
                Branch = Git.Branch(_ctx.Plan.Repo),
                DriverVersion = typeof(RunLoop).Assembly.GetName().Version?.ToString(),
                Resumed = _ctx.State.SessionCounter > 0,
            });
            _ctx.RunDb?.InitializeRun(_ctx.State.RunId, _ctx.Plan.Name, _ctx.Plan.Repo, Git.Branch(_ctx.Plan.Repo),
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
                    if (_ctx.State.Status == RunStatus.Aborted) { _ctx.RunDb?.RecordRunEnd(_ctx.State.RunId, _ctx.State.Status.ToString()); _saveAndReport(); return 2; }
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
                    _ctx.Log($"stage → {stage.Id} {stage.Title}");
                    _ctx.Events.Emit(new StageEntered { StageId = stage.Id, Title = stage.Title, StartHead = _ctx.State.CurrentStageStartHead });
                    _ctx.RunDb?.InitializeStage(_ctx.State.RunId, stage.Id, stage.Title);
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

                if (_ctx.Options.DryRun)
                {
                    var kind = _ctx.State.PendingResume != null ? SessionKind.Resume
                        : _ctx.State.PendingAudit != null ? SessionKind.Audit
                        : _ctx.State.PendingFix != null ? SessionKind.Fix : SessionKind.Deliver;
                    var prompt = BuildPrompt(kind, stage, _ctx.State.SessionCounter + 1, _ctx.State.AttemptsThisStage + 1, maxAttempts);
                    var batterySection = _ctx.Prompts.BatterySection(_ctx.State);
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
        finally { ReleaseLock(); }
    }

    // ---------------------------------------------------------------- control & plumbing

    internal async Task<ControlAction?> HandleControlAsync(bool inSession = false, CancellationToken ct = default)
    {
        var cmd = _ctx.Sink.PollControl() ?? PollInbox() ?? await ReadControlFileAsync(ct).ConfigureAwait(false);
        if (cmd is not { } c) return null;
        return await Dispatcher.DispatchAsync(c, inSession, ct).ConfigureAwait(false);
    }

    private ControlCommand? PollInbox() =>
        _ctx.ControlInbox != null && _ctx.ControlInbox.TryDequeue(out var c) ? c : null;

    private async Task<ControlCommand?> ReadControlFileAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_ctx.ControlPath)) return null;
            var writeTime = File.GetLastWriteTimeUtc(_ctx.ControlPath);
            if (_ctx.LastControlWrite == writeTime) return null;
            _ctx.LastControlWrite = writeTime;
            var text = await File.ReadAllTextAsync(_ctx.ControlPath, ct).ConfigureAwait(false);
            var parsed = ControlFile.Parse(text);
            if (parsed.Action == null) return null;
            if (parsed.Confirmed && parsed.IntentId != null)
                _ctx.Log($"control confirmed [intent={parsed.IntentId}]");
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private void DeleteControlFile()
    {
        try { if (File.Exists(_ctx.ControlPath)) File.Delete(_ctx.ControlPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _ctx.LastControlWrite = null;
    }

    private void RecoverFromCrash()
    {
        var recovered = false;

        if (_ctx.State.Status is RunStatus.Running or RunStatus.VerifyingGates or RunStatus.Backoff)
        {
            var last = _ctx.State.History.LastOrDefault();
            if (last != null && last.EndedUtc == null)
            {
                last.EndedUtc = DateTime.UtcNow;
                last.Outcome = SessionOutcome.Interrupted;
                _verdicts.QueueResume(last, "conductor crashed or was killed mid-session");
                _ctx.Log($"recovered: session #{last.Number} was interrupted — will resume its agent session");
                recovered = true;
            }
            _ctx.State.Status = RunStatus.Idle;
            _ctx.Save();
        }

        if (!recovered && _ctx.State.PendingResume == null)
        {
            var eventsPath = Path.Combine(_ctx.Plan.StateDir, "events.jsonl");
            if (File.Exists(eventsPath))
            {
                var evts = EventLog.ReadAll(eventsPath);
                var interrupted = RunStateProjection.FindInterruptedSession(evts);
                if (interrupted != null)
                {
                    var rec = _ctx.State.History.FirstOrDefault(h => h.Number == interrupted.Number);
                    if (rec != null)
                    {
                        if (rec.EndedUtc == null) rec.EndedUtc = DateTime.UtcNow;
                        rec.Outcome = SessionOutcome.Interrupted;
                        _verdicts.QueueResume(rec, "event log shows interrupted session — recovering");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(interrupted.AgentSessionId))
                        {
                            _ctx.Log($"recovered from event log: session #{interrupted.Number} has no AgentSessionId — marking needs-attention (cannot resume without a session id)");
                            _ctx.State.Status = RunStatus.NeedsHuman;
                            _ctx.State.AttentionReason = $"Orphaned session #{interrupted.Number} in events.jsonl has no AgentSessionId — manual review needed.";
                            _ctx.Save();
                        }
                        else
                        {
                            rec = new SessionRecord
                            {
                                Number = interrupted.Number,
                                Stage = interrupted.StageId,
                                Kind = SessionKind.Deliver,
                                Attempt = 1,
                                StartedUtc = DateTime.UtcNow,
                                ClaudeSessionId = interrupted.AgentSessionId,
                                Outcome = SessionOutcome.Interrupted,
                            };
                            _ctx.State.History.Add(rec);
                            _verdicts.QueueResume(rec, "event log shows interrupted session — recovering from orphaned SessionStarted");
                        }
                    }
                    if (_ctx.State.Status != RunStatus.NeedsHuman)
                    {
                        _ctx.Log($"recovered from event log: session #{interrupted.Number} was interrupted — will resume");
                        _ctx.State.Status = RunStatus.Idle;
                        _ctx.Save();
                    }
                }

                foreach (var evt in evts)
                {
                    if (evt is TaskAdded ta)
                        _ctx.DecomposedCheckpoints.Add(ta.CheckpointId);
                }
            }
        }
    }

    private void WarnOnBranchPattern()
    {
        if (string.IsNullOrWhiteSpace(_ctx.Plan.BranchPattern)) return;
        var branch = Git.Branch(_ctx.Plan.Repo);
        if (!Regex.IsMatch(branch, _ctx.Plan.BranchPattern, RegexOptions.None, ProgressConventions.RegexTimeout))
            _ctx.Log($"⚠ branch '{branch}' does not match plan branchPattern '{_ctx.Plan.BranchPattern}' — check before letting sessions commit");
    }

    private void EnsureStateDirGitignore()
    {
        var gi = Path.Combine(_ctx.Plan.StateDir, ".gitignore");
        if (!File.Exists(gi))
            File.WriteAllText(gi, "*\n!.gitignore\n!REPORT.md\n");
    }
}
