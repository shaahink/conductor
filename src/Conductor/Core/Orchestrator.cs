using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
#pragma warning disable MA0045 // Orchestrator.Run() is sync by design — file I/O at sync boundaries is deliberate
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

public sealed record RunOptions(bool DryRun, bool Once, int MaxSessions);

/// <summary>
/// The session cycle, mechanized:
///   pick stage from tracker → spawn agent session (deliver / fix / resume) → watchdog it →
///   independently verify (gates + git + tracker diff) → record, report, decide next.
/// Every transition is persisted, so killing conductor at any point is recoverable.
/// </summary>
public sealed class Orchestrator(PlanConfig plan, RunState state, string statePath, IProgressSink sink, IEventSink events, RunOptions opts, ILogger<Orchestrator> logger, ITelegramService telegram, WebhookNotifier webhooks, IPlanner? planner = null)
{
    private readonly IPlanner _planner = planner ?? new CheckpointPlanner();
    private readonly PromptBuilder _prompts = BuildPromptBuilder(plan);
    private readonly IProgressProvider _progress = ProgressProviderFactory.Create(plan);
    // The agent provider owns backend-specific concerns (stream parsing lives in AgentSession, the
    // usage-limit phrasing lives here) so the Orchestrator no longer switches on `output` (B2.4, D-11).
    private readonly IAgentProvider _agentProvider = AgentProviderFactory.Create(plan.Agent);
    private readonly LessonsManager _lessons = new(plan.StateDir);
    private IReadOnlyList<GateResult>? _lastGates;
    private bool _pendingSkip;
    private bool _pausePending;
    private readonly string _lockPath = Path.Combine(plan.StateDir, "conductor.lock");
    private readonly string _controlPath = Path.Combine(plan.StateDir, "control.json");
    private DateTime? _lastControlWrite;
    private readonly string _logPath = Path.Combine(plan.StateDir, "conductor.log");
    private DateTime? _backoffUntil;
    private DateTime? _stallBackoffUntil;
    private int _stallBackoffMultiplier = 1;
    private DateTime? _dnsParkedUntil;
    private readonly List<(string Kind, string Text, DateTime Utc)> _activity = new();
    private readonly HashSet<string> _decomposedCheckpoints = new(StringComparer.Ordinal);
    private bool _softBreakSignalled;
    // B12.2: bounded worker pool for Tier A analysis lanes (replaces ad-hoc Task.Run of B12.1)
    private LaneWorkerPool? _lanePool;

    // Correlation state attached to every structured log line (B2.5): runId + stage + session number
    // come from RunState; the gate marker is set while a battery runs.
    private string? _curGate;
    private string? _outcome;
    private string? _gotoStageId;
    private bool _rollbackForce;
    private string? _heartbeatToggleValue;
    private readonly int _originalHeartbeatMinutes = plan.Report.HeartbeatMinutes;
    private bool _sessionApproved;
    private decimal _runCostUsd;
    private long _runTokens;
    private decimal _runOverheadUsd; // O3: gate runtime estimate accumulator

    // ---------------------------------------------------------------- main loop

