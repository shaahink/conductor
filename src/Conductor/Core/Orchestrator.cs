using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core;

public sealed record RunOptions(bool DryRun, bool Once, int MaxSessions);

/// <summary>
/// The session cycle, mechanized:
///   pick stage from tracker → spawn agent session (deliver / fix / resume) → watchdog it →
///   independently verify (gates + git + tracker diff) → record, report, decide next.
/// Every transition is persisted, so killing conductor at any point is recoverable.
/// </summary>
public sealed class Orchestrator(PlanConfig plan, RunState state, string statePath, IProgressSink sink, RunOptions opts)
{
    private static readonly Regex LimitRx = new(
        @"usage limit|rate.?limit|overloaded|quota|out of credit|insufficient credit|credit balance|429|too many requests|5-hour|weekly limit",
        RegexOptions.IgnoreCase);

    private readonly PromptBuilder _prompts = new(plan);
    private IReadOnlyList<GateResult>? _lastGates;
    private bool _pendingSkip;
    private bool _pausePending;
    private readonly string _lockPath = Path.Combine(plan.StateDir, "conductor.lock");
    private readonly string _controlPath = Path.Combine(plan.StateDir, "control.json");
    private readonly string _logPath = Path.Combine(plan.StateDir, "conductor.log");
    private DateTime? _backoffUntil;

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
            WarnOnBranchPattern();

            var sessionsThisRun = 0;
            while (!ct.IsCancellationRequested)
            {
                HandleControl();
                if (state.Status == RunStatus.Aborted) { SaveAndReport(); return 2; }
                if (state.Status is RunStatus.Paused or RunStatus.NeedsHuman)
                {
                    PushIdleSnapshot();
                    Thread.Sleep(800);
                    continue;
                }

                if (_backoffUntil is { } until)
                {
                    if (DateTime.UtcNow < until) { PushIdleSnapshot(); Thread.Sleep(1000); continue; }
                    _backoffUntil = null;
                    state.Status = RunStatus.Idle;
                    Log("backoff over — resuming");
                }

                var track = TrackerParser.ParseFile(plan.TrackerPath);
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
                    Save();
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
                    sink.Log($"--- DRY RUN: would start session #{state.SessionCounter + 1} ({kind}, stage {stage.Id}) with prompt: ---");
                    sink.Log(prompt);
                    return 0;
                }

                RunSession(stage, track, ct);
                sessionsThisRun++;

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
            // Ctrl+C / external cancel
            Log("cancelled — state saved; run `conductor run` again to resume");
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
        var prompt = kind switch
        {
            SessionKind.Resume => _prompts.Resume(stage, state.SessionCounter, attempt, maxAttempts, pendingResume!),
            SessionKind.Audit => _prompts.Audit(stage, state.SessionCounter, pendingAudit!, state.CurrentStageStartHead ?? "HEAD~1"),
            SessionKind.Fix => _prompts.Fix(stage, state.SessionCounter, attempt, maxAttempts, pendingFix!),
            _ => _prompts.Deliver(stage, state.SessionCounter, attempt, maxAttempts),
        };

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
        var rawLog = Path.Combine(logsDir, $"session-{rec.Number:000}.jsonl");

        var startHead = Git.Head(plan.Repo);
        state.History.Add(rec);
        state.Status = RunStatus.Running;
        Save();
        Log($"session #{rec.Number} start — {kind} {stage.Id} attempt {attempt}/{maxAttempts}" +
            (kind == SessionKind.Resume ? $" (resume #{rec.ResumeCount} of {rec.ClaudeSessionId[..8]})" : ""));

        bool stalled = false, timedOut = false, killedByUser = false;
        GateRunner.RunHook(plan, plan.Setup, "setup", Log, ct);
        using (var agent = AgentSession.Start(plan.Agent, plan.Repo, prompt, rec.ClaudeSessionId,
                   kind == SessionKind.Resume ? rec.ClaudeSessionId : null, rawLog))
        {
            while (!agent.HasExited)
            {
                while (agent.TryDequeue(out var ev)) sink.AgentEvent(ev);
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
                Thread.Sleep(400);
            }
            var exit = agent.WaitForExitCode();
            while (agent.TryDequeue(out var ev)) sink.AgentEvent(ev);
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
            if ((agent.ResultIsError || exit != 0) && LimitRx.IsMatch(limitEvidence))
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

            EvaluateSession(rec, stage, preTrack, startHead, stalled, timedOut, killedByUser,
                agentErrored: agent.ResultIsError || (exit != 0 && !stalled && !timedOut && !killedByUser), ct);
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
            if (rec.ResumeCount < plan.Limits.MaxResumesPerSession)
            {
                QueueResume(rec, stalled ? "session stalled (no output)" : "session hit the hard timeout");
                Log($"will resume agent session (resume {rec.ResumeCount + 1}/{plan.Limits.MaxResumesPerSession})");
            }
            else
            {
                var verdict = ConsultAdvisor(rec, stage, TrackerParser.ParseFile(plan.TrackerPath), "resume budget exhausted after stall/timeout");
                ApplyVerdict(verdict, rec, stage, defaultAction: "retry");
            }
            state.Status = RunStatus.Idle;
            SaveAndReport();
            return;
        }

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

