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
    private readonly Action<SessionRecord, StageConfig, TrackerSnapshot, string, CancellationToken> _recordRolloverFacts;
    private readonly Action<SessionRecord, string, bool, bool> _queueResume;
    private readonly Action<string> _needsHuman;
    private readonly Action<SessionRecord> _reflectionStep;
    private readonly Action<string> _notify;

    public SessionRunner(
        RunContext ctx,
        LaneCoordinator lanes,
        Func<CancellationToken, Task<ControlAction?>> handleControl,
        Action<AgentSession, SessionRecord, StageConfig, int, int, TrackerSnapshot> pushSessionSnapshot,
        Action saveAndReport,
        Func<SessionRecord, StageConfig, TrackerSnapshot, string, bool, bool, bool, bool, CancellationToken, Task> evaluateSession,
        Action<SessionRecord, StageConfig, TrackerSnapshot, string, CancellationToken> recordRolloverFacts,
        Action<SessionRecord, string, bool, bool> queueResume,
        Action<string> needsHuman,
        Action<SessionRecord> reflectionStep,
        Action<string>? notify = null)
    {
        _ctx = ctx;
        _lanes = lanes;
        _handleControl = handleControl;
        _pushSessionSnapshot = pushSessionSnapshot;
        _saveAndReport = saveAndReport;
        _evaluateSession = evaluateSession;
        _recordRolloverFacts = recordRolloverFacts;
        _queueResume = queueResume;
        _needsHuman = needsHuman;
        _reflectionStep = reflectionStep;
        _notify = notify ?? (_ => { });
    }

    // ── entry point ──

    public async Task RunAsync(StageConfig stage, TrackerSnapshot preTrack, CancellationToken ct)
    {
        var pendingResume = _ctx.State.PendingResume; _ctx.State.PendingResume = null;
        var pendingAudit = _ctx.State.PendingAudit; _ctx.State.PendingAudit = null;
        var pendingFix = _ctx.State.PendingFix; _ctx.State.PendingFix = null;
        var pendingVerify = _ctx.State.PendingVerify; _ctx.State.PendingVerify = null;

        // P1/PF3/W4.4: the work graph, folded ONCE — the kind ladder's per-item QA dial, the
        // assignment policy's declared path claims and the P3 task-context section all read this
        // same projection (the dial used to fold a private second copy).
        TaskGraph? taskGraph = null;
        if (_ctx.Store != null)
        {
            taskGraph = new TaskGraph();
            taskGraph.Fold(_ctx.Store.ReadAllEvents(_ctx.State.RunId));
        }

        // Resolve session kind: workflow-driven (M3.1) with pending-state fallback for crash
        // recovery (Resume must carry the agent session id). The ladder IS the launch decision's
        // (StageSelection — round 6): the same function `preflight`'s drill and the dry run read,
        // consulted here with the live step-index dictionary because a stage's very first workflow
        // resolution advances and records it, and that side effect belongs where the session begins.
        var kind = ResolveSessionKind(stage, pendingResume, pendingAudit, pendingVerify, pendingFix, preTrack, taskGraph);

        // W1.3 (bug #6): a Verify session reviews the stage that DELIVERED. After an advance the
        // loop's current stage has already moved on — PendingVerify.StageId is authoritative for
        // the prompt, the session record, and the verdict scope. (All three U-series verifies were
        // dispatched against the NEXT stage and produced nothing usable.) The rule is shared with
        // the compose surfaces, so the drill names the same stage this dispatch records.
        var target = SessionComposer.EffectiveStage(_ctx.Plan, stage, kind, pendingVerify);
        if (!ReferenceEquals(target, stage))
        {
            _ctx.Log($"verify session targets stage {target.Id} (the delivered work), not the loop's current {stage.Id}");
            stage = target;
        }

        _ctx.State.SessionCounter++;
        var attempt = _ctx.State.NextAttemptNumber; // SC2.2: the one source every attempt line reads
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

        // Round 6: the prompt — template render (assignment-persona resolved), battery section,
        // claimed-items list, task-context cards, parallel-audit findings — is SessionComposer's,
        // ONE call shared with the dry run and preflight's compose leg, so the measured length IS
        // the spawned length. This runner performs only the mutations the composition implies.
        var composed = SessionComposer.Compose(_ctx.Plan, _ctx.Prompts, _ctx.Assignments,
            _ctx.State, preTrack, taskGraph, _ctx.Store,
            kind, stage, _ctx.State.SessionCounter, attempt,
            pendingResume, pendingAudit, pendingVerify, pendingFix);
        kind = composed.Kind;
        stage = composed.Stage;
        pendingAudit = composed.Audit;
        pendingVerify = composed.Verify;
        pendingFix = composed.Fix;
        var assignment = composed.Assignment;
        var prompt = composed.Prompt;
        if (composed.ConsumesParallelAuditOutcome)
            _ctx.State.ParallelAuditOutcome = null;

        // P1: with no `pipeline` rules the default policy reproduces the classic behavior exactly
        // (stage/plan default agent, the first not-done checkpoint, one item). SC5.3: a SKIPPED
        // checkpoint is settled work — the composer offers only open items, so `task --skipped`
        // actually stops the scheduling it claims to.
        if (assignment.Model != null || assignment.Persona != null || assignment.Command != null)
            _ctx.Log($"P1 assignment: role '{DefaultAssignmentPolicy.RoleFor(kind)}' → " +
                     $"{(assignment.Command != null ? $"command {assignment.Command} " : "")}" +
                     $"{(assignment.Model != null ? $"model {assignment.Model} " : "")}" +
                     $"{(assignment.Persona != null ? $"persona {assignment.Persona}" : "")}".TrimEnd());
        if (assignment.Items.Count > 1)
            _ctx.Log($"P1 assignment: multi-item session — claims {string.Join(", ", assignment.Items.Select(i => i.Id))}");

        var personaName = assignment.Persona ?? _ctx.Plan.ResolvePersona(stage);
        var activeCp = preTrack.ForStage(stage.Id).FirstOrDefault(c => c.IsOpen);
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
        // SC4.3: the satellites' start markers have to be taken HERE, next to the primary repo's,
        // or the post-session diff has nothing to diff against.
        rec.SatelliteStartHeads = SatelliteRepos.Heads(_ctx.Plan, _ctx.Log);
        if (rec.SatelliteStartHeads.Count > 0)
            _ctx.Log($"watching {rec.SatelliteStartHeads.Count} satellite repo(s) for commits: {string.Join(", ", rec.SatelliteStartHeads.Select(kv => $"{kv.Key}@{kv.Value[..Math.Min(7, kv.Value.Length)]}"))}");
        _ctx.State.History.Add(rec);
        _ctx.State.Status = RunStatus.Running;
        _ctx.Save();
        _ctx.SoftBreakSignalled = false;
        _softBreakSignalledAtTokens = 0;
        CleanSoftBreakSignal();
        _ctx.Log($"session #{rec.Number} start — {kind} {stage.Id} attempt {attempt}/{maxAttempts}" +
            (kind == SessionKind.Resume ? $" (resume #{rec.ResumeCount} of {rec.ClaudeSessionId[..8]})" : ""));
        var resolvedAgent = _ctx.Plan.ResolveAgent(stage);
        // P1: the role→agent rule overrides only what it names; ResolveAgent returns a fresh merged
        // instance, so mutating it never touches the plan's own config.
        if (assignment.Model is { Length: > 0 }) resolvedAgent.Model = assignment.Model;
        if (assignment.Command is { Length: > 0 }) resolvedAgent.Command = assignment.Command;
        _ctx.Events.Emit(new SessionStarted
        {
            SessionId = rec.Number.ToString(), Number = rec.Number,
            StageId = stage.Id, Kind = kind.ToString(),
            Attempt = attempt, MaxAttempts = maxAttempts,
            AgentSessionId = rec.ClaudeSessionId,
            Persona = personaName,
            Model = resolvedAgent.Model,
        });
        // SF5: durable BEFORE the agent exists. Its seq is the boundary "claimed during this session"
        // is measured against; SF5SessionStartSeqTests has the inversion this prevents.
        _ctx.Store?.FlushEvents();
        _ctx.Transcript.Append(rec.Number.ToString(), "system",
            $"Session #{rec.Number} started · {kind} · Stage {stage.Id} · Attempt {attempt}/{maxAttempts}" +
            (string.IsNullOrEmpty(resolvedAgent.Model) ? "" : $" · {resolvedAgent.Model}"));

        bool stalled = false, timedOut = false, killedByUser = false, budgetKilled = false;
        await GateRunner.RunHookAsync(_ctx.Plan, _ctx.Plan.Setup, "setup", _ctx.Log, ct).ConfigureAwait(false);

        var mcpWiring = await WireMcpServerAsync(rec, stage, ct).ConfigureAwait(false);
        // W2.1: CONDUCTOR_PLAN scopes every in-worker `conductor task/bug/note` to THIS run's plan.
        // Without it the child resolved by scanning the cwd, and a repo holding more than one
        // *.plan.json killed the verb outright ("Multiple plan files found") with output redirected —
        // the four U-series crash-*.logs. Set unconditionally: the CLI verbs matter even if the MCP
        // config could not be written.
        var extraEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(_ctx.Plan.PlanFilePath))
            extraEnv["CONDUCTOR_PLAN"] = _ctx.Plan.PlanFilePath;
        // SF0.3 (FU-OWNER-9): the pid of the process supervising this session, so an agent can CHECK
        // instead of inferring. A fix session met a build error reading `locked by: conductor (15300)`,
        // reasoned that a leftover orphan held the lock, and ran `Stop-Process -Id 15300` on the very
        // conductor that had spawned it — two sessions' worth of work gone with no crash dump. It had
        // no way to know: nothing in its environment or its prompt named that number. Now both do
        // (see ToolContract). It matters more since: this machine runs more than one conductor, so a
        // pid an agent writes off as stale can belong to another repo's live run.
        extraEnv["CONDUCTOR_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyList<string> extraArgs = [];
        if (mcpWiring != null)
        {
            extraEnv["OPENCODE_CONFIG"] = mcpWiring.OpencodeConfigPath;
            var provider = Providers.AgentProviderFactory.ResolveName(resolvedAgent);
            extraArgs = McpArgsFor(provider, resolvedAgent.Args, mcpWiring.ClaudeConfigPath, mcpWiring.ClaudeSettingsPath, resolvedAgent.Permissions);
            _ctx.Log($"I1: MCP task server wired ({provider}) — opencode config {mcpWiring.OpencodeConfigPath}" +
                     (extraArgs.Count > 0 ? $", claude --mcp-config {mcpWiring.ClaudeConfigPath}" : ""));
        }
        // KS7.1: state the posture that was APPLIED, in the run log, next to the flags it produced —
        // measured against the resolved args rather than read off the plan, because the two disagree
        // exactly when it matters (a stage override, a resume template with its own flags).
        if (resolvedAgent.Permissions is { IsConfigured: true })
        {
            var stripped = resolvedAgent.Permissions.WantsRestrictedMode
                ? Providers.PermissionPosture.BypassFlagCount(resolvedAgent.Args)
                : 0;
            _ctx.Log(Providers.PermissionPosture.Describe(resolvedAgent.Permissions, stripped));
        }

        using (var agent = AgentSession.Start(resolvedAgent, _ctx.Plan.Repo, prompt, rec.ClaudeSessionId,
                   kind == SessionKind.Resume ? rec.ClaudeSessionId : null, rawLog, _ctx.Events, rec.Number.ToString(), extraEnv, supervisor: _ctx.ProcessSupervisor, extraArgs: extraArgs, stageId: stage.Id))
        {
            _ctx.Activity.Clear();
            var lastHeartbeat = DateTime.UtcNow;

            // W3.1: the stall + hard-timeout rails run on their own thread. Inside this loop they
            // could only fire when the loop got around to them — bug #8's 90-minute timeout firing
            // at 337 minutes. The bg-liveness sample keeps its 5s cache, now owned by the watchdog
            // thread alone (no shared mutable state with the poll loop).
            DateTime? lastBgCheck = null;
            var cachedBgAlive = false;
            using var watchdog = new SessionWatchdog(
                hardTimeout: _ctx.Plan.Limits.EffectiveSessionTimeout,
                stallThreshold: _ctx.Plan.Limits.EffectiveStall,
                stallGrace: _ctx.Plan.Limits.EffectiveStallGrace,
                sample: () =>
                {
                    if (lastBgCheck == null || (DateTime.UtcNow - lastBgCheck.Value).TotalSeconds > 5)
                    {
                        cachedBgAlive = StallDetector.AnyBgProcessAlive(_ctx.Store, _ctx.State.RunId);
                        lastBgCheck = DateTime.UtcNow;
                    }
                    return new WatchdogSignals(agent.LastActivityUtc, agent.LastToolCallUtc, cachedBgAlive);
                },
                onAction: (action, message) => OnWatchdogAction(agent, rec, stage, action, message));
            watchdog.Start();

            while (!agent.HasExited)
            {
                while (agent.TryDequeue(out var ev)) { _ctx.Sink.AgentEvent(ev); TrackActivity(ev, rec); }
                _lanes.PollLaneCompletion();
                await _lanes.CheckParallelAuditCompletionAsync().ConfigureAwait(false);
                CheckSoftBreak(agent, preTrack);
                if (!budgetKilled && OverSessionTokenBudget(agent)) budgetKilled = EndOnBudget(agent, rec);
                var ctl = await _handleControl(ct).ConfigureAwait(false);
                if (ctl == ControlAction.KillSession) { killedByUser = true; _ctx.Log("kill requested"); agent.Kill(); }
                if (ctl == ControlAction.AbortNow) { killedByUser = true; _ctx.State.Status = RunStatus.Aborted; _ctx.Log("abort requested"); agent.Kill(); }
                if (ctl == ControlAction.Heartbeat) { RefreshReport(rec, stage, agent, preTrack); lastHeartbeat = DateTime.UtcNow; }
                if (ct.IsCancellationRequested) { agent.Kill(); }
                _pushSessionSnapshot(agent, rec, stage, attempt, maxAttempts, preTrack);
                if (_ctx.Plan.Report.HeartbeatMinutes > 0 && (DateTime.UtcNow - lastHeartbeat).TotalMinutes >= _ctx.Plan.Report.HeartbeatMinutes)
                {
                    lastHeartbeat = DateTime.UtcNow;
                    RefreshReport(rec, stage, agent, preTrack);
                }
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { killedByUser = true; agent.Kill(); }
            }
            // W3.1: the rails' verdicts are read back here — the watchdog thread decided them,
            // and it has already killed the process by the time this loop notices the exit.
            watchdog.Stop();
            stalled = watchdog.Stalled;
            timedOut = watchdog.TimedOut;
            var exit = agent.WaitForExitCode();
            while (agent.TryDequeue(out var ev)) { _ctx.Sink.AgentEvent(ev); TrackActivity(ev, rec); }
            agent.ReapStrays();

            rec.EndedUtc = DateTime.UtcNow;
            rec.CostUsd = agent.CostUsd;
            if (budgetKilled) rec.CostUsd ??= PriceBudgetKill(agent);
            rec.NumTurns = agent.NumTurns;
            rec.TokensInput = agent.TokensInput;
            rec.TokensOutput = agent.TokensOutput;
            rec.TokensReasoning = agent.TokensReasoning;
            rec.TokensCacheRead = agent.TokensCacheRead;
            // K4.1: only when the stream actually reported per-turn usage. `Measured` is false for a
            // provider that never emitted one, and recording None there would put a 0k context on a
            // session that simply was not instrumented.
            rec.Context = agent.Context is { Measured: true } ctx ? ctx : null;
            // K1.2: read the cooperative rail's own record BEFORE any branch below returns — the
            // measurement is owed on every outcome, and the rollover path is precisely the one where
            // it matters most.
            rec.SoftBreak = ReadSoftBreakOutcome(budgetKilled, rec.TokensTotal);
            if (rec.SoftBreak is { } sb) _ctx.Log($"session #{rec.Number} {sb.Summary()}", sb.Obeyed ? "pass" : "warn");
            rec.ResultSummary = ExtractSessionResult(agent.ResultText, rec.Kind);
            if (kind == SessionKind.Audit && !_ctx.State.AuditedStages.Contains(stage.Id))
                _ctx.State.AuditedStages.Add(stage.Id);
            _ctx.Log($"session #{rec.Number} exited (code {exit}, {(rec.EndedUtc - rec.StartedUtc).Value.TotalMinutes:0}m" +
                (agent.CostUsd.HasValue ? $", ${agent.CostUsd:0.00}" : "") + ")");
            // SC7.2: the digest at a glance, in the log, beside the exit it describes.
            if (!rec.Digest.IsEmpty) _ctx.Log($"session #{rec.Number} digest: {rec.Digest.Summary()}");
            // KS7.1: what the posture actually stopped. Logged only when it stopped something, so a
            // clean session stays quiet — but when it fires, every distinct refusal is named, because
            // "3 refusals" without the rules that fired is a number nobody can act on.
            if (agent.Refusals.Count > 0)
            {
                var byRule = agent.Refusals.GroupBy(r => r.Line, StringComparer.Ordinal)
                    .Select(g => g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key);
                _ctx.Log($"session #{rec.Number} posture refused {agent.Refusals.Count} tool call(s): " +
                         string.Join(" · ", byRule), "warn");
            }
            _ctx.Transcript.Append(rec.Number.ToString(), "system",
                $"Session #{rec.Number} exited · code {exit} · {(rec.EndedUtc - rec.StartedUtc).Value.TotalMinutes:0}m" +
                (agent.CostUsd.HasValue ? $" · ${agent.CostUsd:0.00}" : ""));

            FoldMcpJournal();
            CleanupMcpConfig(mcpWiring);

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

            // W3.2: a dead credential is checked BEFORE the usage limit, because it looks like one
            // (both are refusals from the backend) and is nothing like one: backing off 30 minutes
            // and retrying cannot mint a new token, and the advisor is the same CLI, so its
            // judgement died with the credential. U-series #13 was recorded as a generic AgentError
            // and burned the stage's remaining attempts against a 401.
            var authEvidence = (agent.AuthFailure ?? "") + " " + limitEvidence + " " +
                               (agent.ResultText == null ? LastRawTail(rawLog) : "");
            // budgetKilled short-circuits both refusal checks below: WE ended this process, so its
            // nonzero exit and truncated tail are our own doing. Read as a dead credential it would go
            // to NeedsHuman; as a rate limit it would park 30 minutes. Both are wrong answers here.
            if (!budgetKilled && (agent.ResultIsError || exit != 0 || agent.AuthFailure != null)
                && _ctx.AgentProvider.DetectsAuthFailure(authEvidence))
            {
                rec.Outcome = SessionOutcome.AuthFailed;
                var detail = Trunc((agent.AuthFailure ?? agent.ResultText ?? "the agent backend rejected the credential").Trim(), 200);
                _ctx.Log($"auth: the agent backend rejected the credential — {detail}");
                _needsHuman($"re-auth: {ReauthHint(_ctx.AgentProvider.Name)} — {detail}");
                return;
            }

            if (!budgetKilled && (agent.ResultIsError || exit != 0) && _ctx.AgentProvider.DetectsUsageLimit(limitEvidence))
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

            if (_ctx.EffectiveMaxSessionTokens is { } maxTok && (budgetKilled || rec.TokensTotal >= maxTok))
            {
                rec.Outcome = SessionOutcome.RolledOver;
                rec.ResultSummary = ExtractSessionResult(agent.ResultText, rec.Kind);
                if (kind == SessionKind.Audit && !_ctx.State.AuditedStages.Contains(stage.Id))
                    _ctx.State.AuditedStages.Add(stage.Id);
                // K1.1: the facts BEFORE the handoff hint, because BuildRolloverResumeHint reads the
                // tracker and the resume line should describe a session whose commits are already on
                // the record. Facts only — no attempt burned, no gate battery: still a rollover.
                _recordRolloverFacts(rec, stage, preTrack, startHead, ct);
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

    private int MaxAttempts(StageConfig stage) => StageSelection.MaxAttempts(_ctx.Plan, stage);


    // Activity tracking and out-of-repo write capture live in SessionRunner.Activity.cs.

    // ── static helpers ──

    // A Verify session's entire payload is the JSON verdict (score/findings/verdict), which can
    // legitimately run to several KB once a verifier lists five or six findings — nothing like the
    // "one paragraph" SESSION-RESULT: convention Deliver/Fix/Audit sessions follow. Truncating it to
    // the same 700 chars as a narrative summary cut the closing brace off a real, valid verdict JSON
    // (session #3, 2026-07-17) and Verifier.Parse had nothing left to match — a genuine PASS/WARN
    // got recorded as AgentError. Verify keeps the full text (generously capped, not narrative-cropped).
    internal const int VerifyResultMaxChars = 16_000;

    // K5.1: a Deliver/Fix/Audit result is parsed through the one contract and stored in its canonical
    // form — whole, with each field clipped on its own — instead of being cut blind at 700 characters
    // mid-word. A legacy or malformed result is not structured, and SessionResult.ToCanonical then
    // reproduces exactly the old cut, so nothing that used to be stored gets shorter.
    internal static string ExtractSessionResult(string? resultText, SessionKind kind)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return "";
        if (kind == SessionKind.Verify) return Trunc(resultText.Trim(), VerifyResultMaxChars);
        return SessionResult.Parse(resultText).ToCanonical();
    }

    private string LastRawTail(string rawLogPath)
    {
        try { return GateRunner.TailOf(File.ReadAllText(rawLogPath), 10); }
        catch (IOException) { return ""; }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;
}
