using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

public sealed record RunOptions(bool DryRun, bool Once, int MaxSessions, bool ControlPlane = false, int ControlPlanePort = 4317);

/// <summary>
/// The session cycle, mechanized:
///   pick stage from tracker â†’ spawn agent session (deliver / fix / resume) â†’ watchdog it â†’
///   independently verify (gates + git + tracker diff) â†’ record, report, decide next.
/// Every transition is persisted, so killing conductor at any point is recoverable.
/// </summary>
#pragma warning disable MA0045 // Orchestrator helper methods (Save/Log/AcquireLock/etc.) use sync file I/O by design â€” fast local writes, not hot-path
public sealed partial class Orchestrator(PlanConfig plan, RunState state, string statePath, IProgressSink sink, IEventSink events, RunOptions opts, ILogger<Orchestrator> logger, ITelegramService telegram, WebhookNotifier webhooks, IPlanner? planner = null, RunDb? runDb = null, ProcessSupervisor? processSupervisor = null, ControlDispatcher? dispatcher = null, ConcurrentQueue<ControlCommand>? controlInbox = null, TranscriptLog? transcript = null)
{
    private readonly IPlanner _planner = planner ?? new CheckpointPlanner();
    private readonly PromptBuilder _prompts = BuildPromptBuilder(plan);
    private readonly IProgressProvider _progress = ProgressProviderFactory.Create(plan);
    // The agent provider owns backend-specific concerns (stream parsing lives in AgentSession, the
    // usage-limit phrasing lives here) so the Orchestrator no longer switches on `output` (B2.4, D-11).
    private readonly IAgentProvider _agentProvider = AgentProviderFactory.Create(plan.Agent);
    private readonly LessonsManager _lessons = new(plan.StateDir);
    // Control-verb execution (what pause/skip/rollback/goto/etc. DO) lives in ControlDispatcher â€”
    // Orchestrator only owns when a command is polled. See Core/Commands/ControlDispatcher.cs.
    // Lazy (not a field initializer): binding Log/Save/etc. as delegates needs `this`, which a
    // primary-constructor field initializer can't reference (CS0236).
    private ControlDispatcher? _dispatcherInstance;
    private ControlDispatcher Dispatcher => _dispatcherInstance ??=
        dispatcher ?? new ControlDispatcher(plan, state, sink, events, Log, Save, DeleteControlFile, SkipStage, ApproveAwaitingOwnerAsync);
    // Lane coordination (parallel audit / fix-lanes / analysis lanes) lives in LaneCoordinator â€”
    // Orchestrator only owns when to call in, same seam ControlDispatcher was cut along in F5.
    private LaneCoordinator? _lanesInstance;
    private LaneCoordinator Lanes => _lanesInstance ??= new LaneCoordinator(plan, state, sink, events, Log);
    // F7: gate execution and persistence extracted from the Orchestrator god-class.
    private GateOrchestrator? _gateInstance;
    private GateOrchestrator Gates => _gateInstance ??= new GateOrchestrator(plan, state, events, _runDb);
    // F5: commands posted to the HTTP control plane land here â€” a third ingress alongside the TUI
    // queue (sink.PollControl) and control.json (ReadControlFileAsync), same dispatcher for all three.
    private readonly ConcurrentQueue<ControlCommand>? _controlInbox = controlInbox;
    private IReadOnlyList<GateResult>? _lastGates;
    private readonly string _lockPath = Path.Combine(plan.StateDir, "conductor.lock");
    private readonly string _controlPath = Path.Combine(plan.StateDir, "control.json");
    private DateTime? _lastControlWrite;
    private readonly string _logPath = Path.Combine(plan.StateDir, "conductor.log");
    private DateTime? _backoffUntil;
    private DateTime? _stallBackoffUntil;
    private int _stallBackoffMultiplier = 1;
    private DateTime? _dnsParkedUntil;
    private int _preflightConsecutiveFailures;
    private readonly List<(string Kind, string Text, DateTime Utc)> _activity = new();
    private readonly HashSet<string> _decomposedCheckpoints = new(StringComparer.Ordinal);
    private bool _softBreakSignalled;