    public int Run(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(plan.StateDir, "logs"));
        EnsureStateDirGitignore();
        if (!AcquireLock()) return 4;
        try
        {
            RecoverFromCrash();
            Log($"conductor start — plan '{plan.Name}', repo {plan.Repo}, branch {Git.Branch(plan.Repo)}");
            events.Emit(new RunStarted
            {
                Plan = plan.Name,
                Repo = plan.Repo,
                Branch = Git.Branch(plan.Repo),
                DriverVersion = typeof(Orchestrator).Assembly.GetName().Version?.ToString(),
                Resumed = state.SessionCounter > 0,
            });
            WarnOnBranchPattern();

            var sessionsThisRun = 0;
            _runCostUsd = state.PerRunCostUsd;
            _runTokens = state.PerRunTokens;
            _runOverheadUsd = state.PerRunOverheadCostUsd;
            if (_runCostUsd > 0 || _runTokens > 0 || _runOverheadUsd > 0)
                Log($"restored budget: ${_runCostUsd:0.00} agent / ${_runOverheadUsd:0.00} overhead / {_runTokens / 1000.0:0.#}k tokens (from prior process)");
            while (!ct.IsCancellationRequested)
            {
                HandleControl();
                if (state.Status == RunStatus.Aborted) { SaveAndReport(); return 2; }
                if (!opts.DryRun && state.Status is RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)
                {
                    PushIdleSnapshot();
                    Thread.Sleep(800);
                    continue;
                }

                if (!opts.DryRun && _backoffUntil is { } until)
                {
                    if (DateTime.UtcNow < until) { PushIdleSnapshot(); Thread.Sleep(1000); continue; }
                    _backoffUntil = null;
                    state.Status = RunStatus.Idle;
                    Log("backoff over — resuming");
                }

                if (!opts.DryRun && _stallBackoffUntil is { } sbUntil)
                {
                    if (DateTime.UtcNow < sbUntil)
                    {
                        PushIdleSnapshot();
                        Thread.Sleep(1000);
                        continue;
                    }
                    _stallBackoffUntil = null;
                    state.Status = RunStatus.Idle;
                    Log("stall backoff over — resuming");
                }

                if (!opts.DryRun && _dnsParkedUntil is { } dpUntil)
                {
                    if (DateTime.UtcNow < dpUntil)
                    {
                        PushIdleSnapshot();
                        Thread.Sleep(1000);
                        continue;
                    }
                    _dnsParkedUntil = null;
                    if (CheckDnsPreflight())
                    {
                        Log("DNS recovered — resuming session");
                    }
                    else
                    {
                        Log("DNS still unhealthy — parking again");
                        _dnsParkedUntil = DateTime.UtcNow.AddSeconds(plan.Limits.DnsHealthCheck?.IntervalSeconds ?? 60);
                        PushIdleSnapshot();
                        Thread.Sleep(1000);
                        continue;
                    }
                }

                var track = _progress.Read(plan, CancellationToken.None);
                if (track.Checkpoints.Count == 0)
                {
                    NeedsHuman($"tracker {plan.Tracker} has no parseable checkpoint rows — check the table format");
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
                    RunPhaseGate(state.PendingPhaseGate, ct);
                    if (opts.Once && state.PendingAudit == null && state.PendingFix == null) return 0;
                    continue;
                }

                var allDone = AllEffectivelyDone(track);
                if (allDone && state.PendingFix == null && state.PendingResume == null)
                {
                    if (ConfirmCompletion(ct)) { CompletePlan(track); return 0; }
                    continue; // gates red on a "done" tracker — a fix session is now queued
                }

                // tracker may say done while a fix/resume is pending — keep working the last active stage
                var stage = allDone
                    ? plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage) ?? plan.Stages[^1]
                    : SelectStage(track);
                if (stage == null)
                {
                    NeedsHuman("no runnable stage left (remaining stages are skipped) — review skipped stages");
                    continue;
                }
                if (stage.Id != state.CurrentStage)
                {
                    state.CurrentStage = stage.Id;
                    state.CurrentStageStartHead = Git.Head(plan.Repo);
                    state.AttemptsThisStage = 0;
                    state.PendingFix = null;
                    Log($"stage → {stage.Id} {stage.Title}");
                    events.Emit(new StageEntered { StageId = stage.Id, Title = stage.Title, StartHead = state.CurrentStageStartHead });
                    Save();
                }

                // B10.3: pre-hook runs once per stage, before any session. A non-zero exit blocks the
                // stage (NeedsHuman) so a broken environment never wastes session attempts.
                // RunStageHook records the stage id in PreHookRunStages ONLY on success — a failed
                // pre-hook will retry on the next loop iteration after the human resolves the issue.
                if (stage.PreHook is { } preHook
                    && !state.PreHookRunStages.Contains(stage.Id))
                {
                    RunStageHook(stage.Id, "pre-hook", preHook, ct);
                    Save();
                    if (state.Status == RunStatus.NeedsHuman) continue;
                }

                if (HandoffWantsHuman(track))
                {
                    NeedsHuman("agent asked for a human in the tracker handoff (HUMAN: line) — resolve, then run `conductor resume`");
                    continue;
                }

                // perPhase: this stage's rows are all DONE but it isn't confirmed yet, and no fix/resume/audit
                // is queued → owe a full-battery phase gate rather than another deliver session.
                if (plan.PerPhaseGates && track.StageDone(stage.Id)
                    && state.PendingFix == null && state.PendingResume == null && state.PendingAudit == null)
                {
                    if (opts.DryRun)
                    {
                        sink.Log($"--- DRY RUN: stage {stage.Id} checkpoints all DONE — would schedule the audit / full-battery phase gate next (nothing executed) ---");
                        return 0;
                    }
                    ScheduleGateOrAudit(stage.Id, state.CurrentStageStartHead ?? Git.Head(plan.Repo));
                    Save();
                    continue;
                }

                var maxAttempts = MaxAttempts(stage);
                if (state.AttemptsThisStage >= maxAttempts && state.PendingAudit == null)
                {
                    if (!EscalateExhaustedStage(stage, track, maxAttempts)) continue; // paused/skip handled inside
                }

                if (opts.MaxSessions > 0 && sessionsThisRun >= opts.MaxSessions)
                {
                    Log($"--max-sessions {opts.MaxSessions} reached — stopping");
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
                // exactly one session (then we park again) — approving must NOT confirm the stage.
                if (plan.Limits.ApprovalMode && !_sessionApproved)
                {
                    events.Emit(new OwnerApprovalRequested { StageId = stage.Id });
                    state.Status = RunStatus.AwaitingOwner;
                    state.AwaitingOwnerReason = AwaitingOwnerReason.ApprovalMode;
                    Log($"approval mode: park before session #{state.SessionCounter + 1} on stage {stage.Id} — approve with R or `conductor approve`");
                    SaveAndReport();
                    continue;
                }
                _sessionApproved = false;

                // O2: DNS preflight — verify network health before spawning an agent session.
                if (plan.Limits.DnsHealthCheck is { Enabled: true } && !CheckDnsPreflight())
                {
                    _dnsParkedUntil = DateTime.UtcNow.AddSeconds(plan.Limits.DnsHealthCheck.IntervalSeconds);
                    Log("DNS preflight failed — parking until DNS resolves");
                    Save();
                    continue;
                }

                // B12.1: start read-only analysis lanes for this stage (run concurrently in scratch dirs)
                StartAnalysisLanes(stage, track.HandoffBlock, ct);

                RunSession(stage, track, ct);
                sessionsThisRun++;
                var rec = state.History[^1];

                // B12.1: collect any lane artifacts that weren't captured during the session
                CollectLaneArtifacts(stage.Id);

                _runCostUsd += rec.CostUsd ?? 0;
                _runTokens += (rec.TokensInput ?? 0) + (rec.TokensOutput ?? 0) + (rec.TokensReasoning ?? 0);
                state.PerRunCostUsd = _runCostUsd;
                state.PerRunTokens = _runTokens;
                EmitSessionFinished(rec);

                if (CheckBudgetCap()) continue; // parked at AwaitingOwner

                if (_pendingSkip)
                {
                    _pendingSkip = false;
                    SkipStage(stage, "skipped by user control");
                }
                if (_pausePending)
                {
                    _pausePending = false;
                    if (state.Status is not (RunStatus.NeedsHuman or RunStatus.Aborted)) state.Status = RunStatus.Paused;
                    Log("paused after session as requested — press R or run `conductor resume` to continue");
                    SaveAndReport();
                }
                if (state.StopAfterSession)
                {
                    state.StopAfterSession = false;
                    if (state.Status is not (RunStatus.NeedsHuman or RunStatus.Aborted)) state.Status = RunStatus.Paused;
                    Log("quitting after session as requested — run `conductor run` to continue later");
                    SaveAndReport();
                    return 0;
                }
                if (opts.Once)
                {
                    Log("--once: stopping after one session");
                    return 0;
                }
            }
            // Ctrl+C / external cancel — graceful shutdown (B3.5)
            Log("cancelled — writing final heartbeat, queueing resume, saving state");
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

    // ---------------------------------------------------------------- one session

    private void RunSession(StageConfig stage, TrackerSnapshot preTrack, CancellationToken ct)
    {
        // consume pending fix/resume/audit — they describe THIS session
        var pendingResume = state.PendingResume; state.PendingResume = null;
        var pendingAudit = state.PendingAudit; state.PendingAudit = null;
        var pendingFix = state.PendingFix; state.PendingFix = null;
        var kind = pendingResume != null ? SessionKind.Resume
            : pendingAudit != null ? SessionKind.Audit
            : pendingFix != null ? SessionKind.Fix : SessionKind.Deliver;

        state.SessionCounter++;
        var attempt = state.AttemptsThisStage + 1;
        var maxAttempts = MaxAttempts(stage);
        var isReview = stage.Kind.Equals("review", StringComparison.OrdinalIgnoreCase);
        var reviewDir = Path.Combine(plan.StateDir, "reviews");
        var reviewPath = isReview ? Path.Combine(reviewDir, $"{stage.Id}.md") : "";
        if (isReview)
        {
            // B8.3: scaffold the review artifact before the session starts so the agent
            // is told where to write it; the content is advisory-only, never auto-applied.
            Directory.CreateDirectory(reviewDir);
            var skeleton = $"# Self-review: {stage.Id} — {stage.Title}\n\n" +
                           $"_Generated {DateTime.UtcNow:u} by Conductor (B8.3) — pending agent review_\n";
            File.WriteAllText(reviewPath, skeleton);
            Log($"review stage {stage.Id}: scaffolded review artifact at {reviewPath}");
        }

        // B9.2: if this is a Deliver session with planner persona, and the active checkpoint
        // hasn't been decomposed yet, seed the task graph with ordered sub-tasks.
        var personaName = plan.ResolvePersona(stage);
        var activeCp = preTrack.ForStage(stage.Id).FirstOrDefault(c => !c.IsDone);
        if (kind == SessionKind.Deliver &&
            "planner".Equals(personaName, StringComparison.OrdinalIgnoreCase) &&
            activeCp != null &&
            _decomposedCheckpoints.Add(activeCp.Id))
        {
            var tasks = _planner.Decompose(activeCp.Id, activeCp.Title, stage.Notes ?? "");
            var runId = state.RunId;
            foreach (var task in tasks)
            {
                events.Emit(new TaskAdded
                {
                    RunId = runId,
                    TaskId = $"{activeCp.Id}-t{task.Order}",
                    CheckpointId = activeCp.Id,
                    Title = task.Title,
                    Source = "planner",
                    Order = task.Order,
                });
            }
            if (tasks.Count > 0)
                Log($"B9.2: decomposed checkpoint {activeCp.Id} into {tasks.Count} sub-task(s)");
        }

        var prompt = kind switch
        {
            SessionKind.Resume => _prompts.Resume(stage, state.SessionCounter, attempt, maxAttempts, pendingResume!),
            SessionKind.Audit => _prompts.Audit(stage, state.SessionCounter, pendingAudit!, state.CurrentStageStartHead ?? "HEAD~1"),
            SessionKind.Fix => _prompts.Fix(stage, state.SessionCounter, attempt, maxAttempts, pendingFix!),
            _ => isReview
                ? _prompts.Review(stage, state.SessionCounter, attempt, maxAttempts, reviewPath)
                : _prompts.Deliver(stage, state.SessionCounter, attempt, maxAttempts),
        };
        // Append bounded battery sections (B8.5): lessons, recent failures, etc.
        var batterySection = _prompts.BatterySection(state);
        if (batterySection.Length > 0)
            prompt = prompt.TrimEnd() + "\n\n" + batterySection;

        var rec = new SessionRecord
        {
            Number = state.SessionCounter,
            Stage = stage.Id,
            Kind = kind,
            Attempt = attempt,
            StartedUtc = DateTime.UtcNow,
            ClaudeSessionId = pendingResume?.ClaudeSessionId ?? Guid.NewGuid().ToString(),
            ResumeCount = pendingResume?.ResumeCount ?? 0,
        };
        var logsDir = Path.Combine(plan.StateDir, "logs");
        File.WriteAllText(Path.Combine(logsDir, $"session-{rec.Number:000}.prompt.md"), prompt);
        // Queued human instructions are now baked into this prompt — mark them consumed so the next
        // session doesn't re-inject them (files are renamed .done, not deleted — chain stays intact).
        InstructionQueue.ConsumeAll(plan);
        var rawLog = Path.Combine(logsDir, $"session-{rec.Number:000}.jsonl");

        var startHead = Git.Head(plan.Repo);
        state.History.Add(rec);
        state.Status = RunStatus.Running;
        Save();
        _softBreakSignalled = false;
        // B9.4: clean up any stale soft-break signal from a previous session
        CleanSoftBreakSignal();
        Log($"session #{rec.Number} start — {kind} {stage.Id} attempt {attempt}/{maxAttempts}" +
            (kind == SessionKind.Resume ? $" (resume #{rec.ResumeCount} of {rec.ClaudeSessionId[..8]})" : ""));
        events.Emit(new SessionStarted
        {
            SessionId = rec.Number.ToString(),
            Number = rec.Number,
            StageId = stage.Id,
            Kind = kind.ToString(),
            Attempt = attempt,
            MaxAttempts = maxAttempts,
            AgentSessionId = rec.ClaudeSessionId,
            Persona = plan.ResolvePersona(stage),
        });

        bool stalled = false, timedOut = false, killedByUser = false;
        GateRunner.RunHook(plan, plan.Setup, "setup", Log, ct);
        var resolvedAgent = plan.ResolveAgent(stage);
        using (var agent = AgentSession.Start(resolvedAgent, plan.Repo, prompt, rec.ClaudeSessionId,
                   kind == SessionKind.Resume ? rec.ClaudeSessionId : null, rawLog, events, rec.Number.ToString()))
        {
            _activity.Clear();
            var lastHeartbeat = DateTime.UtcNow;
            while (!agent.HasExited)
            {
                while (agent.TryDequeue(out var ev)) { sink.AgentEvent(ev); TrackActivity(ev); }
                // B12.1: check for completed analysis lanes running concurrently
                PollLaneCompletion();
                // B9.4: soft-break — when live tokens cross the soft threshold, write a cooperative
                // nudge for the agent to finish the current sub-task and hand off cleanly.
                CheckSoftBreak(agent, preTrack);
                var ctl = HandleControl(inSession: true);
                if (ctl == ControlAction.KillSession) { killedByUser = true; Log("kill requested"); agent.Kill(); }
                if (ctl == ControlAction.AbortNow) { killedByUser = true; state.Status = RunStatus.Aborted; Log("abort requested"); agent.Kill(); }
                if (ct.IsCancellationRequested) { agent.Kill(); }
                else if ((DateTime.UtcNow - agent.LastActivityUtc).TotalMinutes > plan.Limits.StallMinutes)
                {
                    stalled = true;
                    Log($"stall: no agent output for {plan.Limits.StallMinutes}m — killing session");
                    agent.Kill();
                }
                else if ((DateTime.UtcNow - agent.StartedUtc).TotalMinutes > plan.Limits.SessionTimeoutMinutes)
                {
                    timedOut = true;
                    Log($"timeout: session exceeded {plan.Limits.SessionTimeoutMinutes}m — killing");
                    agent.Kill();
                }
                PushSessionSnapshot(agent, rec, stage, attempt, maxAttempts, preTrack);
                // AFK heartbeat: refresh+commit REPORT.md mid-session so the GitHub view reflects live progress.
                if (plan.Report.HeartbeatMinutes > 0 && (DateTime.UtcNow - lastHeartbeat).TotalMinutes >= plan.Report.HeartbeatMinutes)
                {
                    lastHeartbeat = DateTime.UtcNow;
                    HeartbeatReport(rec, stage, agent, preTrack);
                }
                Thread.Sleep(400);
            }
            var exit = agent.WaitForExitCode();
            while (agent.TryDequeue(out var ev)) { sink.AgentEvent(ev); TrackActivity(ev); }
            agent.ReapStrays();

            rec.EndedUtc = DateTime.UtcNow;
            rec.CostUsd = agent.CostUsd;
            rec.NumTurns = agent.NumTurns;
            rec.TokensInput = agent.TokensInput;
            rec.TokensOutput = agent.TokensOutput;
            rec.TokensReasoning = agent.TokensReasoning;
            rec.TokensCacheRead = agent.TokensCacheRead;
            rec.ResultSummary = ExtractSessionResult(agent.ResultText);
            Log($"session #{rec.Number} exited (code {exit}, {(rec.EndedUtc - rec.StartedUtc).Value.TotalMinutes:0}m" +
                (agent.CostUsd.HasValue ? $", ${agent.CostUsd:0.00}" : "") + ")");

            // B9.4: fold any MCP journal entries into the main event log so agent-initiated
            // task status changes survive the session.
            FoldMcpJournal();

            if (ct.IsCancellationRequested)
            {
                rec.Outcome = SessionOutcome.Interrupted;
                QueueResume(rec, "conductor was cancelled mid-session");
                Save();
                return;
            }
            if (state.Status == RunStatus.Aborted)
            {
                rec.Outcome = SessionOutcome.KilledByUser;
                Save();
                return;
            }

            // usage/rate limit → wait it out, then resume the same agent session (no attempt burned)
            var limitEvidence = (agent.ResultText ?? "") + " " + (exit != 0 && agent.ResultText == null ? LastRawTail(rawLog) : "");
            if ((agent.ResultIsError || exit != 0) && _agentProvider.DetectsUsageLimit(limitEvidence))
            {
                rec.Outcome = SessionOutcome.LimitBackoff;
                state.ConsecutiveBackoffs++;
                if (state.ConsecutiveBackoffs > plan.Limits.MaxBackoffs)
                {
                    NeedsHuman($"agent backend refused {state.ConsecutiveBackoffs} times in a row (usage limit?) — check quota");
                    return;
                }
                QueueResume(rec, "usage/rate limit backoff", countResume: false);
                _backoffUntil = DateTime.UtcNow.AddMinutes(plan.Limits.BackoffMinutes);
                state.Status = RunStatus.Backoff;
                Log($"usage limit detected — backing off {plan.Limits.BackoffMinutes}m (until {_backoffUntil:HH:mm} UTC)");
                SaveAndReport();
                return;
            }
            state.ConsecutiveBackoffs = 0;

            // B8.5/B9.4: per-session token budget — if the session exceeded maxSessionTokens, end it
            // as RolledOver so the next session starts fresh with no attempt burned. B9.4 adds
            // task-graph-aware resume: the next session knows which sub-task to continue from.
            if (plan.Limits.MaxSessionTokens is { } maxTok && rec.TokensTotal >= maxTok)
            {
                rec.Outcome = SessionOutcome.RolledOver;
                rec.ResultSummary = ExtractSessionResult(agent.ResultText);
                var resumeCtx = BuildRolloverResumeHint(preTrack);
                Log($"session #{rec.Number} rolled over — {rec.TokensTotal / 1000.0:0.#}k tokens ≥ {maxTok / 1000.0:0.#}k limit, handoff written{(resumeCtx != null ? $" · {resumeCtx}" : "")}");
                ReflectionStep(rec);
                SaveAndReport();
                return;
            }

            EvaluateSession(rec, stage, preTrack, startHead, stalled, timedOut, killedByUser,
                agentErrored: agent.ResultIsError || (exit != 0 && !stalled && !timedOut && !killedByUser), ct);

            // B8.1: after every session, distill "what was hard" into rolling lessons
            ReflectionStep(rec);
        }
    }

    private void EvaluateSession(SessionRecord rec, StageConfig stage, TrackerSnapshot preTrack, string startHead,
        bool stalled, bool timedOut, bool killedByUser, bool agentErrored, CancellationToken ct)
    {
        // Handle non-finishing sessions before spending ~minutes on the gate battery.
        if (killedByUser)
        {
            rec.Outcome = SessionOutcome.KilledByUser;
            state.Status = RunStatus.Paused;
            Log("session killed by user — pausing (conductor resume to continue)");
            SaveAndReport();
            return;
        }
        if (stalled || timedOut)
        {
            rec.Outcome = stalled ? SessionOutcome.Stalled : SessionOutcome.TimedOut;
            state.AttemptsThisStage++;
            // O2: identical-stall detection — if 2+ consecutive sessions stalled with zero
            // commits and empty result summary, skip to NeedsHuman.
            if (stalled && plan.Limits.StallPatternTermination)
            {
                rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
                if (IdenticalStallPattern(rec))
                {
                    NeedsHuman($"identical-stall: {rec.Number - 1} sessions stalled with no commits, no output — environment or agent is broken");
                    return;
                }
                _stallBackoffMultiplier++;
            }
            else
            {
                _stallBackoffMultiplier = 1;
            }
            // O2: exponential backoff between stalled attempts.
            if (stalled)
            {
                var delayMinutes = plan.Limits.StallBackoffMinutes * _stallBackoffMultiplier;
                _stallBackoffUntil = DateTime.UtcNow.AddMinutes(delayMinutes);
                Log($"stall backoff: {delayMinutes}m (multiplier ×{_stallBackoffMultiplier}) until {_stallBackoffUntil:HH:mm} UTC");
            }
            else
            {
                _stallBackoffUntil = null;
            }
            if (rec.ResumeCount < plan.Limits.MaxResumesPerSession)
            {
                QueueResume(rec, stalled ? "session stalled (no output)" : "session hit the hard timeout");
                Log($"will resume agent session (resume {rec.ResumeCount + 1}/{plan.Limits.MaxResumesPerSession})");
            }
            else
            {
                var verdict = ConsultAdvisor(rec, stage, _progress.Read(plan, CancellationToken.None), "resume budget exhausted after stall/timeout");
                ApplyVerdict(verdict, rec, stage, defaultAction: "retry");
            }
            state.Status = RunStatus.Idle;
            SaveAndReport();
            return;
        }
        _stallBackoffMultiplier = 1;

        // Audit session: its fixes are confirmed by the full-battery phase gate that runs next,
        // so we don't run gates inline — just record it and schedule re-verification.
        if (rec.Kind == SessionKind.Audit)
        {
            rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
            rec.Outcome = SessionOutcome.Progress;
            if (!state.AuditedStages.Contains(stage.Id)) state.AuditedStages.Add(stage.Id);
            state.PendingAudit = null;
            state.PendingPhaseGate = new PendingPhaseGate
            {
                StageId = stage.Id,
                StageStartHead = state.CurrentStageStartHead ?? startHead,
            };
            state.Status = RunStatus.Idle;
            Log($"audit session #{rec.Number} complete ({rec.NewCommits.Count} commits) — re-verifying phase {stage.Id} with full battery");

            // B8.4: parse the audit handover for deferred/weak bullets and track as followups
            ParseAuditFollowups(stage.Id);

            SaveAndReport();
            return;
        }

        state.Status = RunStatus.VerifyingGates;
        Save();
        PushIdleSnapshot();
        // perPhase: cheap per-session check (fast-tier gates only); the full battery runs at phase end.
        Log(plan.PerPhaseGates
            ? "verifying independently: fast gates + git + tracker diff (full battery at phase end)"
            : "verifying independently: gate battery + git + tracker diff");
        var gates = RunGateBattery(ct, fastOnly: plan.PerPhaseGates);
        _lastGates = gates;
        rec.GateSummary = GateRunner.Summary(gates);
        EmitGates(gates, "session", rec.Number.ToString());
        var sessionOverhead = gates.Sum(g => g.EstimatedCostUsd(plan.Limits.OverheadCostPerSecond));
        rec.OverheadCostUsd = sessionOverhead;
        _runOverheadUsd += sessionOverhead;
        state.PerRunOverheadCostUsd = _runOverheadUsd;

        // A gate cut short by Ctrl+C / abort is not a real failure — don't burn a fix on it.
        if (ct.IsCancellationRequested)
        {
            rec.Outcome = SessionOutcome.Interrupted;
            QueueResume(rec, "conductor was cancelled during gate verification");
            state.Status = RunStatus.Idle;
            Log("verification interrupted — will re-verify on resume (no fix queued)");
            SaveAndReport();
            return;
        }

        var postTrack = _progress.Read(plan, CancellationToken.None);
        rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
        rec.NewlyDone = postTrack.Checkpoints
            .Where(c => c.IsDone && !(preTrack.ById(c.Id)?.IsDone ?? false))
            .Select(c => c.Id).ToList();
        var newlyBlocked = postTrack.Checkpoints
            .Where(c => c.IsBlocked && !(preTrack.ById(c.Id)?.IsBlocked ?? false))
            .Select(c => c.Id).ToList();
        var gatesGreen = GateRunner.AllRequiredPassed(gates);
        var dirty = Git.IsDirty(plan.Repo);

        Log($"verdict inputs: gates {(gatesGreen ? "green" : "RED")} · commits {rec.NewCommits.Count} · newly DONE [{string.Join(",", rec.NewlyDone)}] · dirty {(dirty ? "YES" : "no")}", gatesGreen ? "pass" : "fail");

        if (newlyBlocked.Count > 0 && plan.PauseOnBlocked)
        {
            NeedsHuman($"checkpoint(s) newly BLOCKED: {string.Join(", ", newlyBlocked)} — see tracker handoff");
            SaveAndReport();
            return;
        }

        if (gatesGreen && rec.NewCommits.Count > 0 && !agentErrored)
        {
            rec.Outcome = rec.NewlyDone.Count > 0 ? SessionOutcome.Advanced : SessionOutcome.Progress;
            state.AttemptsThisStage = rec.NewlyDone.Count > 0 ? 0 : state.AttemptsThisStage + 1;
            state.PendingFix = null;
            if (dirty) Log($"note: working tree left dirty after green session: {Git.DirtySummary(plan.Repo)}");
            Log($"session #{rec.Number} {rec.Outcome} — {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) + " done" : "no checkpoint flipped yet")}", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");

            // perPhase: if this session completed the stage, schedule the audit / confirming battery.
            if (plan.PerPhaseGates && postTrack.StageDone(stage.Id))
            {
                ScheduleGateOrAudit(stage.Id, state.CurrentStageStartHead ?? startHead);
            }
        }
        else
        {
            rec.Outcome = agentErrored ? SessionOutcome.AgentError : gatesGreen ? SessionOutcome.NoProgress : SessionOutcome.GatesRed;
            state.AttemptsThisStage++;
            state.PendingFix = new PendingFix
            {
                FromSession = rec.Number,
                GateFailures = GateRunner.FailureDetails(gates),
                ProgressSummary = $"new commits: {rec.NewCommits.Count}" +
                                  (rec.NewCommits.Count > 0 ? $" ({string.Join("; ", rec.NewCommits.Take(5))})" : "") +
                                  $" · newly DONE: {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) : "none")}" +
                                  $" · working tree: {(dirty ? "DIRTY — " + Git.DirtySummary(plan.Repo) : "clean")}" +
                                  (agentErrored ? " · agent process reported an error result" : ""),
            };
            Log($"session #{rec.Number} {rec.Outcome} — queuing fix session (attempt {state.AttemptsThisStage}/{MaxAttempts(stage)})", rec.Outcome?.ToString().ToLowerInvariant() ?? "unknown");
        }
        state.Status = RunStatus.Idle;
        SaveAndReport();
    }

    /// <summary>Full-battery verification of a stage whose checkpoints are all DONE (perPhase policy).
    /// Under the reworked flow the audit runs first, so this is the single confirming battery. Skips
    /// the run entirely when the tree is unchanged since the last green battery (HEAD-sha cache).</summary>
    private void RunPhaseGate(PendingPhaseGate pg, CancellationToken ct)
    {
        var head = Git.Head(plan.Repo);
        var sig = GateRunner.BatterySignature(plan, head, pg.StageId);
        IReadOnlyList<GateResult> gates;
        bool green;

        if (sig == state.LastGreenGateSig)
        {
            Log($"phase gate {pg.StageId}: tree unchanged since last green battery ({Short(head)}) — reusing result, skipping rerun");
            green = true;
            gates = _lastGates ?? Array.Empty<GateResult>();
        }
        else
        {
            state.Status = RunStatus.VerifyingGates;
            Save();
            PushIdleSnapshot();
            Log($"phase gate {pg.StageId}: running FULL battery at {Short(head)} to confirm the phase");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            gates = RunGateBattery(ct, fastOnly: false);
            _lastGates = gates;

            if (ct.IsCancellationRequested)
            {
                state.Status = RunStatus.Idle;
                Log("phase gate interrupted — will re-run on resume");
                Save();
                return;
            }
            green = GateRunner.AllRequiredPassed(gates);
            EmitGates(gates, "phase");
            _runOverheadUsd += gates.Sum(g => g.EstimatedCostUsd(plan.Limits.OverheadCostPerSecond));
            state.PerRunOverheadCostUsd = _runOverheadUsd;
            Log($"phase gate {pg.StageId} finished in {sw.Elapsed.TotalSeconds:0}s — {(green ? "GREEN" : "RED")}: {GateRunner.Summary(gates)}", green ? "pass" : "fail");
            if (green) state.LastGreenGateSig = sig;
        }

        if (green)
        {
            if (plan.Audit is { Enabled: true } && !state.AuditedStages.Contains(pg.StageId))
            {
                state.PendingAudit = new PendingAudit { StageId = pg.StageId, StageStartHead = pg.StageStartHead };
                state.PendingPhaseGate = null;
                state.Status = RunStatus.Idle;
                Log($"phase {pg.StageId} full battery GREEN — queuing auto-fix audit session");
                SaveAndReport();
            }
            else
            {
                ConfirmStage(pg.StageId);
            }
        }
        else
        {
            state.AttemptsThisStage++;
            state.PendingFix = new PendingFix
            {
                FromSession = state.History.LastOrDefault()?.Number ?? 0,
                GateFailures = GateRunner.FailureDetails(gates),
                ProgressSummary = $"phase {pg.StageId} full battery is RED although its checkpoints read DONE — make the claims true",
            };
            state.PendingPhaseGate = null;
            state.Status = RunStatus.Idle;
            Log($"phase {pg.StageId} full battery RED — queuing fix session (attempt {state.AttemptsThisStage}/{MaxAttempts(CurrentStageConfig())})");
            SaveAndReport();
        }
    }

    /// <summary>A stage's checkpoints are all DONE: schedule the audit first (single battery runs after
    /// it) or, if audit is disabled/done, the confirming full battery.</summary>
    private void ScheduleGateOrAudit(string stageId, string startHead)
    {
        if (plan.Audit is { Enabled: true } && !state.AuditedStages.Contains(stageId))
        {
            state.PendingAudit = new PendingAudit { StageId = stageId, StageStartHead = startHead };
            state.PendingPhaseGate = null;
            Log($"stage {stageId} checkpoints all DONE — scheduling auto-fix audit (single confirming battery runs after it)");
        }
        else
        {
            state.PendingPhaseGate = new PendingPhaseGate { StageId = stageId, StageStartHead = startHead };
            Log($"stage {stageId} checkpoints all DONE — scheduling full-battery phase gate");
        }
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;

    private void ConfirmStage(string id)
    {
        var stage = plan.Stages.FirstOrDefault(s => s.Id == id);
        if (stage is { OwnerGate: true } && !state.OwnerApprovedStages.Contains(id))
        {
            events.Emit(new OwnerApprovalRequested { StageId = id });
            state.Status = RunStatus.AwaitingOwner;
            state.AwaitingOwnerReason = AwaitingOwnerReason.OwnerGate;
            Log($"owner-gate: stage {id} green — awaiting owner approval (run `conductor approve` or press R in the TUI)");
            SaveAndReport();
            Notify($"Conductor {plan.Name}: stage {id} is green and awaiting owner approval");
            _ = telegram.PushWithKeyboardAsync($"Stage {id} green — owner approval needed",
                [("Approve", "approve")]);
            return;
        }
        if (!state.ConfirmedStages.Contains(id)) state.ConfirmedStages.Add(id);
        state.AwaitingOwnerReason = null;
        state.PendingPhaseGate = null;
        state.PendingAudit = null;
        state.PendingFix = null;
        state.AttemptsThisStage = 0;
        // B10.3: post-hook runs after confirmation (best-effort, logged but never blocks).
        if (stage?.PostHook is { } postHook)
            RunStageHook(id, "post-hook", postHook, CancellationToken.None);
        // B12.4: fix-lanes run after the stage is confirmed — they consume .conductor/followups.md
        // entries owned by this stage and run as Tier B mutating lanes behind merge gates.
        RunFollowupFixLanes(id);
        if (state.PauseAfterStage)
        {
            state.PauseAfterStage = false;
            state.Status = RunStatus.Paused;
            Log($"✓ phase {id} CONFIRMED — parked (pause-after-stage was set)");
        }
        else
        {
            state.Status = RunStatus.Idle;
            Log($"✓ phase {id} CONFIRMED (full battery green{(state.AuditedStages.Contains(id) ? " + audit" : "")}) — advancing");
        }
        events.Emit(new StageConfirmed { StageId = id, Audited = state.AuditedStages.Contains(id) });
        SaveAndReport();
    }

    private StageConfig CurrentStageConfig()
        => plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage) ?? plan.Stages[^1];

    /// <summary>Handle an owner approval while parked at <c>AwaitingOwner</c>. What it means depends on
    /// WHY we parked (B3.2/B3.4): an owner-gate confirms the stage; an approval-mode/budget park merely
    /// resumes work — confirming there would advance past unfinished checkpoints.</summary>
    private void ApproveAwaitingOwner()
    {
        var stageId = state.CurrentStage
            ?? plan.Stages.FirstOrDefault(s => !state.ConfirmedStages.Contains(s.Id) && !state.SkippedStages.Contains(s.Id))?.Id;
        switch (OwnerApproval.Decide(state.AwaitingOwnerReason))
        {
            case ApprovalOutcome.ResumeSession:
                _sessionApproved = true;
                state.AwaitingOwnerReason = null;
                state.Status = RunStatus.Idle;
                if (stageId != null) events.Emit(new OwnerApprovalGranted { StageId = stageId });
                Save();
                Log("owner approved (approval mode) — running the next session");
                break;
            case ApprovalOutcome.ResetBudgetAndResume:
                _runCostUsd = 0;
                _runTokens = 0;
                _runOverheadUsd = 0;
                state.PerRunCostUsd = 0;
                state.PerRunTokens = 0;
                state.PerRunOverheadCostUsd = 0;
                state.AwaitingOwnerReason = null;
                state.Status = RunStatus.Idle;
                if (stageId != null) events.Emit(new OwnerApprovalGranted { StageId = stageId });
                Save();
                Log("owner approved (budget) — budget window reset, continuing");
                break;
            default: // ConfirmStage — owner-gate on a green stage (or a legacy/unknown reason)
                if (stageId == null) { state.Status = RunStatus.Idle; Save(); break; }
                if (!state.OwnerApprovedStages.Contains(stageId))
                {
                    events.Emit(new OwnerApprovalGranted { StageId = stageId });
                    state.OwnerApprovedStages.Add(stageId);
                    Log($"owner approved stage {stageId} — continuing");
                }
                ConfirmStage(stageId);
                break;
        }
    }

    // ---------------------------------------------------------------- decisions

    /// <summary>B10.3: runs a per-stage lifecycle hook. For pre-hooks a non-zero exit triggers
    /// <see cref="NeedsHuman"/> (blocking); on success the stage id is recorded in
    /// <see cref="RunState.PreHookRunStages"/> to prevent re-run on resume. For post-hooks
    /// failures are logged only.</summary>
    private void RunStageHook(string stageId, string label, HookConfig hook, CancellationToken ct)
    {
        Log($"{label}: {stageId} — {hook.Command}");
        var cwd = string.IsNullOrWhiteSpace(hook.Cwd) ? plan.Repo : Path.Combine(plan.Repo, hook.Cwd);
        var r = ProcessRunner.RunPowerShell(hook.Command, cwd, TimeSpan.FromMinutes(hook.TimeoutMinutes), ct);
        var timedOut = r.TimedOut ? " (timed out)" : "";
        Log($"{label}: exit {r.ExitCode}{timedOut} in {r.Duration.TotalSeconds:0}s");
        if (r.ExitCode != 0)
        {
            var outputSnippet = r.Output.Length > 500 ? r.Output[..500] + "\n…(truncated)" : r.Output;
            var detail = $"stage {stageId} {label} failed (exit {r.ExitCode}): {hook.Command}";
            Log($"ERROR: {detail}\n{outputSnippet.TrimEnd()}");
            if (label == "pre-hook")
                NeedsHuman(detail);
        }
        else if (label == "pre-hook")
        {
            // Record success so the pre-hook is not re-run on resume/crash-recovery.
            // Only added on success — a failed pre-hook must retry (it is NOT recorded).
            state.PreHookRunStages.Add(stageId);
        }
    }

    private IReadOnlyList<GateResult> RunGateBattery(CancellationToken ct, bool fastOnly = false)
    {
        _curGate = fastOnly ? "battery:fast" : "battery:full";
        try
        {
            GateRunner.RunHook(plan, plan.Setup, "setup", Log, ct);
            var gates = GateRunner.RunAll(plan, Log, ct, fastOnly, state.CurrentStage, sink.GateProgress);
            GateRunner.RunHook(plan, plan.Teardown, "teardown", Log, ct);
            // Emit per-gate summary lines with outcome scope so JSON queries can filter on
            // e.g. gate=build and outcome=fail.
            foreach (var g in gates)
            {
                var outcome = g.Skipped ? "skip" : g.Passed ? "pass" : g.Optional ? "warn" : "fail";
                var prevGate = _curGate;
                _curGate = g.Name;
                Log($"gate {g.Name}: {(g.Skipped ? "SKIP" : g.Passed ? "PASS" : g.Optional ? "WARN" : "FAIL")} ({g.Duration.TotalSeconds:0}s)", outcome);
                _curGate = prevGate;
            }
            return gates;
        }
        finally { _curGate = null; }
    }

    private StageConfig? SelectStage(TrackerSnapshot track)
    {
        // B10.1: readiness = stage itself not complete/skipped AND all dependsOn satisfied.
        // Among ready stages, plan.Stages order determines priority (preserves sequential intent).
        bool IsReady(StageConfig s)
        {
            if (StageComplete(s.Id, track) || state.SkippedStages.Contains(s.Id))
                return false;
            return s.DependsOn is not { Count: > 0 }
                || s.DependsOn.All(d => DepSatisfied(d, track));
        }
        return plan.Stages.FirstOrDefault(IsReady);
    }

    private bool AllEffectivelyDone(TrackerSnapshot track)
        => plan.Stages.All(s => StageComplete(s.Id, track) || state.SkippedStages.Contains(s.Id));

    /// <summary>Under perPhase, a stage is "complete" only once its full battery (and audit) confirmed it —
    /// so a stage whose tracker rows read DONE but whose phase-gate is red is never advanced past.</summary>
    private bool StageComplete(string id, TrackerSnapshot track)
        => plan.PerPhaseGates ? state.ConfirmedStages.Contains(id) : track.StageDone(id);

    /// <summary>A dependency satisfied if the target stage is confirmed/done OR has been skipped
    /// (you can't run a skipped stage — treating it as effectively done unblocks dependents, B10.1).</summary>
    private bool DepSatisfied(string id, TrackerSnapshot track)
        => StageComplete(id, track) || state.SkippedStages.Contains(id);

    private int MaxAttempts(StageConfig stage) => Math.Max(1, stage.Sessions * plan.Limits.StageSlackFactor);

    private bool HandoffWantsHuman(TrackerSnapshot track)
        => plan.Conventions.MentionsHuman(track.HandoffBlock);

    /// <returns>true if the caller should fall through to running a session (advisor said retry)</returns>
    private bool EscalateExhaustedStage(StageConfig stage, TrackerSnapshot track, int maxAttempts)
    {
        Log($"stage {stage.Id} exhausted its attempt budget ({maxAttempts}) — consulting advisor");
        var last = state.History.LastOrDefault();
        var verdict = ConsultAdvisor(last, stage, track, $"attempt budget exhausted ({maxAttempts})");
        if (verdict?.Action == "skip")
        {
            SkipStage(stage, $"advisor: {verdict.Reason}");
            return false;
        }
        if (verdict?.Action is "retry" or "resume")
        {
            Log($"advisor says {verdict.Action} ({verdict.Reason}) — granting {stage.Sessions} more attempts");
            state.AttemptsThisStage = maxAttempts - Math.Max(1, stage.Sessions);
            Save();
            return true;
        }
        NeedsHuman($"stage {stage.Id} used all {maxAttempts} attempts without completing — inspect and `conductor resume` (or `conductor skip`)" +
                   (verdict != null ? $" · advisor: {verdict.Reason}" : ""));
        return false;
    }

    private AdvisorVerdict? ConsultAdvisor(SessionRecord? rec, StageConfig stage, TrackerSnapshot track, string outcome)
    {
        var prompt = _prompts.Advisor(stage,
            outcome + (rec?.Outcome != null ? $" (last session: {rec.Outcome})" : ""),
            rec?.GateSummary ?? "-",
            rec != null ? string.Join("; ", rec.NewCommits.Take(6)) : "-",
            Trunc(track.HandoffBlock, 1200),
            Trunc(rec?.ResultSummary ?? "", 1200),
            state.AttemptsThisStage, MaxAttempts(stage));
        Log("consulting advisor…");
        var v = Advisor.Consult(plan, prompt, Log);
        Log(v != null ? $"advisor verdict: {v.Action} — {v.Reason}" : "advisor unavailable — using deterministic default");
        return v;
    }

    private void ApplyVerdict(AdvisorVerdict? verdict, SessionRecord rec, StageConfig stage, string defaultAction)
    {
        var action = verdict?.Action ?? defaultAction;
        switch (action)
        {
            case "resume":
                QueueResume(rec, "advisor requested resume", force: true);
                break;
            case "skip":
                SkipStage(stage, $"advisor: {verdict?.Reason}");
                break;
            case "human":
                NeedsHuman($"advisor: {verdict?.Reason ?? "human intervention required"}");
                break;
            default: // retry → a fresh deliver/fix session runs next loop iteration
                break;
        }
    }

    private void QueueResume(SessionRecord rec, string reason, bool countResume = true, bool force = false)
    {
        state.PendingResume = new PendingResume
        {
            FromSession = rec.Number,
            ClaudeSessionId = rec.ClaudeSessionId,
            Reason = reason,
            ResumeCount = rec.ResumeCount + (countResume ? 1 : 0),
        };
        if (force) state.PendingResume.ResumeCount = Math.Min(state.PendingResume.ResumeCount, plan.Limits.MaxResumesPerSession - 1);
    }

    private void SkipStage(StageConfig stage, string why)
    {
        if (!state.SkippedStages.Contains(stage.Id)) state.SkippedStages.Add(stage.Id);
        state.PendingFix = null;
        state.PendingResume = null;
        state.AttemptsThisStage = 0;
        Log($"⚠ stage {stage.Id} SKIPPED ({why}) — flagged for human review in the report");
        SaveAndReport();
    }

    /// <summary>Tracker says everything is DONE — confirm with real gates before declaring victory.
    /// An agent can flip rows to DONE; it cannot flip a red build green.</summary>
    private bool ConfirmCompletion(CancellationToken ct)
    {
        var lastOutcome = state.History.LastOrDefault()?.Outcome;
        if (_lastGates != null && GateRunner.AllRequiredPassed(_lastGates) &&
            lastOutcome is SessionOutcome.Advanced or SessionOutcome.Progress)
            return true; // just verified green at the end of the last session in this run

        Log("tracker reports all checkpoints DONE — running the gate battery to confirm before closing the plan");
        state.Status = RunStatus.VerifyingGates;
        Save();
        PushIdleSnapshot();
        var gates = RunGateBattery(ct);
        _lastGates = gates;
        state.Status = RunStatus.Idle;
        EmitGates(gates, "completion");
        _runOverheadUsd += gates.Sum(g => g.EstimatedCostUsd(plan.Limits.OverheadCostPerSecond));
        state.PerRunOverheadCostUsd = _runOverheadUsd;
        if (GateRunner.AllRequiredPassed(gates)) return true;

        state.AttemptsThisStage++;
        state.PendingFix = new PendingFix
        {
            FromSession = state.History.LastOrDefault()?.Number ?? 0,
            GateFailures = GateRunner.FailureDetails(gates),
            ProgressSummary = "tracker claims all checkpoints DONE, but the gate battery is red — the claims are not yet true",
        };
        Log("completion NOT confirmed — gates red; queuing a fix session");
        Save();
        return false;
    }

    private void CompletePlan(TrackerSnapshot track)
    {
        state.Status = RunStatus.Completed;
        state.AttentionReason = state.SkippedStages.Count > 0
            ? $"plan complete EXCEPT skipped stages: {string.Join(", ", state.SkippedStages)}"
            : null;
        Log($"🎉 plan '{plan.Name}' complete — {track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} checkpoints done");
        events.Emit(new RunFinished
        {
            Status = state.Status.ToString(),
            Sessions = state.SessionCounter,
            CheckpointsDone = track.Checkpoints.Count(c => c.IsDone),
            CheckpointsTotal = track.Checkpoints.Count,
        });
        SaveAndReport();
        Notify($"Conductor: plan {plan.Name} COMPLETE ({state.SessionCounter} sessions)");
    }

    private void NeedsHuman(string reason)
    {
        state.Status = RunStatus.NeedsHuman;
        state.AttentionReason = reason;
        events.Emit(new AttentionRequested { Reason = reason });
        Log($"🛑 NEEDS HUMAN: {reason}");
        SaveAndReport();
        Notify($"Conductor {plan.Name}: needs attention — {reason}");
        _ = telegram.PushWithKeyboardAsync(reason, [("Resume", "resume"), ("Skip Stage", "skip")]);
    }

    // ---------------------------------------------------------------- O2: budget intelligence

    /// <summary>Resolves the configured DNS hosts to verify network health before spawning.
    /// Returns true if all hosts resolve or the check is disabled.</summary>
    private bool CheckDnsPreflight()
    {
        var cfg = plan.Limits.DnsHealthCheck;
        if (cfg is not { Enabled: true } || cfg.Hosts is not { Count: > 0 }) return true;
        foreach (var host in cfg.Hosts)
        {
            try
            {
                Dns.GetHostEntry(host);
                Log($"DNS preflight: {host} OK");
            }
            catch (Exception ex)
            {
                Log($"DNS preflight FAIL: {host} — {ex.Message}");
                return false;
            }
        }
        Log("DNS preflight: all hosts healthy");
        return true;
    }

    /// <summary>O2: checks whether the last 2+ sessions (including this record) match the
    /// identical-stall pattern: Stalled outcome, zero commits from startHead, and empty
    /// or null result summary.</summary>
    private bool IdenticalStallPattern(SessionRecord rec)
    {
        if (rec.NewCommits is { Count: > 0 }) return false;
        var summary = rec.ResultSummary?.Trim();
        if (!string.IsNullOrEmpty(summary)) return false;

        var stalledCount = 1;
        for (var i = state.History.Count - 2; i >= 0; i--)
        {
            var prev = state.History[i];
            if (prev.Outcome != SessionOutcome.Stalled) break;
            if (prev.NewCommits is { Count: 0 } && string.IsNullOrEmpty(prev.ResultSummary?.Trim()))
            {
                stalledCount++;
                if (stalledCount >= 2) return true;
            }
            else break;
        }
        return false;
    }

    /// <summary>Returns true if the run is now parked at <c>AwaitingOwner</c> due to a budget cap.</summary>
    private bool CheckBudgetCap()
    {
        if (plan.Limits.MaxRunCostUsd is { } costCap && _runCostUsd >= costCap)
        {
            events.Emit(new OwnerApprovalRequested { StageId = state.CurrentStage ?? "?" });
            state.Status = RunStatus.AwaitingOwner;
            state.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
            Log($"budget cap: ${_runCostUsd:0.00} >= ${costCap:0.00} (limit) — awaiting owner approval to continue");
            SaveAndReport();
            return true;
        }
        if (plan.Limits.MaxRunTokens is { } tokenCap && _runTokens >= tokenCap)
        {
            events.Emit(new OwnerApprovalRequested { StageId = state.CurrentStage ?? "?" });
            state.Status = RunStatus.AwaitingOwner;
            state.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
            Log($"token cap: {_runTokens / 1000.0:0.#}k >= {tokenCap / 1000.0:0.#}k (limit) — awaiting owner approval to continue");
            SaveAndReport();
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- control & plumbing

    private ControlAction? HandleControl(bool inSession = false)
    {
        var action = sink.PollControl() ?? ReadControlFile();
        if (action == null) return null;
        Log($"control: {action}{(inSession ? " (during session)" : "")}");
        switch (action)
        {
            case ControlAction.PauseAfterSession:
                if (inSession) _pausePending = true;
                else { state.Status = RunStatus.Paused; Save(); }
                sink.Toast(new ToastMessage($"pause-after-session {(inSession ? "queued" : "applied")}", LogSeverity.Success));
                DeleteControlFile();
                break;
            case ControlAction.StopAfterSession:
                state.StopAfterSession = true;
                sink.Toast(new ToastMessage("stop-after-session: will stop when current session ends", LogSeverity.Success));
                DeleteControlFile();
                break;
            case ControlAction.ResumeRun:
                if (state.Status is RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)
                {
                    if (state.Status == RunStatus.AwaitingOwner)
                    {
                        ApproveAwaitingOwner();
                        DeleteControlFile();
                        break;
                    }
                    state.Status = RunStatus.Idle;
                    state.AttentionReason = null;
                    Save();
                    Log("resumed by user");
                    sink.Toast(new ToastMessage("run resumed", LogSeverity.Success));
                    DeleteControlFile();
                }
                break;
            case ControlAction.SkipStage:
                if (inSession) _pendingSkip = true;
                else if (state.CurrentStage != null)
                {
                    var s = plan.Stages.FirstOrDefault(x => x.Id == state.CurrentStage);
                    if (s != null) { SkipStage(s, "skipped by user control"); sink.Toast(new ToastMessage($"stage {state.CurrentStage} skipped", LogSeverity.Success)); }
                }
                DeleteControlFile();
                break;
            case ControlAction.AbortNow when !inSession:
                state.Status = RunStatus.Aborted;
                Save();
                sink.Toast(new ToastMessage("run aborted by user", LogSeverity.Warn));
                DeleteControlFile();
                break;
            case ControlAction.RetryStage when !inSession:
                state.PendingFix = null;
                state.PendingResume = null;
                state.AttemptsThisStage = 0;
                state.Status = RunStatus.Idle;
                Save();
                Log($"retry: stage {state.CurrentStage} — attempt counter reset, re-queuing");
                sink.Toast(new ToastMessage($"retry: stage {state.CurrentStage} re-queued", LogSeverity.Success));
                DeleteControlFile();
                break;
            case ControlAction.Rollback when !inSession:
                var force = _rollbackForce; _rollbackForce = false;
                if (state.CurrentStageStartHead is not { Length: > 0 } sha)
                {
                    Log("rollback refused: no checkpoint commit recorded for current stage");
                    sink.Toast(new ToastMessage("rollback refused: no commit for current stage", LogSeverity.Error));
                    break;
                }
                if (!force && Git.IsDirty(plan.Repo))
                {
                    Log($"rollback refused: working tree is dirty — {Git.DirtySummary(plan.Repo)}. Re-run with --force to discard and reset.");
                    sink.Toast(new ToastMessage("rollback refused: dirty working tree", LogSeverity.Error));
                    break;
                }
                var fromSha = Git.Head(plan.Repo);
                Log($"rollback: resetting to {Short(sha)} (stage {state.CurrentStage} start){(force && Git.IsDirty(plan.Repo) ? " — discarding dirty working tree (--force)" : "")}");
                Git.Exec(plan.Repo, "reset", "--hard", sha);
                events.Emit(new RollbackExecuted { StageId = state.CurrentStage ?? "?", FromSha = fromSha, ToSha = sha, Forced = force });
                state.Status = RunStatus.Idle;
                Save();
                sink.Toast(new ToastMessage($"rollback: reset to {Short(sha)}", LogSeverity.Success));
                DeleteControlFile();
                break;
            case ControlAction.PauseAfterStage when !inSession:
                state.PauseAfterStage = true;
                state.Status = RunStatus.Idle;
                Save();
                Log($"pause-after-stage: will park when {state.CurrentStage} completes");
                sink.Toast(new ToastMessage($"pause-after-stage: will park after {state.CurrentStage}", LogSeverity.Success));
                DeleteControlFile();
                break;
            case ControlAction.Goto when !inSession:
                if (_gotoStageId == null) { Log("goto: no target stage — use `conductor goto <stage>`"); sink.Toast(new ToastMessage("goto: no target stage", LogSeverity.Error)); break; }
                {
                    var tg = _gotoStageId; _gotoStageId = null;
                    var target = plan.Stages.FirstOrDefault(s => s.Id == tg);
                    if (target == null) { Log($"goto refused: stage '{tg}' not found in plan"); sink.Toast(new ToastMessage($"goto refused: stage '{tg}' not found", LogSeverity.Error)); break; }
                    if (state.SkippedStages.Contains(tg)) { Log($"goto refused: stage '{tg}' is skipped"); sink.Toast(new ToastMessage($"goto refused: stage '{tg}' is skipped", LogSeverity.Error)); break; }
                    // A goto to an already-confirmed stage must actually take effect: un-confirm it (and
                    // drop any owner approval) so SelectStage re-runs it instead of silently skipping.
                    state.ConfirmedStages.Remove(tg);
                    state.OwnerApprovedStages.Remove(tg);
                    state.AwaitingOwnerReason = null;
                    state.CurrentStage = tg;
                    state.CurrentStageStartHead = Git.Head(plan.Repo);
                    state.AttemptsThisStage = 0;
                    state.PendingFix = null;
                    state.PendingResume = null;
                    state.PendingPhaseGate = null;
                    state.PendingAudit = null;
                    state.Status = RunStatus.Idle;
                    Save();
                    Log($"goto: jumped to stage {tg} {target.Title}");
                    sink.Toast(new ToastMessage($"goto: jumped to {tg} {target.Title}", LogSeverity.Success));
                    DeleteControlFile();
                }
                break;
            case ControlAction.ToggleHeartbeat:
            {
                var toggleVal = _heartbeatToggleValue;
                _heartbeatToggleValue = null;
                // TUI key press (no value) auto-flips the current state
                if (toggleVal == null)
                    toggleVal = plan.Report.HeartbeatMinutes > 0 ? "off" : "on";
                if (toggleVal == "off")
                {
                    plan.Report.HeartbeatMinutes = 0;
                    Log("heartbeat: turned OFF");
                    sink.Toast(new ToastMessage("heartbeat OFF", LogSeverity.Info));
                }
                else if (toggleVal == "on")
                {
                    plan.Report.HeartbeatMinutes = _originalHeartbeatMinutes > 0 ? _originalHeartbeatMinutes : 10;
                    Log($"heartbeat: turned ON (every {plan.Report.HeartbeatMinutes}m)");
                    sink.Toast(new ToastMessage($"heartbeat ON (every {plan.Report.HeartbeatMinutes}m)", LogSeverity.Info));
                }
                try { plan.Save(); } catch (Exception ex) { Log($"heartbeat: failed to persist plan JSON: {ex.Message}"); }
                DeleteControlFile();
                break;
            }
        }
        if (inSession && action is ControlAction.RetryStage or ControlAction.Rollback or ControlAction.PauseAfterStage or ControlAction.Goto or ControlAction.AbortNow)
            Log($"control: {action} received mid-session — re-run after session ends for it to take effect");
        return action;
    }

    private ControlAction? ReadControlFile()
    {
        try
        {
            if (!File.Exists(_controlPath)) return null;
            var writeTime = File.GetLastWriteTimeUtc(_controlPath);
            if (_lastControlWrite == writeTime) return null; // already processed this version
            _lastControlWrite = writeTime;
            var text = File.ReadAllText(_controlPath);
            var parsed = ControlFile.Parse(text);
            var action = parsed.Action;
            if (action != null && parsed.Confirmed && parsed.IntentId != null)
                Log($"control confirmed [intent={parsed.IntentId}]");
            if (action == ControlAction.Goto && parsed.StageId != null)
                _gotoStageId = parsed.StageId;
            if (action == ControlAction.Rollback && parsed.Force)
                _rollbackForce = true;
            if (action == ControlAction.ToggleHeartbeat && parsed.Value != null)
                _heartbeatToggleValue = parsed.Value;
            return action;
        }
        // A malformed/racing control.json is operator input, not an engine fault — ignore this poll
        // and let the next one pick up a well-formed file rather than crash the loop.
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private void DeleteControlFile()
    {
        try { if (File.Exists(_controlPath)) File.Delete(_controlPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _lastControlWrite = null;
    }

    private void RecoverFromCrash()
    {
        var recovered = false;

        // Existing state.json-based path (authoritative for transient control fields the log
        // doesn't yet carry — additive discipline).
        if (state.Status is RunStatus.Running or RunStatus.VerifyingGates or RunStatus.Backoff)
        {
            var last = state.History.LastOrDefault();
            if (last != null && last.EndedUtc == null)
            {
                last.EndedUtc = DateTime.UtcNow;
                last.Outcome = SessionOutcome.Interrupted;
                QueueResume(last, "conductor crashed or was killed mid-session");
                Log($"recovered: session #{last.Number} was interrupted — will resume its agent session");
                recovered = true;
            }
            state.Status = RunStatus.Idle;
            Save();
        }

        // B2.3: event-log-based recovery — the event log may know about a crash that state.json
        // missed (double-hard crash between save and session finish, or a torn state.json write).
        if (!recovered && state.PendingResume == null)
        {
            var eventsPath = Path.Combine(plan.StateDir, "events.jsonl");
            if (File.Exists(eventsPath))
            {
                var evts = EventLog.ReadAll(eventsPath);
                var interrupted = RunStateProjection.FindInterruptedSession(evts);
                if (interrupted != null)
                {
                    var rec = state.History.FirstOrDefault(h => h.Number == interrupted.Number);
                    if (rec != null)
                    {
                        if (rec.EndedUtc == null) rec.EndedUtc = DateTime.UtcNow;
                        rec.Outcome = SessionOutcome.Interrupted;
                        QueueResume(rec, "event log shows interrupted session — recovering");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(interrupted.AgentSessionId))
                        {
                            Log($"recovered from event log: session #{interrupted.Number} has no AgentSessionId — marking needs-attention (cannot resume without a session id)");
                            state.Status = RunStatus.NeedsHuman;
                            state.AttentionReason = $"Orphaned session #{interrupted.Number} in events.jsonl has no AgentSessionId — manual review needed.";
                            Save();
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
                            state.History.Add(rec);
                            QueueResume(rec, "event log shows interrupted session — recovering from orphaned SessionStarted");
                        }
                    }
                    if (state.Status != RunStatus.NeedsHuman)
                    {
                        Log($"recovered from event log: session #{interrupted.Number} was interrupted — will resume");
                        state.Status = RunStatus.Idle;
                        Save();
                    }
                }

                // B9.2: rebuild decomposed-checkpoints set from TaskAdded events so we don't
                // re-decompose after a crash.
                foreach (var evt in evts)
                {
                    if (evt is TaskAdded ta)
                        _decomposedCheckpoints.Add(ta.CheckpointId);
                }
            }
        }
    }

    private void WarnOnBranchPattern()
    {
        if (string.IsNullOrWhiteSpace(plan.BranchPattern)) return;
        var branch = Git.Branch(plan.Repo);
        if (!Regex.IsMatch(branch, plan.BranchPattern, RegexOptions.None, ProgressConventions.RegexTimeout))
            Log($"⚠ branch '{branch}' does not match plan branchPattern '{plan.BranchPattern}' — check before letting sessions commit");
    }

    private string BuildPrompt(SessionKind kind, StageConfig stage, int sessionNumber, int attempt, int maxAttempts)
    {
        var isReview = stage.Kind.Equals("review", StringComparison.OrdinalIgnoreCase);
        var reviewPath = isReview ? Path.Combine(plan.StateDir, "reviews", $"{stage.Id}.md") : "";
        return kind switch
        {
            SessionKind.Resume => _prompts.Resume(stage, sessionNumber, attempt, maxAttempts, state.PendingResume!),
            SessionKind.Audit => _prompts.Audit(stage, sessionNumber, state.PendingAudit!, state.CurrentStageStartHead ?? "HEAD~1"),
            SessionKind.Fix => _prompts.Fix(stage, sessionNumber, attempt, maxAttempts, state.PendingFix!),
            _ => isReview
                ? _prompts.Review(stage, sessionNumber, attempt, maxAttempts, reviewPath)
                : _prompts.Deliver(stage, sessionNumber, attempt, maxAttempts),
        };
    }

    private static PromptBuilder BuildPromptBuilder(PlanConfig plan)
    {
        var registry = new PersonaRegistry(plan);
        var lessons = new LessonsManager(plan.StateDir);
        return new PromptBuilder(plan, registry, lessons);
    }

    private static string ExtractSessionResult(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return "";
        var idx = resultText.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        var s = idx >= 0 ? resultText[idx..] : resultText;
        return Trunc(s.Trim(), 700);
    }

    private string LastRawTail(string rawLogPath)
    {
        // Best-effort diagnostics tail: a missing/locked raw log just yields no tail, never a crash.
        try { return GateRunner.TailOf(File.ReadAllText(rawLogPath), 10); }
        catch (IOException) { return ""; }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------------------------------------------------------------- snapshots, log, lock

    private void PushSessionSnapshot(AgentSession agent, SessionRecord rec, StageConfig stage, int attempt, int maxAttempts, TrackerSnapshot track)
        => sink.Snapshot(BaseSnapshot(track) with
        {
            SessionNumber = rec.Number,
            SessionKind = rec.Kind.ToString(),
            Attempt = attempt,
            MaxAttempts = maxAttempts,
            ResumeCount = rec.ResumeCount,
            SessionCostUsd = agent.CostUsd ?? 0m,
            SessionTokensInput = agent.TokensInput ?? 0,
            SessionTokensOutput = agent.TokensOutput ?? 0,
            SessionTokensReasoning = agent.TokensReasoning ?? 0,
            SessionElapsed = DateTime.UtcNow - agent.StartedUtc,
            LastActivityAgoSec = (DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds,
            AgentActive = true,
        });

    private void PushIdleSnapshot()
    {
        TrackerSnapshot track;
        // Display-only read on the idle hot path: a transient tracker read failure falls back to an
        // empty snapshot without log spam — the authoritative read in the main loop (Run) is what
        // surfaces a genuinely broken tracker via NeedsHuman.
        try { track = _progress.Read(plan, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { track = new TrackerSnapshot(); }
        sink.Snapshot(BaseSnapshot(track));
    }

    private DashboardSnapshot BaseSnapshot(TrackerSnapshot track)
        => SnapshotBuilder.Build(plan, state, track,
            _lastGates != null ? GateRunner.Summary(_lastGates) : "", _backoffUntil);

    private void Save() => state.Save(statePath);

    /// <summary>Emit the terminal event for a session from its finalized record (single choke point:
    /// the record's Outcome is set on every RunSession exit path). Also emits CheckpointConfirmed for
    /// each row that flipped DONE in a gate-green, committed session (an Advanced outcome).</summary>
    private void EmitSessionFinished(SessionRecord rec)
    {
        var sid = rec.Number.ToString();
        events.Emit(new SessionFinished
        {
            SessionId = sid,
            Number = rec.Number,
            StageId = rec.Stage,
            Outcome = rec.Outcome?.ToString() ?? "Unknown",
            NewCommits = rec.NewCommits,
            NewlyDone = rec.NewlyDone,
            CostUsd = rec.CostUsd,
            TokensInput = rec.TokensInput,
            TokensOutput = rec.TokensOutput,
            TokensReasoning = rec.TokensReasoning,
            TokensCacheRead = rec.TokensCacheRead,
        });
        if (rec.Outcome == SessionOutcome.Advanced)
            foreach (var id in rec.NewlyDone)
                events.Emit(new CheckpointConfirmed { SessionId = sid, CheckpointId = id, StageId = rec.Stage });
    }

    /// <summary>Emit one GateFinished per result — the trust-model verification surface, from one source.</summary>
    private void EmitGates(IReadOnlyList<GateResult> gates, string scope, string? sessionId = null)
    {
        foreach (var g in gates)
            events.Emit(new GateFinished
            {
                SessionId = sessionId,
                Name = g.Name,
                Passed = g.Passed,
                Skipped = g.Skipped,
                Optional = g.Optional,
                ExitCode = g.ExitCode,
                DurationMs = (long)g.Duration.TotalMilliseconds,
                Scope = scope,
            });
    }

    /// <summary>Keep a small ring buffer of recent agent activity for the AFK live-activity report.</summary>
    private void TrackActivity(AgentEvent ev)
    {
        if (ev.Kind is not ("tool" or "text" or "result" or "thinking")) return;
        _activity.Add((ev.Kind, ev.Text, ev.Utc));
        if (_activity.Count > 60) _activity.RemoveRange(0, 20);
    }

    /// <summary>Markdown of the latest agent activity (tool calls + thinking) for REPORT.md.</summary>
    private string BuildActivitySection(SessionRecord rec, AgentSession agent)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"_Session #{rec.Number} ({rec.Kind}) · running {(DateTime.UtcNow - agent.StartedUtc).TotalMinutes:0}m · " +
                      $"last output {(DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds:0}s ago" +
                      (agent.CostUsd is { } c ? $" · ${c:0.0000}" : "") + "_");
        sb.AppendLine();
        var think = _activity.Where(a => a.Kind == "thinking").TakeLast(3).ToList();
        if (think.Count > 0)
        {
            sb.AppendLine("**Thinking:**");
            foreach (var t in think) sb.AppendLine($"> {Trunc(t.Text.Replace("\n", " "), 300)}");
            sb.AppendLine();
        }
        var acts = _activity.Where(a => a.Kind != "thinking").TakeLast(10).ToList();
        if (acts.Count > 0)
        {
            sb.AppendLine("**Recent actions:**");
            foreach (var a in acts)
            {
                var glyph = a.Kind switch { "tool" => "»", "result" => "◆", _ => "·" };
                sb.AppendLine($"- `{a.Utc.ToLocalTime():HH:mm:ss}` {glyph} {Trunc(a.Text.Replace("\n", " "), 160)}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private void HeartbeatReport(SessionRecord rec, StageConfig stage, AgentSession agent, TrackerSnapshot track)
    {
        try
        {
            var cp = track.ForStage(stage.Id).FirstOrDefault(c => !c.IsDone)?.Id ?? stage.Id;
            var msg = $"chore(conductor): s{rec.Number} {stage.Id} working ▸{cp} @ {DateTime.Now:HH:mm}";
            Reporter.WriteAndPublish(plan, state, track, _lastGates, Log, BuildActivitySection(rec, agent), msg);
        }
        catch (Exception ex) { Log($"heartbeat report failed: {ex.Message}"); }
    }

    private void SaveAndReport()
    {
        Save();
        TrackerSnapshot track;
        // Report render tolerates a transient tracker read failure (→ empty snapshot); the main loop's
        // authoritative read is what escalates a broken tracker to the human.
        try { track = _progress.Read(plan, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { track = new TrackerSnapshot(); }
        Reporter.WriteAndPublish(plan, state, track, _lastGates, Log);
        PushIdleSnapshot();
    }

    private void Log(string line)
    {
        Log(line, null);
    }

    private void Log(string line, string? outcome)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        // Legacy plain log (.conductor/conductor.log) kept additively for humans/back-compat; the
        // structured Serilog sink under .conductor/logs/ is the authoritative record.
        try { File.AppendAllText(_logPath, stamped + Environment.NewLine); }
        catch (IOException) { /* plain log is best-effort; the structured log below still records it */ }
        catch (UnauthorizedAccessException) { /* ditto — never let narration I/O break the run */ }
        var prev = _outcome;
        _outcome = outcome;
        try
        {
            using (BeginCorrelationScope())
                logger.LogInformation("{ConductorMessage}", line);
        }
        finally { _outcome = prev; }
        sink.Log(stamped);
    }

    /// <summary>Pushes the current runId/sessionId/stage/gate as a logging scope so every structured
    /// line is correlated; absent values are omitted (they render empty in the sink template).</summary>
    private IDisposable? BeginCorrelationScope()
    {
        var scope = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(state.RunId)) scope["runId"] = state.RunId;
        if (state.SessionCounter > 0) scope["sessionId"] = state.SessionCounter.ToString();
        if (!string.IsNullOrEmpty(state.CurrentStage)) scope["stage"] = state.CurrentStage;
        if (_curGate != null) scope["gate"] = _curGate;
        if (_outcome != null) scope["outcome"] = _outcome;
        return scope.Count > 0 ? logger.BeginScope(scope) : null;
    }

    private void Notify(string message)
    {
        // B6: push to Telegram (fire-and-forget — the hosted service owns its own queue).
        _ = telegram.PushAsync(message);
        // B6.4: fire webhook notifications (generic/Discord/Slack).
        webhooks.FireAsync(message);

        var n = plan.Notify;
        if (n == null || string.IsNullOrWhiteSpace(n.Command)) return;
        try
        {
            var args = n.Args.Select(a => a.Replace("{message}", message));
            ProcessRunner.Run(n.Command, args, plan.Repo, TimeSpan.FromMinutes(1));
        }
        catch (Exception ex) { Log($"notify failed: {ex.Message}"); }
    }

    private bool AcquireLock()
    {
        try
        {
            if (File.Exists(_lockPath))
            {
                var pidText = File.ReadAllText(_lockPath).Trim();
                if (int.TryParse(pidText, out var pid))
                {
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
                        if (!p.HasExited)
                        {
                            sink.Log($"another conductor (pid {pid}) is already running this plan — exiting");
                            return false;
                        }
                    }
                    catch (ArgumentException) { /* stale lock — process gone */ }
                }
            }
            File.WriteAllText(_lockPath, Environment.ProcessId.ToString());
            return true;
        }
        catch (Exception ex)
        {
            sink.Log($"could not acquire lock: {ex.Message}");
            return false;
        }
    }

    private void ReleaseLock()
    {
        // Best-effort unlock on shutdown: if the lock file is already gone or transiently locked, a
        // stale entry is reclaimed on the next start by the pid-liveness check in AcquireLock.
        try { if (File.Exists(_lockPath)) File.Delete(_lockPath); }
        catch (IOException) { /* reclaimed next start via pid-liveness */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    private void EnsureStateDirGitignore()
    {
        var gi = Path.Combine(plan.StateDir, ".gitignore");
        if (!File.Exists(gi))
            File.WriteAllText(gi, "*\n!.gitignore\n!REPORT.md\n");
    }

    // ---------------------------------------------------------------- B8 brain layer

    /// <summary>B8.1: Reflection step — after each session ends, distil "what was hard" from
    /// the SESSION-RESULT text into a bounded, rotating lessons file for future sessions.</summary>
    private void ReflectionStep(SessionRecord rec)
    {
        if (string.IsNullOrWhiteSpace(rec.ResultSummary)) return;

        // Extract the struggle note after "SESSION-RESULT:" — this is the "what was hard" signal
        // the next session's prompt should receive.
        var text = rec.ResultSummary;
        var idx = text.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        // Take the post-SESSION-RESULT text, strip the marker
        var difficulty = text[(idx + "SESSION-RESULT:".Length)..].Trim();
        if (difficulty.Length == 0) return;
        if (difficulty.Length > 500)
            difficulty = difficulty[..497] + "…";

        _lessons.Append(rec.Stage, rec.Number, difficulty);
    }

    /// <summary>B8.4: Parse audit handover doc for deferred/weak/bugs-not-fixed bullets and track
    /// them in <c>.conductor/followups.md</c>. Only appends entries not already tracked (by title
    /// match), avoiding duplicates across multiple phases.</summary>
    private void ParseAuditFollowups(string stageId)
    {
        var handoverPath = Path.Combine(plan.StateDir, "handovers", $"{stageId}.md");
        if (!File.Exists(handoverPath)) return;

        var followupsPath = Path.Combine(plan.StateDir, "followups.md");
        var existing = File.Exists(followupsPath) ? File.ReadAllText(followupsPath, Encoding.UTF8) : "";

        var bullets = new List<string>();
        try
        {
            var content = File.ReadAllText(handoverPath, Encoding.UTF8);
            var lines = content.Split('\n');
            var inSection = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                // Match sections: "Weak / deferred", "Bugs not fixed", "Deferred / unfixed"
                if (t.StartsWith("## ", StringComparison.Ordinal) || t.StartsWith("### ", StringComparison.Ordinal))
                {
                    var heading = t.ToLowerInvariant();
                    inSection = heading.Contains("weak", StringComparison.Ordinal) || heading.Contains("deferred", StringComparison.Ordinal) ||
                                heading.Contains("bugs not fixed", StringComparison.Ordinal) || heading.Contains("unfixed", StringComparison.Ordinal) ||
                                heading.Contains("concrete follow", StringComparison.Ordinal);
                }
                else if (inSection && (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)
                         || (t.StartsWith("### ", StringComparison.Ordinal) && t.Contains("D-"))))
                {
                    var bullet = t.TrimStart('-', '*', ' ').Trim();
                    if (bullet.Length > 0) bullets.Add(bullet);
                }
            }
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (bullets.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        var prevExists = existing.Length > 0;
        if (!prevExists)
        {
            sb.AppendLine("# Conductor followups (auto-tracked from audit handovers)");
            sb.AppendLine();
            sb.AppendLine("| Id | Item | Stage | Status |");
            sb.AppendLine("|---|---|---|---|");
        }
        else
        {
            sb.Append(existing.TrimEnd());
        }

        var added = 0;
        var sid = stageId;
        foreach (var bullet in bullets)
        {
            var title = bullet.Length > 80 ? bullet[..77] + "…" : bullet;
            if (existing.Contains(title, StringComparison.OrdinalIgnoreCase))
                continue; // already tracked

            if (!prevExists && added == 0)
                sb.AppendLine();
            sb.AppendLine($"| FU-{sid}-{added + 1:00} | {title} | {sid} | OPEN |");
            added++;
        }

        if (added > 0)
        {
            File.WriteAllText(followupsPath, sb.ToString().TrimEnd() + Environment.NewLine, Encoding.UTF8);
            Log($"followups: {added} new item(s) from {stageId} audit tracked in followups.md");
        }
    }

    // ---------------------------------------------------------------- B9.4: soft-break + MCP journal

    /// <summary>B9.4: Check whether the live agent token count has crossed the soft-break threshold
    /// and if so, emit a <c>SoftBreakRequested</c> event and write a nudge signal file. Only fires
    /// once per session — one nudge is enough.</summary>
    private void CheckSoftBreak(AgentSession agent, TrackerSnapshot preTrack)
    {
        if (_softBreakSignalled) return;
        var threshold = ComputeSoftThreshold();
        if (threshold is not { } thresh) return;

        var liveTokens = (agent.TokensInput ?? 0) + (agent.TokensOutput ?? 0)
            + (agent.TokensReasoning ?? 0) + (agent.TokensCacheRead ?? 0);
        if (liveTokens < thresh) return;

        _softBreakSignalled = true;
        var activeCp = preTrack.Checkpoints.FirstOrDefault(c => !c.IsDone)?.Id;
        var maxTokens = plan.Limits.MaxSessionTokens!.Value; // guarded: ComputeSoftThreshold returns null unless MaxSessionTokens is set
        var signalFile = Path.Combine(plan.StateDir, "soft-break");
        File.WriteAllText(signalFile, $"finish-subtask-and-handoff:{DateTime.UtcNow:o}");

        events.Emit(new SoftBreakRequested
        {
            LiveTokens = liveTokens,
            TokenBudget = maxTokens,
            CurrentCheckpointId = activeCp,
        });
        Log($"soft-break: {liveTokens / 1000.0:0.#}k tokens ≥ {thresh / 1000.0:0.#}k threshold — nudge written, session should hand off cleanly");
        sink.Log($"[soft-break] {liveTokens / 1000.0:0.#}k/{maxTokens / 1000.0:0.#}k tokens — agent has been nudged to hand off");
    }

    /// <summary>Compute the absolute token threshold for the soft-break, or null if soft-break is
    /// disabled (no <c>MaxSessionTokens</c> configured).</summary>
    private long? ComputeSoftThreshold()
    {
        if (plan.Limits.MaxSessionTokens is not { } max) return null;
        var ratio = plan.Limits.SoftBreakRatio is { } r and > 0 and <= 1.0
            ? r : 0.8;
        return (long)(max * ratio);
    }

    /// <summary>B9.4: remove the soft-break signal file if it exists from a prior session.</summary>
    private void CleanSoftBreakSignal()
    {
        var signalFile = Path.Combine(plan.StateDir, "soft-break");
        try { if (File.Exists(signalFile)) File.Delete(signalFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>B9.4: fold any MCP journal entries into the main event log. The McpTaskServer writes
    /// task updates to a side journal to avoid concurrent-write races; after the agent exits the
    /// journal is safe to merge. Merged entries are deleted so they aren't duplicated next time.</summary>
    private void FoldMcpJournal()
    {
        var journalPath = Path.Combine(plan.StateDir, "mcp-journal.jsonl");
        if (!File.Exists(journalPath)) return;
        try
        {
            var journalEvents = EventLog.ReadAll(journalPath);
            if (journalEvents.Count == 0) return;
            foreach (var evt in journalEvents)
                events.Emit(evt);
            File.Delete(journalPath);
            Log($"MCP journal folded: {journalEvents.Count} event(s) merged into event log");
        }
        catch (Exception ex)
        {
            Log($"MCP journal fold failed: {ex.Message}");
        }
    }

    /// <summary>B9.4: Build a human-readable hint about which sub-task the next session should
    /// resume from. Reads the task graph from the event log, finds the active checkpoint, and
    /// returns the first pending sub-task's title.</summary>
    private string? BuildRolloverResumeHint(TrackerSnapshot preTrack)
    {
        var eventsPath = Path.Combine(plan.StateDir, "events.jsonl");
        if (!File.Exists(eventsPath)) return null;
        try
        {
            var allEvents = EventLog.ReadAll(eventsPath);
            var taskGraph = new TaskGraph();
            taskGraph.Fold(allEvents);
            var activeCp = preTrack.ForStage(state.CurrentStage ?? "")
                .FirstOrDefault(c => !c.IsDone);
            if (activeCp == null) return null;
            var next = taskGraph.CurrentTask(activeCp.Id);
            return next != null
                ? $"next sub-task: {next.Title} [{next.Status}]"
                : null;
        }
        catch (Exception ex)
        {
            Log($"task-graph resume hint failed: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------- B12.4: fix-lanes

    /// <summary>
    /// B12.4: Read OPEN followup entries owned by the given stage from
    /// <c>.conductor/followups.md</c> and run each as a Tier B mutating fix-lane behind a
    /// merge gate. Closed followups are updated in-place with the resulting commit ref.
    /// </summary>
    private void RunFollowupFixLanes(string stageId)
    {
        var followupsPath = Path.Combine(plan.StateDir, "followups.md");
        if (!File.Exists(followupsPath)) return;

        var entries = FollowupParser.ReadOpenForStage(followupsPath, stageId);
        if (entries.Count == 0) return;

        // Resolve the plan's default agent (per-lane overrides aren't used for fix-lanes yet).
        var agent = plan.Agent;
        Log($"fix-lanes: {entries.Count} OPEN followup(s) owned by stage {stageId}");

        foreach (var entry in entries)
        {
            var lane = FollowupEntryToMutatingLane(entry);
            Log($"fix-lane '{entry.Id}' starting — {entry.Item}");

#pragma warning disable MA0045 // sync boundary: orchestrator loop is sync, lane runner uses ConfigureAwait(false)
            // Run the mutating lane synchronously (the orchestrator loop is sync). The lane
            // runner uses ConfigureAwait(false) internally so GetAwaiter().GetResult() is safe.
            MutatingLaneResult result;
            try
            {
                result = Task.Run(() => MutatingLaneRunner.RunAsync(
                    plan, lane, agent, stageId, events, Log, CancellationToken.None))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log($"fix-lane '{entry.Id}' threw: {ex.Message}");
                continue;
            }

            if (result.Merged || (result.IsSuccess && !result.AgentCommitted))
            {
                var commitRef = Git.Head(plan.Repo)[..Math.Min(7, Git.Head(plan.Repo).Length)];
                if (FollowupParser.UpdateStatus(followupsPath, entry.Id, "CLOSED", $"b{entry.Id}"))
                    Log($"fix-lane '{entry.Id}' CLOSED — {entry.Item} ({commitRef})");
#pragma warning restore MA0045
                else
                    Log($"fix-lane '{entry.Id}' done but status update failed in followups.md");
            }
            else
            {
                Log($"fix-lane '{entry.Id}' FAILED — merge gate rejected: {result.Error ?? "unknown"}");
            }
        }
    }

    private static MutatingLaneConfig FollowupEntryToMutatingLane(FollowupEntry entry)
    {
        var prompt = $"Fix the followup: {entry.Item}\n\n";
        if (!string.IsNullOrWhiteSpace(entry.Detail))
            prompt += $"Detail: {entry.Detail}\n\n";
        prompt += "Read .conductor/followups.md for full context. " +
                  "Commit your fix with a conventional commit message (e.g. 'fix: …').";

        return new MutatingLaneConfig
        {
            Id = $"fix-{entry.Id.ToLowerInvariant()}",
            Kind = "fix",
            Name = $"Fix: {entry.Item}",
            Prompt = prompt,
            TimeoutMinutes = 30,
        };
    }

    // ---------------------------------------------------------------- B12.1: analysis lanes

    /// <summary>B12.2: Enqueue read-only analysis lanes for the current stage into the bounded
    /// worker pool. The pool respects <see cref="LimitsConfig.MaxConcurrentLanes"/> and emits
    /// <see cref="LaneStarted"/> / <see cref="LaneFinished"/> lifecycle events.
    /// Each lane runs in a scratch temp directory so it can never write the working tree.</summary>
    private void StartAnalysisLanes(StageConfig stage, string? handoff, CancellationToken ct)
    {
        if (plan.AnalysisLanes.Count == 0) return;

        var triggered = plan.AnalysisLanes
            .Where(l => l.Enabled && (l.StageTrigger == null ||
                l.StageTrigger.Equals(stage.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (triggered.Count == 0) return;

        _lanePool ??= new LaneWorkerPool(plan.Limits.MaxConcurrentLanes, events, Log);

        var gitSummary = GitView.Summary(plan.Repo);
        var resolvedAgent = plan.ResolveAgent(stage);

        foreach (var lane in triggered)
        {
            var capturedLane = lane;
            _lanePool.Enqueue(new LaneWorkItem(
                lane.Id, lane.Kind, stage.Id,
                ct2 => LaneRunner.RunAsync(capturedLane, resolvedAgent,
                    plan.Name, stage.Id, stage.Title, plan.StateDir,
                    handoff, gitSummary, ct2)), ct);
        }
    }

    /// <summary>B12.2: Drain any lanes that completed since the last poll so the session prompt
    /// can optionally be updated with fresh analysis results.</summary>
    private void PollLaneCompletion()
    {
        if (_lanePool == null || _lanePool.CompletedCount == 0) return;

        var results = _lanePool.DrainCompleted();
        foreach (var result in results)
        {
            if (result.IsSuccess)
                Log($"analysis lane '{result.LaneId}' completed ({result.ElapsedMs}ms)" +
                    (result.ArtifactPath != null ? $" → {Path.GetFileName(result.ArtifactPath)}" : ""));
            else
                Log($"analysis lane '{result.LaneId}' failed: {result.Error ?? "unknown error"}");
        }
    }

    /// <summary>B12.2: After the session ends, wait briefly for any remaining lanes, then
    /// collect their artifacts. The pool already emitted lifecycle events; we just log a summary.</summary>
    private void CollectLaneArtifacts(string stageId)
    {
        if (_lanePool == null || (_lanePool.ActiveCount == 0 && _lanePool.CompletedCount == 0)) return;

#pragma warning disable MA0045 // sync boundary: CollectLaneArtifacts is called from sync stage-confirm flow
        // Wait for remaining lanes with a short timeout so we don't block the orchestrator.
        // Use GetAwaiter().GetResult() (same pattern as the pre-pool .Wait() call).
        var remaining = _lanePool.WaitAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
            .GetAwaiter().GetResult();
#pragma warning restore MA0045

        var successCount = remaining.Count(r => r.IsSuccess);
        var failCount = remaining.Count - successCount;
        if (remaining.Count > 0)
            Log($"analysis lanes collected: {successCount} succeeded, {failCount} failed for stage {stageId}");
    }
}
