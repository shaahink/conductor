using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Lanes;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core;

#pragma warning disable MA0045 // Session helper methods use sync file I/O by design — fast local writes, not hot-path
public sealed partial class Orchestrator
{
    private async Task RunSessionAsync(StageConfig stage, TrackerSnapshot preTrack, CancellationToken ct)
    {
        var pendingResume = state.PendingResume; state.PendingResume = null;
        var pendingAudit = state.PendingAudit; state.PendingAudit = null;
        var pendingFix = state.PendingFix; state.PendingFix = null;
        var pendingVerify = state.PendingVerify; state.PendingVerify = null;
        var kind = pendingResume != null ? SessionKind.Resume
            : pendingAudit != null ? SessionKind.Audit
            : pendingVerify != null ? SessionKind.Verify
            : pendingFix != null ? SessionKind.Fix : SessionKind.Deliver;

        state.SessionCounter++;
        var attempt = state.AttemptsThisStage + 1;
        var maxAttempts = MaxAttempts(stage);
        var isReview = stage.Kind.Equals("review", StringComparison.OrdinalIgnoreCase);
        var reviewDir = Path.Combine(plan.StateDir, "reviews");
        var reviewPath = isReview ? Path.Combine(reviewDir, $"{stage.Id}.md") : "";
        if (isReview)
        {
            Directory.CreateDirectory(reviewDir);
            var skeleton = $"# Self-review: {stage.Id} — {stage.Title}\n\n" +
                           $"_Generated {DateTime.UtcNow:u} by Conductor (B8.3) — pending agent review_\n";
            await File.WriteAllTextAsync(reviewPath, skeleton, ct).ConfigureAwait(false);
            Log($"review stage {stage.Id}: scaffolded review artifact at {reviewPath}");
        }

        var personaName = plan.ResolvePersona(stage);
        var activeCp = preTrack.ForStage(stage.Id).FirstOrDefault(c => !c.IsDone);
        if (kind == SessionKind.Deliver &&
            "deliver".Equals(personaName, StringComparison.OrdinalIgnoreCase) &&
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
                    Source = "deliver",
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
            SessionKind.Verify => _prompts.Verify(stage, state.SessionCounter, pendingVerify!),
            SessionKind.Fix => _prompts.Fix(stage, state.SessionCounter, attempt, maxAttempts, pendingFix!),
            _ => isReview
                ? _prompts.Review(stage, state.SessionCounter, attempt, maxAttempts, reviewPath)
                : _prompts.Deliver(stage, state.SessionCounter, attempt, maxAttempts),
        };
        var batterySection = _prompts.BatterySection(state);
        if (batterySection.Length > 0)
            prompt = prompt.TrimEnd() + "\n\n" + batterySection;

        if (kind == SessionKind.Deliver && state.ParallelAuditOutcome is { Completed: true, MaxSeverity: not AuditFindingSeverity.High } outcome)
        {
            var findings = Trunc(outcome.Findings, 3000);
            if (!string.IsNullOrWhiteSpace(findings))
            {
                prompt = prompt.TrimEnd() + $"\n\n## Parallel audit findings for stage {outcome.StageId}\n" +
                    $"The following audit findings were produced by a read-only audit lane running concurrently with the previous stage. " +
                    $"Address LOW and MEDIUM findings in this session if convenient.\n\n{findings}";
                state.ParallelAuditOutcome = null;
            }
        }

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
        await File.WriteAllTextAsync(Path.Combine(logsDir, $"session-{rec.Number:000}.prompt.md"), prompt, ct).ConfigureAwait(false);
        InstructionQueue.ConsumeAll(plan);
        var rawLog = Path.Combine(logsDir, $"session-{rec.Number:000}.jsonl");

        var startHead = Git.Head(plan.Repo);
        state.History.Add(rec);
        state.Status = RunStatus.Running;
        Save();
        _softBreakSignalled = false;
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
        await GateRunner.RunHookAsync(plan, plan.Setup, "setup", Log, ct).ConfigureAwait(false);
        var resolvedAgent = plan.ResolveAgent(stage);

        var mcpConfigPath = WireMcpServer(rec, stage);
        Dictionary<string, string>? extraEnv = null;
        if (mcpConfigPath != null)
        {
            extraEnv = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_CONFIG"] = mcpConfigPath };
            Log("I1: MCP task server wired (opencode config at " + mcpConfigPath + ")");
        }

        using (var agent = AgentSession.Start(resolvedAgent, plan.Repo, prompt, rec.ClaudeSessionId,
                   kind == SessionKind.Resume ? rec.ClaudeSessionId : null, rawLog, events, rec.Number.ToString(), extraEnv, supervisor: processSupervisor))
        {
            _activity.Clear();
            var lastHeartbeat = DateTime.UtcNow;
            var stallDetector = new StallDetector(
                TimeSpan.FromMinutes(plan.Limits.StallMinutes),
                TimeSpan.FromMinutes(plan.Limits.StallGraceMinutes));
            var stallGraceLogged = false;
            while (!agent.HasExited)
            {
                while (agent.TryDequeue(out var ev)) { sink.AgentEvent(ev); TrackActivity(ev, rec.Number); }
                Lanes.PollLaneCompletion();
                await Lanes.CheckParallelAuditCompletionAsync().ConfigureAwait(false);
                CheckSoftBreak(agent, preTrack);
                var ctl = await HandleControlAsync(inSession: true, ct: ct).ConfigureAwait(false);
                if (ctl == ControlAction.KillSession) { killedByUser = true; Log("kill requested"); agent.Kill(); }
                if (ctl == ControlAction.AbortNow) { killedByUser = true; state.Status = RunStatus.Aborted; Log("abort requested"); agent.Kill(); }
                if (ct.IsCancellationRequested) { agent.Kill(); }
                else
                {
                    if (_lastBgLivenessCheck == null || (DateTime.UtcNow - _lastBgLivenessCheck.Value).TotalSeconds > 5)
                    {
                        _cachedBgAlive = StallDetector.AnyBgProcessAlive(_runDb, state.RunId);
                        _lastBgLivenessCheck = DateTime.UtcNow;
                    }

                    var verdict = stallDetector.Evaluate(
                        agent.LastActivityUtc,
                        agent.LastToolCallUtc,
                        _cachedBgAlive);

                    switch (verdict)
                    {
                        case StallVerdict.Active:
                            stallGraceLogged = false;
                            break;
                        case StallVerdict.SoftKillStarted:
                            Log($"stall: all signals quiet for {plan.Limits.StallMinutes}m — {plan.Limits.StallGraceMinutes}m soft-kill grace window started");
                            stallGraceLogged = true;
                            break;
                        case StallVerdict.GraceRunning:
                            if (!stallGraceLogged) { stallGraceLogged = true; Log("stall: in soft-kill grace window — waiting for agent to recover"); }
                            break;
                        case StallVerdict.HardKill:
                            stalled = true;
                            Log("stall: grace window expired — killing session");
                            agent.Kill();
                            break;
                    }

                    if ((DateTime.UtcNow - agent.StartedUtc).TotalMinutes > plan.Limits.SessionTimeoutMinutes)
                    {
                        timedOut = true;
                        Log($"timeout: session exceeded {plan.Limits.SessionTimeoutMinutes}m — killing");
                        agent.Kill();
                    }
                }
                PushSessionSnapshot(agent, rec, stage, attempt, maxAttempts, preTrack);
                if (plan.Report.HeartbeatMinutes > 0 && (DateTime.UtcNow - lastHeartbeat).TotalMinutes >= plan.Report.HeartbeatMinutes)
                {
                    lastHeartbeat = DateTime.UtcNow;
                    RefreshReport(rec, stage, agent, preTrack);
                }
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { killedByUser = true; agent.Kill(); }
            }
            var exit = agent.WaitForExitCode();
            while (agent.TryDequeue(out var ev)) { sink.AgentEvent(ev); TrackActivity(ev, rec.Number); }
            agent.ReapStrays();

            rec.EndedUtc = DateTime.UtcNow;
            rec.CostUsd = agent.CostUsd;
            rec.NumTurns = agent.NumTurns;
            rec.TokensInput = agent.TokensInput;
            rec.TokensOutput = agent.TokensOutput;
            rec.TokensReasoning = agent.TokensReasoning;
            rec.TokensCacheRead = agent.TokensCacheRead;
            rec.ResultSummary = ExtractSessionResult(agent.ResultText);
            if (kind == SessionKind.Audit && !state.AuditedStages.Contains(stage.Id))
                state.AuditedStages.Add(stage.Id);
            Log($"session #{rec.Number} exited (code {exit}, {(rec.EndedUtc - rec.StartedUtc).Value.TotalMinutes:0}m" +
                (agent.CostUsd.HasValue ? $", ${agent.CostUsd:0.00}" : "") + ")");

            FoldMcpJournal();
            CleanupMcpConfig(mcpConfigPath);

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

            if (plan.Limits.MaxSessionTokens is { } maxTok && rec.TokensTotal >= maxTok)
            {
                rec.Outcome = SessionOutcome.RolledOver;
                rec.ResultSummary = ExtractSessionResult(agent.ResultText);
                if (kind == SessionKind.Audit && !state.AuditedStages.Contains(stage.Id))
                    state.AuditedStages.Add(stage.Id);
                var resumeCtx = BuildRolloverResumeHint(preTrack);
                Log($"session #{rec.Number} rolled over — {rec.TokensTotal / 1000.0:0.#}k tokens ≥ {maxTok / 1000.0:0.#}k limit, handoff written{(resumeCtx != null ? $" · {resumeCtx}" : "")}");
                ReflectionStep(rec);
                SaveAndReport();
                return;
            }

            await EvaluateSessionAsync(rec, stage, preTrack, startHead, stalled, timedOut, killedByUser,
                agentErrored: agent.ResultIsError || (exit != 0 && !stalled && !timedOut && !killedByUser), ct).ConfigureAwait(false);

            ReflectionStep(rec);
        }
    }
}