        var postTrack = TrackerParser.ParseFile(plan.TrackerPath);
        rec.NewCommits = Git.CommitsSince(plan.Repo, startHead);
        rec.NewlyDone = postTrack.Checkpoints
            .Where(c => c.IsDone && !(preTrack.ById(c.Id)?.IsDone ?? false))
            .Select(c => c.Id).ToList();
        var newlyBlocked = postTrack.Checkpoints
            .Where(c => c.IsBlocked && !(preTrack.ById(c.Id)?.IsBlocked ?? false))
            .Select(c => c.Id).ToList();
        var gatesGreen = GateRunner.AllRequiredPassed(gates);
        var dirty = Git.IsDirty(plan.Repo);

        Log($"verdict inputs: gates {(gatesGreen ? "green" : "RED")} · commits {rec.NewCommits.Count} · newly DONE [{string.Join(",", rec.NewlyDone)}] · dirty {(dirty ? "YES" : "no")}");

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
            Log($"session #{rec.Number} {rec.Outcome} — {(rec.NewlyDone.Count > 0 ? string.Join(", ", rec.NewlyDone) + " done" : "no checkpoint flipped yet")}");

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
            Log($"session #{rec.Number} {rec.Outcome} — queuing fix session (attempt {state.AttemptsThisStage}/{MaxAttempts(stage)})");
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
            Log($"phase gate {pg.StageId} finished in {sw.Elapsed.TotalSeconds:0}s — {(green ? "GREEN" : "RED")}: {GateRunner.Summary(gates)}");
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
        if (!state.ConfirmedStages.Contains(id)) state.ConfirmedStages.Add(id);
        state.PendingPhaseGate = null;
        state.PendingAudit = null;
        state.PendingFix = null;
        state.AttemptsThisStage = 0;
        state.Status = RunStatus.Idle;
        Log($"✓ phase {id} CONFIRMED (full battery green{(state.AuditedStages.Contains(id) ? " + audit" : "")}) — advancing");
        SaveAndReport();
    }

    private StageConfig CurrentStageConfig()
        => plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage) ?? plan.Stages[^1];

    // ---------------------------------------------------------------- decisions

    private IReadOnlyList<GateResult> RunGateBattery(CancellationToken ct, bool fastOnly = false)
    {
        GateRunner.RunHook(plan, plan.Setup, "setup", Log, ct);
        var gates = GateRunner.RunAll(plan, Log, ct, fastOnly, state.CurrentStage, sink.GateProgress);
        GateRunner.RunHook(plan, plan.Teardown, "teardown", Log, ct);
        return gates;
    }

    private StageConfig? SelectStage(TrackerSnapshot track)
        => plan.Stages.FirstOrDefault(s => !StageComplete(s.Id, track) && !state.SkippedStages.Contains(s.Id));

    private bool AllEffectivelyDone(TrackerSnapshot track)
        => plan.Stages.All(s => StageComplete(s.Id, track) || state.SkippedStages.Contains(s.Id));

    /// <summary>Under perPhase, a stage is "complete" only once its full battery (and audit) confirmed it —
    /// so a stage whose tracker rows read DONE but whose phase-gate is red is never advanced past.</summary>
    private bool StageComplete(string id, TrackerSnapshot track)
        => plan.PerPhaseGates ? state.ConfirmedStages.Contains(id) : track.StageDone(id);

    private int MaxAttempts(StageConfig stage) => Math.Max(1, stage.Sessions * plan.Limits.StageSlackFactor);

    private bool HandoffWantsHuman(TrackerSnapshot track)
        => track.HandoffBlock.Contains("HUMAN:", StringComparison.OrdinalIgnoreCase);

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
        SaveAndReport();
        Notify($"Conductor: plan {plan.Name} COMPLETE ({state.SessionCounter} sessions)");
    }

    private void NeedsHuman(string reason)
    {
        state.Status = RunStatus.NeedsHuman;
        state.AttentionReason = reason;
        Log($"🛑 NEEDS HUMAN: {reason}");
        SaveAndReport();
        Notify($"Conductor {plan.Name}: needs attention — {reason}");
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
                break;
            case ControlAction.StopAfterSession:
                state.StopAfterSession = true;
                break;
            case ControlAction.ResumeRun:
                if (state.Status is RunStatus.Paused or RunStatus.NeedsHuman)
                {
                    state.Status = RunStatus.Idle;
                    state.AttentionReason = null;
                    Save();
                    Log("resumed by user");
                }
                break;
            case ControlAction.SkipStage:
                if (inSession) _pendingSkip = true;
                else if (state.CurrentStage != null)
                {
                    var s = plan.Stages.FirstOrDefault(x => x.Id == state.CurrentStage);
                    if (s != null) SkipStage(s, "skipped by user control");
                }
                break;
            case ControlAction.AbortNow when !inSession:
                state.Status = RunStatus.Aborted;
                Save();
                break;
        }
        return action;
    }

    private ControlAction? ReadControlFile()
    {
        try
        {
            if (!File.Exists(_controlPath)) return null;
            var text = File.ReadAllText(_controlPath);
            File.Delete(_controlPath);
            using var doc = JsonDocument.Parse(text);
            var cmd = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() : null;
            return cmd?.ToLowerInvariant() switch
            {
                "pause" => ControlAction.PauseAfterSession,
                "resume" => ControlAction.ResumeRun,
                "abort" => ControlAction.AbortNow,
                "skip" => ControlAction.SkipStage,
                "kill" => ControlAction.KillSession,
                "stop-after" => ControlAction.StopAfterSession,
                _ => null,
            };
        }
        catch { return null; }
    }

    private void RecoverFromCrash()
    {
        if (state.Status is not (RunStatus.Running or RunStatus.VerifyingGates or RunStatus.Backoff)) return;
        var last = state.History.LastOrDefault();
        if (last != null && last.EndedUtc == null)
        {
            last.EndedUtc = DateTime.UtcNow;
            last.Outcome = SessionOutcome.Interrupted;
            QueueResume(last, "conductor crashed or was killed mid-session");
            Log($"recovered: session #{last.Number} was interrupted — will resume its agent session");
        }
        state.Status = RunStatus.Idle;
        Save();
    }

    private void WarnOnBranchPattern()
    {
        if (string.IsNullOrWhiteSpace(plan.BranchPattern)) return;
        var branch = Git.Branch(plan.Repo);
        if (!Regex.IsMatch(branch, plan.BranchPattern))
            Log($"⚠ branch '{branch}' does not match plan branchPattern '{plan.BranchPattern}' — check before letting sessions commit");
    }

    private string BuildPrompt(SessionKind kind, StageConfig stage, int sessionNumber, int attempt, int maxAttempts) => kind switch
    {
        SessionKind.Resume => _prompts.Resume(stage, sessionNumber, attempt, maxAttempts, state.PendingResume!),
        SessionKind.Audit => _prompts.Audit(stage, sessionNumber, state.PendingAudit!, state.CurrentStageStartHead ?? "HEAD~1"),
        SessionKind.Fix => _prompts.Fix(stage, sessionNumber, attempt, maxAttempts, state.PendingFix!),
        _ => _prompts.Deliver(stage, sessionNumber, attempt, maxAttempts),
    };

    private static string ExtractSessionResult(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return "";
        var idx = resultText.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        var s = idx >= 0 ? resultText[idx..] : resultText;
        return Trunc(s.Trim(), 700);
    }

    private string LastRawTail(string rawLogPath)
    {
        try { return GateRunner.TailOf(File.ReadAllText(rawLogPath), 10); } catch { return ""; }
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
            SessionElapsed = DateTime.UtcNow - agent.StartedUtc,
            LastActivityAgoSec = (DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds,
            AgentActive = true,
        });

    private void PushIdleSnapshot()
    {
        TrackerSnapshot track;
        try { track = TrackerParser.ParseFile(plan.TrackerPath); }
        catch { track = new TrackerSnapshot(); }
        sink.Snapshot(BaseSnapshot(track));
    }

    private DashboardSnapshot BaseSnapshot(TrackerSnapshot track)
        => SnapshotBuilder.Build(plan, state, track,
            _lastGates != null ? GateRunner.Summary(_lastGates) : "", _backoffUntil);

    private void Save() => state.Save(statePath);

    private void SaveAndReport()
    {
        Save();
        TrackerSnapshot track;
        try { track = TrackerParser.ParseFile(plan.TrackerPath); }
        catch { track = new TrackerSnapshot(); }
        Reporter.WriteAndPublish(plan, state, track, _lastGates, Log);
        PushIdleSnapshot();
    }

    private void Log(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        try { File.AppendAllText(_logPath, stamped + Environment.NewLine); } catch { }
        sink.Log(stamped);
    }

    private void Notify(string message)
    {
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
        try { if (File.Exists(_lockPath)) File.Delete(_lockPath); } catch { }
    }

    private void EnsureStateDirGitignore()
    {
        var gi = Path.Combine(plan.StateDir, ".gitignore");
        if (!File.Exists(gi))
            File.WriteAllText(gi, "*\n!.gitignore\n!REPORT.md\n");
    }
}