    // Correlation state attached to every structured log line (B2.5): runId + stage + session number
    // come from RunState; the gate marker is set while a battery runs.
    private string? _curGate;
    private string? _outcome;
    private bool _sessionApproved;
    private decimal _runCostUsd;
    private long _runTokens;
    private decimal _runOverheadUsd; // O3: gate runtime estimate accumulator
    private readonly RunDb? _runDb = runDb; // F1: SQLite task store (null in dry-runs)
    // F3.1: cached bg liveness to avoid querying the OS process table on every 400ms tick
    private DateTime? _lastBgLivenessCheck;
    private bool _cachedBgAlive;

    // ---------------------------------------------------------------- main loop

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(plan.StateDir, "logs"));
        EnsureStateDirGitignore();
        if (!AcquireLock()) return 4;
        try
        {
            RecoverFromCrash();
            Log($"conductor start â€” plan '{plan.Name}', repo {plan.Repo}, branch {Git.Branch(plan.Repo)}");
            events.Emit(new RunStarted
            {
                Plan = plan.Name,
                Repo = plan.Repo,
                Branch = Git.Branch(plan.Repo),
                DriverVersion = typeof(Orchestrator).Assembly.GetName().Version?.ToString(),
                Resumed = state.SessionCounter > 0,
            });
            _runDb?.InitializeRun(state.RunId, plan.Name, plan.Repo, Git.Branch(plan.Repo),
                typeof(Orchestrator).Assembly.GetName().Version?.ToString());
            processSupervisor?.ReapOrphans();
            // F1.2: seed checkpoints from the existing tracker into run.db (additive â€” doesn't replace parse)
            SeedCheckpointsFromTracker();
            WarnOnBranchPattern();

            var sessionsThisRun = 0;
            _runCostUsd = state.PerRunCostUsd;
            _runTokens = state.PerRunTokens;
            _runOverheadUsd = state.PerRunOverheadCostUsd;
            if (_runCostUsd > 0 || _runTokens > 0 || _runOverheadUsd > 0)
                Log($"restored budget: ${_runCostUsd:0.00} agent / ${_runOverheadUsd:0.00} overhead / {_runTokens / 1000.0:0.#}k tokens (from prior process)");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await HandleControlAsync(ct: ct).ConfigureAwait(false);
                    if (state.Status == RunStatus.Aborted) { _runDb?.RecordRunEnd(state.RunId, state.Status.ToString()); SaveAndReport(); return 2; }
                    if (!opts.DryRun && state.Status is RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)
                    {
                        PushIdleSnapshot();
                        await Task.Delay(800, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!opts.DryRun && _backoffUntil is { } until)
                    {
                        if (DateTime.UtcNow < until) { PushIdleSnapshot(); await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                        _backoffUntil = null;
                        state.Status = RunStatus.Idle;
                        Log("backoff over â€” resuming");
                    }

                    if (!opts.DryRun && _stallBackoffUntil is { } sbUntil)
                    {
                        if (DateTime.UtcNow < sbUntil)
                        {
                            PushIdleSnapshot();
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }
                        _stallBackoffUntil = null;
                        Log("stall backoff over â€” resuming");
                    }

                    // F3.4: preflight health park â€” wait for backoff timer, then recheck
                    if (!opts.DryRun && _dnsParkedUntil is { } dpUntil)
                    {
                        if (DateTime.UtcNow < dpUntil)
                        {
                            PushIdleSnapshot();
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }
                        _dnsParkedUntil = null;
                        var recheckResults = await PreflightHealth.RunAllAsync(
                            plan.Limits.DnsHealthCheck, plan.Repo, _runCostUsd,
                            plan.Limits.MaxRunCostUsd).ConfigureAwait(false);
                        if (PreflightHealth.AllPassed(recheckResults))
                        {
                            _preflightConsecutiveFailures = 0;
                            Log("preflight recovered â€” resuming session");
                        }
                        else
                        {
                            _preflightConsecutiveFailures++;
                            var backoff = PreflightHealth.ComputeBackoff(
                                _preflightConsecutiveFailures,
                                plan.Limits.DnsHealthCheck?.IntervalSeconds ?? 60,
                                plan.Limits.DnsHealthCheck?.BackoffMultiplier ?? 2.0,
                                plan.Limits.DnsHealthCheck?.MaxBackoffSeconds ?? 3600);
                            _dnsParkedUntil = DateTime.UtcNow.AddSeconds(backoff);
                            Log($"preflight still failing (Ã—{_preflightConsecutiveFailures}) â€” parking {backoff}s");
                            PushIdleSnapshot();
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                            continue;
                        }
                    }

                var track = _progress.Read(plan, ct);
                if (track.Checkpoints.Count == 0)
                {
                    NeedsHuman($"tracker {plan.Tracker} has no parseable checkpoint rows â€” check the table format");
                    continue;
                }

                // perPhase: a completed stage owes a full-battery verification (then audit) before we advance.
                if (plan.PerPhaseGates && state.PendingPhaseGate != null)
                {
                    if (opts.DryRun)
                    {
                        sink.Log($"--- DRY RUN: would run the FULL-battery phase gate for stage {state.PendingPhaseGate.StageId} (nothing executed) ---");
                        return 0;
                    }
                    await RunPhaseGateAsync(state.PendingPhaseGate, ct).ConfigureAwait(false);
                    if (opts.Once && state.PendingAudit == null && state.PendingFix == null) return 0;
                    continue;
                }

                var allDone = AllEffectivelyDone(track);
                if (allDone && state.PendingFix == null && state.PendingResume == null)
                {
                    if (await ConfirmCompletionAsync(ct).ConfigureAwait(false)) { CompletePlan(track); return 0; }
                    continue; // gates red on a "done" tracker â€” a fix session is now queued
                }

                // tracker may say done while a fix/resume is pending â€” keep working the last active stage
                var stage = allDone
                    ? plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage) ?? plan.Stages[^1]
                    : SelectStage(track);
                if (stage == null)
                {
                    NeedsHuman("no runnable stage left (remaining stages are skipped) â€” review skipped stages");
                    continue;
                }
                if (stage.Id != state.CurrentStage)
                {
                    state.CurrentStage = stage.Id;
                    state.CurrentStageStartHead = Git.Head(plan.Repo);
                    state.AttemptsThisStage = 0;
                    state.PendingFix = null;
                    Log($"stage â†’ {stage.Id} {stage.Title}");
                    events.Emit(new StageEntered { StageId = stage.Id, Title = stage.Title, StartHead = state.CurrentStageStartHead });
                    _runDb?.InitializeStage(state.RunId, stage.Id, stage.Title);
                    Save();
                }

                // B10.3: pre-hook runs once per stage, before any session. A non-zero exit blocks the
                // stage (NeedsHuman) so a broken environment never wastes session attempts.
                // RunStageHook records the stage id in PreHookRunStages ONLY on success â€” a failed
                // pre-hook will retry on the next loop iteration after the human resolves the issue.
                if (stage.PreHook is { } preHook
                    && !state.PreHookRunStages.Contains(stage.Id))
                {
                    await RunStageHookAsync(stage.Id, "pre-hook", preHook, ct).ConfigureAwait(false);
                    Save();
                    if (state.Status == RunStatus.NeedsHuman) continue;
                }

                if (HandoffWantsHuman(track))
                {
                    NeedsHuman("agent asked for a human in the tracker handoff (HUMAN: line) â€” resolve, then run `conductor resume`");
                    continue;
                }

                // perPhase: this stage's rows are all DONE but it isn't confirmed yet, and no fix/resume/audit
                // is queued â†’ owe a full-battery phase gate rather than another deliver session.
                if (plan.PerPhaseGates && track.StageDone(stage.Id)
                    && state.PendingFix == null && state.PendingResume == null && state.PendingAudit == null)
                {
                    if (opts.DryRun)
                    {
                        sink.Log($"--- DRY RUN: stage {stage.Id} checkpoints all DONE â€” would schedule the audit / full-battery phase gate next (nothing executed) ---");
                        return 0;
                    }
                    ScheduleGateOrAudit(stage.Id, state.CurrentStageStartHead ?? Git.Head(plan.Repo));
                    Save();
                    continue;
                }

                var maxAttempts = MaxAttempts(stage);
                if (state.AttemptsThisStage >= maxAttempts && state.PendingAudit == null)
                {
                    if (!await EscalateExhaustedStageAsync(stage, track, maxAttempts).ConfigureAwait(false)) continue; // paused/skip handled inside
                }

                if (opts.MaxSessions > 0 && sessionsThisRun >= opts.MaxSessions)
                {
                    Log($"--max-sessions {opts.MaxSessions} reached â€” stopping");
                    return 0;
                }

                if (opts.DryRun)
                {
                    var kind = state.PendingResume != null ? SessionKind.Resume
                        : state.PendingAudit != null ? SessionKind.Audit
                        : state.PendingFix != null ? SessionKind.Fix : SessionKind.Deliver;
                    var prompt = BuildPrompt(kind, stage, state.SessionCounter + 1, state.AttemptsThisStage + 1, maxAttempts);
                    var batterySection = _prompts.BatterySection(state);
                    if (batterySection.Length > 0)
                        prompt = prompt.TrimEnd() + "\n\n" + batterySection;
                    sink.Log($"--- DRY RUN: would start session #{state.SessionCounter + 1} ({kind}, stage {stage.Id}) with prompt: ---");
                    sink.Log(prompt);
                    return 0;
                }

                // Approval mode: park at AwaitingOwner before each session (B3.4). One approval runs
                // exactly one session (then we park again) â€” approving must NOT confirm the stage.
                if (plan.Limits.ApprovalMode && !_sessionApproved)
                {
                    events.Emit(new OwnerApprovalRequested { StageId = stage.Id });
                    state.Status = RunStatus.AwaitingOwner;
                    state.AwaitingOwnerReason = AwaitingOwnerReason.ApprovalMode;
                    Log($"approval mode: park before session #{state.SessionCounter + 1} on stage {stage.Id} â€” approve with R or `conductor approve`");
                    SaveAndReport();
                    continue;
                }
                _sessionApproved = false;

                // F3.4: pre-flight health check â€” DNS, API reachability, disk, git, budget.
                // Fail â†’ park with exponential backoff + Telegram notify.
                var preflightResults = await PreflightHealth.RunAllAsync(
                    plan.Limits.DnsHealthCheck, plan.Repo, _runCostUsd,
                    plan.Limits.MaxRunCostUsd).ConfigureAwait(false);
                if (PreflightHealth.AnyFailed(preflightResults))
                {
                    _preflightConsecutiveFailures++;
                    var backoff = PreflightHealth.ComputeBackoff(
                        _preflightConsecutiveFailures,
                        plan.Limits.DnsHealthCheck?.IntervalSeconds ?? 60,
                        plan.Limits.DnsHealthCheck?.BackoffMultiplier ?? 2.0,
                        plan.Limits.DnsHealthCheck?.MaxBackoffSeconds ?? 3600);
                    _dnsParkedUntil = DateTime.UtcNow.AddSeconds(backoff);
                    var failures = string.Join("; ", preflightResults.Where(r => !r.Passed).Select(r => $"{r.Name}:{r.Message}"));
                    Log($"preflight FAILED (Ã—{_preflightConsecutiveFailures}): {failures} â€” parking {backoff}s");
                    _ = telegram.PushWithKeyboardAsync(
                        $"Conductor {plan.Name}: preflight failed â€” {failures}",
                        [("Resume", "resume"), ("Skip", "skip")], CancellationToken.None);
                    Save();
                    continue;
                }
                _preflightConsecutiveFailures = 0;

                // B12.1: start read-only analysis lanes for this stage (run concurrently in scratch dirs)
                Lanes.StartAnalysisLanes(stage, track.HandoffBlock, ct);

                // P2: if a previous stage's parallel audit is pending and we're about to deliver a new
                // stage, launch the audit as a read-only lane concurrently with this session.
                if (state.PendingParallelAudit != null && state.PendingFix == null && state.PendingResume == null)
                {
                    Lanes.StartParallelAudit(state.PendingParallelAudit, ct);
                }
                // P2: inject any completed parallel audit findings from a prior run
                if (state.ParallelAuditOutcome is { Completed: true } outcome && state.PendingFix == null)
                {
                    if (outcome.MaxSeverity == AuditFindingSeverity.High)
                    {
                        // HIGH findings from a prior completed audit â€” queue fix before proceeding
                        var fixNote = $"prior parallel audit found HIGH-severity issues in stage {outcome.StageId}:\n{Trunc(outcome.Findings, 2000)}";
                        state.PendingFix = new PendingFix
                        {
                            FromSession = state.History.LastOrDefault()?.Number ?? 0,
                            GateFailures = "",
                            ProgressSummary = fixNote,
                        };
                        state.ParallelAuditOutcome = null;
                        state.Status = RunStatus.Idle;
                        Log($"parallel audit: HIGH findings from stage {outcome.StageId} â€” queuing fix session");
                        Save();
                        continue;
                    }
                }

                await RunSessionAsync(stage, track, ct).ConfigureAwait(false);
                sessionsThisRun++;
                var rec = state.History[^1];

                // B12.1: collect any lane artifacts that weren't captured during the session
                await Lanes.CollectLaneArtifactsAsync(stage.Id, ct).ConfigureAwait(false);

                _runCostUsd += rec.CostUsd ?? 0;
                _runTokens += (rec.TokensInput ?? 0) + (rec.TokensOutput ?? 0) + (rec.TokensReasoning ?? 0);
                state.PerRunCostUsd = _runCostUsd;
                state.PerRunTokens = _runTokens;
                EmitSessionFinished(rec);

                if (CheckBudgetCap()) continue; // parked at AwaitingOwner

                if (Dispatcher.ConsumePendingSkip())
                {
                    SkipStage(stage, "skipped by user control");
                }
                if (Dispatcher.ConsumePausePending())
                {
                    if (state.Status is not (RunStatus.NeedsHuman or RunStatus.Aborted)) state.Status = RunStatus.Paused;
                    Log("paused after session as requested â€” press R or run `conductor resume` to continue");
                    SaveAndReport();
                }
                if (state.StopAfterSession)
                {
                    state.StopAfterSession = false;
                    if (state.Status is not (RunStatus.NeedsHuman or RunStatus.Aborted)) state.Status = RunStatus.Paused;
                    Log("quitting after session as requested â€” run `conductor run` to continue later");
                    SaveAndReport();
                    return 0;
                }
                if (opts.Once)
                {
                    Log("--once: stopping after one session");
                    return 0;
                }
            }
            }
            catch (OperationCanceledException) { /* cancellation requested â€” fall through to cleanup */ }
            // Ctrl+C / external cancel â€” graceful shutdown (B3.5)
            Log("cancelled â€” saving state");
            try { SaveAndReport(); } catch { /* best-effort */ }
            if (state.Status == RunStatus.Running && state.History.LastOrDefault() is { EndedUtc: null } last)
            {
                last.EndedUtc = DateTime.UtcNow;
                last.Outcome = SessionOutcome.Interrupted;
                QueueResume(last, "conductor cancelled mid-session");
            }
            Log("state saved; run `conductor run` again to resume");
            Save();
            return 130;
        }
        finally { ReleaseLock(); }
    }

}
