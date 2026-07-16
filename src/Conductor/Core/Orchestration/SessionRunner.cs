using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Lanes;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
public sealed partial class SessionRunner
{
    private readonly RunContext _ctx;
    private readonly LaneCoordinator _lanes;

    // ── delegates for cross-cutting ops owned by RunLoop/VerdictEngine ──

    private readonly Func<CancellationToken, Task<ControlAction?>> _handleControl;
    private readonly Action<AgentSession, SessionRecord, StageConfig, int, int, TrackerSnapshot> _pushSessionSnapshot;
    private readonly Action _saveAndReport;
    private readonly Func<SessionRecord, StageConfig, TrackerSnapshot, string, bool, bool, bool, bool, CancellationToken, Task> _evaluateSession;
    private readonly Action<SessionRecord, string, bool, bool> _queueResume;
    private readonly Action<string> _needsHuman;
    private readonly Action<SessionRecord> _reflectionStep;

    public SessionRunner(
        RunContext ctx,
        LaneCoordinator lanes,
        Func<CancellationToken, Task<ControlAction?>> handleControl,
        Action<AgentSession, SessionRecord, StageConfig, int, int, TrackerSnapshot> pushSessionSnapshot,
        Action saveAndReport,
        Func<SessionRecord, StageConfig, TrackerSnapshot, string, bool, bool, bool, bool, CancellationToken, Task> evaluateSession,
        Action<SessionRecord, string, bool, bool> queueResume,
        Action<string> needsHuman,
        Action<SessionRecord> reflectionStep)
    {
        _ctx = ctx;
        _lanes = lanes;
        _handleControl = handleControl;
        _pushSessionSnapshot = pushSessionSnapshot;
        _saveAndReport = saveAndReport;
        _evaluateSession = evaluateSession;
        _queueResume = queueResume;
        _needsHuman = needsHuman;
        _reflectionStep = reflectionStep;
    }

    // ── entry point ──

    public async Task RunAsync(StageConfig stage, TrackerSnapshot preTrack, CancellationToken ct)
    {
        var pendingResume = _ctx.State.PendingResume; _ctx.State.PendingResume = null;
        var pendingAudit = _ctx.State.PendingAudit; _ctx.State.PendingAudit = null;
        var pendingFix = _ctx.State.PendingFix; _ctx.State.PendingFix = null;
        var pendingVerify = _ctx.State.PendingVerify; _ctx.State.PendingVerify = null;

        // Resolve session kind: workflow-driven (M3.1) with pending-state fallback
        // for crash recovery (Resume must carry the agent session id).
        var kind = ResolveSessionKind(stage, pendingResume, pendingAudit, pendingVerify, pendingFix);

        // A workflow-resolved kind can arrive without its pending context (a custom workflow that
        // opens on a QA step, or a pending cleared out from under the recorded index). Verify and
        // audit have a well-defined meaning without one — review the stage's work since it started
        // — so synthesize that context rather than dereference null. A fix without failure context
        // is just a delivery attempt; fall back honestly.
        var lastSession = _ctx.State.History.Count > 0 ? _ctx.State.History[^1].Number : _ctx.State.SessionCounter;
        if (kind == SessionKind.Verify && pendingVerify is null)
            pendingVerify = new PendingVerify { FromSession = lastSession, StageId = stage.Id, StageStartHead = _ctx.State.CurrentStageStartHead ?? "" };
        else if (kind == SessionKind.Audit && pendingAudit is null)
            pendingAudit = new PendingAudit { StageId = stage.Id, StageStartHead = _ctx.State.CurrentStageStartHead ?? "" };
        else if (kind == SessionKind.Fix && pendingFix is null)
            kind = SessionKind.Deliver;

        // P1: ask the assignment policy who runs this session and which ready items it claims.
        // With no `pipeline` rules the default policy reproduces the classic behavior exactly
        // (stage/plan default agent, the first not-done checkpoint, one item).
        var readyItems = preTrack.ForStage(stage.Id).Where(c => !c.IsDone)
            .Select(c => new ReadyItem { Id = c.Id, Title = c.Title })
            .ToList();
        var assignment = _ctx.Assignments.Assign(_ctx.Plan.Pipeline, kind, readyItems, claimedPaths: null);
        if (assignment.Model != null || assignment.Persona != null || assignment.Command != null)
            _ctx.Log($"P1 assignment: role '{DefaultAssignmentPolicy.RoleFor(kind)}' → " +
                     $"{(assignment.Command != null ? $"command {assignment.Command} " : "")}" +
                     $"{(assignment.Model != null ? $"model {assignment.Model} " : "")}" +
                     $"{(assignment.Persona != null ? $"persona {assignment.Persona}" : "")}".TrimEnd());
        if (assignment.Items.Count > 1)
            _ctx.Log($"P1 assignment: multi-item session — claims {string.Join(", ", assignment.Items.Select(i => i.Id))}");

        _ctx.State.SessionCounter++;
        var attempt = _ctx.State.AttemptsThisStage + 1;
        var maxAttempts = MaxAttempts(stage);
        var isReview = stage.Kind.Equals("review", StringComparison.OrdinalIgnoreCase);
        var reviewDir = Path.Combine(_ctx.Plan.StateDir, "reviews");
        var reviewPath = isReview ? Path.Combine(reviewDir, $"{stage.Id}.md") : "";
        if (isReview)
        {
            Directory.CreateDirectory(reviewDir);
            var skeleton = $"# Self-review: {stage.Id} — {stage.Title}\n\n" +
                           $"_Generated {DateTime.UtcNow:u} by Conductor (B8.3) — pending agent review_\n";
            await File.WriteAllTextAsync(reviewPath, skeleton, ct).ConfigureAwait(false);
            _ctx.Log($"review stage {stage.Id}: scaffolded review artifact at {reviewPath}");
        }

        var personaName = assignment.Persona ?? _ctx.Plan.ResolvePersona(stage);
        var activeCp = preTrack.ForStage(stage.Id).FirstOrDefault(c => !c.IsDone);
        if (kind == SessionKind.Deliver &&
            "deliver".Equals(personaName, StringComparison.OrdinalIgnoreCase) &&
            activeCp != null &&
            _ctx.DecomposedCheckpoints.Add(activeCp.Id))
        {
            var tasks = _ctx.Planner.Decompose(activeCp.Id, activeCp.Title, stage.Notes ?? "");
            var runId = _ctx.State.RunId;
            foreach (var task in tasks)
            {
                _ctx.Events.Emit(new TaskAdded
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
                _ctx.Log($"B9.2: decomposed checkpoint {activeCp.Id} into {tasks.Count} sub-task(s)");
        }

        var prompt = BuildPrompt(kind, stage, _ctx.State.SessionCounter, attempt, maxAttempts,
            pendingResume, pendingAudit, pendingVerify, pendingFix, isReview, reviewPath, assignment.Persona);
        var batterySection = _ctx.Prompts.BatterySection(_ctx.State, _ctx.Store);
        if (batterySection.Length > 0)
            prompt = prompt.TrimEnd() + "\n\n" + batterySection;

        // P1: a multi-item session must SEE every item it claimed — the prompt names each one.
        if (assignment.Items.Count > 1)
        {
            var claimedList = new StringBuilder();
            claimedList.AppendLine("## Claimed items this session");
            claimedList.AppendLine("The assignment policy claimed ALL of the following conflict-free items for this single session. Deliver each one and update its tracker row (Status + Commit + Evidence) individually.");
            foreach (var item in assignment.Items)
                claimedList.AppendLine($"- **{item.Id}** — {item.Title}");
            prompt = prompt.TrimEnd() + "\n\n" + claimedList.ToString().TrimEnd() + "\n";
        }

        // P3: owner-provided per-task context must reach the session that delivers the task — the
        // card-detail edit is real prompt input, not decoration. Scope = the claimed checkpoints.
        if (_ctx.Store != null)
        {
            var taskGraph = new TaskGraph();
            taskGraph.Fold(_ctx.Store.ReadAllEvents(_ctx.State.RunId));
            var contextSection = BuildTaskContextSection(taskGraph, assignment.Items.Select(i => i.Id));
            if (contextSection.Length > 0)
                prompt = prompt.TrimEnd() + "\n\n" + contextSection;
        }

        if (kind == SessionKind.Deliver && _ctx.State.ParallelAuditOutcome is { Completed: true, MaxSeverity: not AuditFindingSeverity.High } outcome)
        {
            var findings = Trunc(outcome.Findings, 3000);
            if (!string.IsNullOrWhiteSpace(findings))
            {
                prompt = prompt.TrimEnd() + $"\n\n## Parallel audit findings for stage {outcome.StageId}\n" +
                    "The following audit findings were produced by a read-only audit lane running concurrently with the previous stage. " +
                    "Address LOW and MEDIUM findings in this session if convenient.\n\n{findings}";
                _ctx.State.ParallelAuditOutcome = null;
            }
        }

        var rec = new SessionRecord
        {
            Number = _ctx.State.SessionCounter,
            Stage = stage.Id,
            Kind = kind,
            Attempt = attempt,
            StartedUtc = DateTime.UtcNow,
            ClaudeSessionId = pendingResume?.ClaudeSessionId ?? Guid.NewGuid().ToString(),
            ResumeCount = pendingResume?.ResumeCount ?? 0,
        };
        var logsDir = Path.Combine(_ctx.Plan.StateDir, "logs");
        await File.WriteAllTextAsync(Path.Combine(logsDir, $"session-{rec.Number:000}.prompt.md"), prompt, ct).ConfigureAwait(false);
        InstructionQueue.ConsumeAll(_ctx.Plan);
        var rawLog = Path.Combine(logsDir, $"session-{rec.Number:000}.jsonl");

        var startHead = Git.Head(_ctx.Plan.Repo);
        _ctx.State.History.Add(rec);
        _ctx.State.Status = RunStatus.Running;
        _ctx.Save();
        _ctx.SoftBreakSignalled = false;
        CleanSoftBreakSignal();
        _ctx.Log($"session #{rec.Number} start — {kind} {stage.Id} attempt {attempt}/{maxAttempts}" +
            (kind == SessionKind.Resume ? $" (resume #{rec.ResumeCount} of {rec.ClaudeSessionId[..8]})" : ""));
        _ctx.Events.Emit(new SessionStarted
        {
            SessionId = rec.Number.ToString(),
            Number = rec.Number,
            StageId = stage.Id,
            Kind = kind.ToString(),
            Attempt = attempt,
            MaxAttempts = maxAttempts,
            AgentSessionId = rec.ClaudeSessionId,
            Persona = personaName,
        });

        bool stalled = false, timedOut = false, killedByUser = false;
        await GateRunner.RunHookAsync(_ctx.Plan, _ctx.Plan.Setup, "setup", _ctx.Log, ct).ConfigureAwait(false);
        var resolvedAgent = _ctx.Plan.ResolveAgent(stage);
        // P1: the role→agent rule overrides only what it names; ResolveAgent returns a fresh merged
        // instance, so mutating it never touches the plan's own config.
        if (assignment.Model is { Length: > 0 }) resolvedAgent.Model = assignment.Model;
        if (assignment.Command is { Length: > 0 }) resolvedAgent.Command = assignment.Command;

        var mcpConfigPath = WireMcpServer(rec, stage);
        Dictionary<string, string>? extraEnv = null;
        if (mcpConfigPath != null)
        {
            extraEnv = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_CONFIG"] = mcpConfigPath };
            _ctx.Log("I1: MCP task server wired (opencode config at " + mcpConfigPath + ")");
        }

        using (var agent = AgentSession.Start(resolvedAgent, _ctx.Plan.Repo, prompt, rec.ClaudeSessionId,
                   kind == SessionKind.Resume ? rec.ClaudeSessionId : null, rawLog, _ctx.Events, rec.Number.ToString(), extraEnv, supervisor: _ctx.ProcessSupervisor))
        {
            _ctx.Activity.Clear();
            var lastHeartbeat = DateTime.UtcNow;
            var stallDetector = new StallDetector(
                TimeSpan.FromMinutes(_ctx.Plan.Limits.StallMinutes),
                TimeSpan.FromMinutes(_ctx.Plan.Limits.StallGraceMinutes));
            var stallGraceLogged = false;
            while (!agent.HasExited)
            {
                while (agent.TryDequeue(out var ev)) { _ctx.Sink.AgentEvent(ev); TrackActivity(ev, rec.Number); }
                _lanes.PollLaneCompletion();
                await _lanes.CheckParallelAuditCompletionAsync().ConfigureAwait(false);
                CheckSoftBreak(agent, preTrack);
                var ctl = await _handleControl(ct).ConfigureAwait(false);
                if (ctl == ControlAction.KillSession) { killedByUser = true; _ctx.Log("kill requested"); agent.Kill(); }
                if (ctl == ControlAction.AbortNow) { killedByUser = true; _ctx.State.Status = RunStatus.Aborted; _ctx.Log("abort requested"); agent.Kill(); }
                if (ctl == ControlAction.Heartbeat) { RefreshReport(rec, stage, agent, preTrack); lastHeartbeat = DateTime.UtcNow; }
                if (ct.IsCancellationRequested) { agent.Kill(); }
                else
                {
                    if (_ctx.LastBgLivenessCheck == null || (DateTime.UtcNow - _ctx.LastBgLivenessCheck.Value).TotalSeconds > 5)
                    {
                        _ctx.CachedBgAlive = StallDetector.AnyBgProcessAlive(_ctx.Store, _ctx.State.RunId);
                        _ctx.LastBgLivenessCheck = DateTime.UtcNow;
                    }

                    var verdict = stallDetector.Evaluate(
                        agent.LastActivityUtc,
                        agent.LastToolCallUtc,
                        _ctx.CachedBgAlive);

                    switch (verdict)
                    {
                        case StallVerdict.Active:
                            stallGraceLogged = false;
                            break;
                        case StallVerdict.SoftKillStarted:
                            _ctx.Log($"stall: all signals quiet for {_ctx.Plan.Limits.StallMinutes}m — {_ctx.Plan.Limits.StallGraceMinutes}m soft-kill grace window started");
                            stallGraceLogged = true;
                            break;
                        case StallVerdict.GraceRunning:
                            if (!stallGraceLogged) { stallGraceLogged = true; _ctx.Log("stall: in soft-kill grace window — waiting for agent to recover"); }
                            break;
                        case StallVerdict.HardKill:
                            stalled = true;
                            _ctx.Log("stall: grace window expired — killing session");
                            agent.Kill();
                            break;
                    }

                    if ((DateTime.UtcNow - agent.StartedUtc).TotalMinutes > _ctx.Plan.Limits.SessionTimeoutMinutes)
                    {
                        timedOut = true;
                        _ctx.Log($"timeout: session exceeded {_ctx.Plan.Limits.SessionTimeoutMinutes}m — killing");
                        agent.Kill();
                    }
                }
                _pushSessionSnapshot(agent, rec, stage, attempt, maxAttempts, preTrack);
                if (_ctx.Plan.Report.HeartbeatMinutes > 0 && (DateTime.UtcNow - lastHeartbeat).TotalMinutes >= _ctx.Plan.Report.HeartbeatMinutes)
                {
                    lastHeartbeat = DateTime.UtcNow;
                    RefreshReport(rec, stage, agent, preTrack);
                }
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { killedByUser = true; agent.Kill(); }
            }
            var exit = agent.WaitForExitCode();
            while (agent.TryDequeue(out var ev)) { _ctx.Sink.AgentEvent(ev); TrackActivity(ev, rec.Number); }
            agent.ReapStrays();

            rec.EndedUtc = DateTime.UtcNow;
            rec.CostUsd = agent.CostUsd;
            rec.NumTurns = agent.NumTurns;
            rec.TokensInput = agent.TokensInput;
            rec.TokensOutput = agent.TokensOutput;
            rec.TokensReasoning = agent.TokensReasoning;
            rec.TokensCacheRead = agent.TokensCacheRead;
            rec.ResultSummary = ExtractSessionResult(agent.ResultText);
            if (kind == SessionKind.Audit && !_ctx.State.AuditedStages.Contains(stage.Id))
                _ctx.State.AuditedStages.Add(stage.Id);
            _ctx.Log($"session #{rec.Number} exited (code {exit}, {(rec.EndedUtc - rec.StartedUtc).Value.TotalMinutes:0}m" +
                (agent.CostUsd.HasValue ? $", ${agent.CostUsd:0.00}" : "") + ")");

            FoldMcpJournal();
            CleanupMcpConfig(mcpConfigPath);

            if (ct.IsCancellationRequested)
            {
                rec.Outcome = SessionOutcome.Interrupted;
                _queueResume(rec, "conductor was cancelled mid-session", true, false);
                _ctx.Save();
                return;
            }
            if (_ctx.State.Status == RunStatus.Aborted)
            {
                rec.Outcome = SessionOutcome.KilledByUser;
                _ctx.Save();
                return;
            }

            var limitEvidence = (agent.ResultText ?? "") + " " + (exit != 0 && agent.ResultText == null ? LastRawTail(rawLog) : "");
            if ((agent.ResultIsError || exit != 0) && _ctx.AgentProvider.DetectsUsageLimit(limitEvidence))
            {
                rec.Outcome = SessionOutcome.LimitBackoff;
                _ctx.State.ConsecutiveBackoffs++;
                if (_ctx.State.ConsecutiveBackoffs > _ctx.Plan.Limits.MaxBackoffs)
                {
                    _needsHuman($"agent backend refused {_ctx.State.ConsecutiveBackoffs} times in a row (usage limit?) — check quota");
                    return;
                }
                _queueResume(rec, "usage/rate limit backoff", false, false);
                _ctx.BackoffUntil = DateTime.UtcNow.AddMinutes(_ctx.Plan.Limits.BackoffMinutes);
                _ctx.State.Status = RunStatus.Backoff;
                _ctx.Log($"usage limit detected — backing off {_ctx.Plan.Limits.BackoffMinutes}m (until {_ctx.BackoffUntil:HH:mm} UTC)");
                _saveAndReport();
                return;
            }
            _ctx.State.ConsecutiveBackoffs = 0;

            if (_ctx.Plan.Limits.MaxSessionTokens is { } maxTok && rec.TokensTotal >= maxTok)
            {
                rec.Outcome = SessionOutcome.RolledOver;
                rec.ResultSummary = ExtractSessionResult(agent.ResultText);
                if (kind == SessionKind.Audit && !_ctx.State.AuditedStages.Contains(stage.Id))
                    _ctx.State.AuditedStages.Add(stage.Id);
                var resumeCtx = BuildRolloverResumeHint(preTrack);
                _ctx.Log($"session #{rec.Number} rolled over — {rec.TokensTotal / 1000.0:0.#}k tokens ≥ {maxTok / 1000.0:0.#}k limit, handoff written{(resumeCtx != null ? $" · {resumeCtx}" : "")}");
                _reflectionStep(rec);
                _saveAndReport();
                return;
            }

            await _evaluateSession(rec, stage, preTrack, startHead, stalled, timedOut, killedByUser,
                agent.ResultIsError || (exit != 0 && !stalled && !timedOut && !killedByUser), ct).ConfigureAwait(false);

            _reflectionStep(rec);
        }
    }

    private int MaxAttempts(StageConfig stage) => Math.Max(1, stage.Sessions * _ctx.Plan.Limits.StageSlackFactor);

    // ── snapshot + activity tracking ──

    private void TrackActivity(AgentEvent ev, int sessionNumber)
    {
        if (ev.Kind is not ("tool" or "text" or "result" or "thinking")) return;
        _ctx.Activity.Add((ev.Kind, ev.Text, ev.Utc));
        if (_ctx.Activity.Count > 60) _ctx.Activity.RemoveRange(0, 20);
    }

    // ── static helpers ──

    private static string ExtractSessionResult(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return "";
        var idx = resultText.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        var s = idx >= 0 ? resultText[idx..] : resultText;
        return Trunc(s.Trim(), 700);
    }

    private string LastRawTail(string rawLogPath)
    {
        try { return GateRunner.TailOf(File.ReadAllText(rawLogPath), 10); }
        catch (IOException) { return ""; }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";

    /// <summary>P3: the prompt section carrying owner-provided per-task context for the claimed
    /// checkpoints \u2014 only open cards (todo/in_progress) with non-empty context appear; empty when
    /// there is nothing to say, so untouched plans keep byte-identical prompts.</summary>
    internal static string BuildTaskContextSection(TaskGraph graph, IEnumerable<string> checkpointIds)
    {
        var lines = new List<string>();
        foreach (var cpId in checkpointIds)
        {
            foreach (var t in graph.ForCheckpoint(cpId))
            {
                if (t.Status is not ("todo" or "in_progress") || string.IsNullOrWhiteSpace(t.Context)) continue;
                lines.Add($"- **{t.TaskId} \u2014 {t.Title}**: {t.Context.Trim()}");
            }
        }
        if (lines.Count == 0) return "";
        return "## Task context (owner-provided)\n" +
               "The owner attached extra context to these open sub-tasks \u2014 honor it when delivering them.\n" +
               string.Join("\n", lines) + "\n";
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;

    // ── report refresh (delegated from RunLoop) ──

    private void RefreshReport(SessionRecord rec, StageConfig stage, AgentSession agent, TrackerSnapshot track)
    {
        try
        {
            var cp = track.ForStage(stage.Id).FirstOrDefault(c => !c.IsDone)?.Id ?? stage.Id;
            _ctx.Log($"report refresh @ {cp} (cost ${agent.CostUsd:0.00})");
            Reporter.WriteReport(_ctx.Plan, _ctx.State, track, _ctx.LastGates, _ctx.Log, BuildActivitySection(rec, agent), store: _ctx.Store);
        }
        catch (Exception ex) { _ctx.Log($"report refresh failed: {ex.Message}"); }
    }

    private string BuildActivitySection(SessionRecord rec, AgentSession agent)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"_Session #{rec.Number} ({rec.Kind}) · running {(DateTime.UtcNow - agent.StartedUtc).TotalMinutes:0}m · " +
                      $"last output {(DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds:0}s ago" +
                      (agent.CostUsd is { } c ? $" · ${c:0.0000}" : "") + "_");
        sb.AppendLine();
        var think = _ctx.Activity.Where(a => a.Kind == "thinking").TakeLast(3).ToList();
        if (think.Count > 0)
        {
            sb.AppendLine("**Thinking:**");
            foreach (var t in think) sb.AppendLine($"> {Trunc(t.Text.Replace("\n", " "), 300)}");
            sb.AppendLine();
        }
        var acts = _ctx.Activity.Where(a => a.Kind != "thinking").TakeLast(10).ToList();
        if (acts.Count > 0)
        {
            sb.AppendLine("**Recent actions:**");
            foreach (var a in acts)
            {
                var glyph = a.Kind switch { "tool" => "\u00bb", "result" => "\u25c6", _ => "\u00b7" };
                sb.AppendLine($"- `{a.Utc.ToLocalTime():HH:mm:ss}` {glyph} {Trunc(a.Text.Replace("\n", " "), 160)}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
