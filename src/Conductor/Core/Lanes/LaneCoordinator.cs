using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Lanes;

/// <summary>
/// Owns the read-only parallel-audit lane, the fix-lanes that consume closed followups, and the
/// bounded analysis-lane pool (B12.1/B12.2/P2). Extracted from Orchestrator (F-debt: ~350 lines of
/// lane-coordination glue scattered through the god-class, per AGENTS.md's Command/Query/Event
/// layering note — same seam ControlDispatcher was cut along in F5).
/// </summary>
public sealed class LaneCoordinator(PlanConfig plan, RunState state, IProgressSink sink, IEventSink events, Action<string> log)
{
    private LaneWorkerPool? _lanePool;
    private Task<ParallelAuditOutcome>? _parallelAuditTask;

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;

    // ---------------------------------------------------------------- P2: parallel audit lane

    /// <summary>P2: launches the parallel audit for <paramref name="audit"/> as a read-only lane in a
    /// detached git worktree at the pinned SHA. The audit agent reads code, produces findings,
    /// and cannot modify the real working tree.</summary>
    public void StartParallelAudit(PendingParallelAudit audit, CancellationToken ct)
    {
        var stageId = audit.StageId;
        var sha = audit.StageStartHead;
        if (string.IsNullOrEmpty(sha)) sha = Git.Head(plan.Repo);

        log($"parallel audit: launching read-only audit for stage {stageId} at {Short(sha)}");
        var prompt = BuildParallelAuditPrompt(stageId, sha);

        var resolvedAgent = plan.ResolveAgent(plan.Stages.FirstOrDefault(s => s.Id == stageId) ?? plan.Stages[^1]);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var lanePath = Path.Combine(Path.GetTempPath(), $"conductor-pa-{stageId}-{suffix}");

        _parallelAuditTask = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(lanePath);
                var createResult = Git.WorktreeAddDetached(plan.Repo, lanePath, sha);
                if (createResult.ExitCode != 0)
                {
                    log($"parallel audit: worktree creation failed — {createResult.Output.Trim()}");
                    CleanupLanePath(lanePath);
                    return new ParallelAuditOutcome { StageId = stageId, MaxSeverity = AuditFindingSeverity.None, Findings = "", Completed = true };
                }

                var promptPath = Path.Combine(lanePath, ".conductor-audit-prompt.md");
                await File.WriteAllTextAsync(promptPath, prompt, ct).ConfigureAwait(false);

                var args = resolvedAgent.Args.Select(a =>
                    a.Replace("{prompt}", prompt)
                     .Replace("{sessionId}", $"audit-{stageId}-{suffix}"));
                var result = await ProcessRunner.RunAsync(resolvedAgent.Command, args, lanePath,
                    TimeSpan.FromMinutes(plan.Audit?.MaxAttempts > 0 ? plan.Audit.MaxAttempts * 30 : 30), ct).ConfigureAwait(false);

                CleanupLanePath(lanePath);

                if (ct.IsCancellationRequested)
                {
                    log($"parallel audit for {stageId}: cancelled");
                    return new ParallelAuditOutcome { StageId = stageId, MaxSeverity = AuditFindingSeverity.None, Findings = "", Completed = true };
                }

                var findings = result.Output ?? "";
                var severity = ParseAuditSeverity(findings);
                log($"parallel audit for {stageId}: completed (severity={severity})");
                return new ParallelAuditOutcome { StageId = stageId, MaxSeverity = severity, Findings = findings, Completed = true };
            }
            catch (Exception ex)
            {
                CleanupLanePath(lanePath);
                log($"parallel audit for {stageId}: error — {ex.Message}");
                return new ParallelAuditOutcome { StageId = stageId, MaxSeverity = AuditFindingSeverity.None, Findings = "", Completed = true };
            }
        }, ct);

        state.PendingParallelAudit = null;
    }

    private static void CleanupLanePath(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>P2: builds the read-only audit prompt for the parallel audit lane.</summary>
    private string BuildParallelAuditPrompt(string stageId, string sha)
    {
        var stage = plan.Stages.FirstOrDefault(s => s.Id == stageId);
        var title = stage?.Title ?? stageId;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are a read-only code auditor for an autonomous engineering orchestrator (Conductor).");
        sb.AppendLine("You are running in a detached git worktree pinned to a specific commit — you can");
        sb.AppendLine("read files freely but you CANNOT modify them or create commits.");
        sb.AppendLine();
        sb.AppendLine("## Audit context");
        sb.AppendLine($"Plan: {plan.Name}");
        sb.AppendLine($"Stage: {stageId} ({title})");
        sb.AppendLine($"Pinned commit: {sha}");
        sb.AppendLine($"Tracker: {plan.Tracker}");
        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine($"Audit the code at commit {sha}. Read the tracker for this stage's deliverables.");
        sb.AppendLine("Look for:");
        sb.AppendLine("1. **Regressions** — did the stage accidentally break something that was working?");
        sb.AppendLine("2. **Missed edge cases** — did the implementation overlook error paths, nulls, timeouts?");
        sb.AppendLine("3. **Code quality gaps** — duplicated logic, inconsistent naming, missing null checks?");
        sb.AppendLine("4. **Tracker honesty** — do the claimed DONE checkpoints match the actual code changes?");
        sb.AppendLine();
        sb.AppendLine("## Output");
        sb.AppendLine("Produce a structured markdown report. Start each finding with a severity marker:");
        sb.AppendLine("- `HIGH:` — regression, security issue, or broken gate that must be fixed before continuing");
        sb.AppendLine("- `MEDIUM:` — missed edge case, technical debt that should be addressed");
        sb.AppendLine("- `LOW:` — style, minor improvement, nit");
        sb.AppendLine();
        sb.AppendLine("End with verdict on a single line starting with `AUDIT-VERDICT:` followed by one word:");
        sb.AppendLine("`PASS` (no significant issues), `WARN` (issues but safe to continue), or `FAIL` (must fix first).");
        return sb.ToString();
    }

    /// <summary>P2: parses the audit output for severity markers and returns the highest level found.</summary>
    private static AuditFindingSeverity ParseAuditSeverity(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return AuditFindingSeverity.None;
        var up = output.ToUpperInvariant();
        if (up.Contains("AUDIT-VERDICT: FAIL") || up.Contains("AUDIT_VERDICT: FAIL"))
            return AuditFindingSeverity.High;
        if (up.Contains("HIGH:") || up.Contains("**HIGH**"))
            return AuditFindingSeverity.High;
        if (up.Contains("AUDIT-VERDICT: WARN") || up.Contains("AUDIT_VERDICT: WARN") ||
            up.Contains("MEDIUM:") || up.Contains("**MEDIUM**"))
            return AuditFindingSeverity.Medium;
        if (up.Contains("LOW:") || up.Contains("**LOW**"))
            return AuditFindingSeverity.Low;
        return AuditFindingSeverity.None;
    }

    /// <summary>P2: polled during the deliver session to check if the parallel audit has completed.
    /// When it finishes, the findings are stored in state for prompt injection and post-session decision.</summary>
    public async Task CheckParallelAuditCompletionAsync()
    {
        if (_parallelAuditTask is not { IsCompleted: true }) return;
        try
        {
            var outcome = await _parallelAuditTask.ConfigureAwait(false);
            _parallelAuditTask = null;
            state.ParallelAuditOutcome = outcome;
            if (outcome.MaxSeverity == AuditFindingSeverity.High)
            {
                log($"parallel audit: HIGH findings detected for stage {outcome.StageId} — signal delivered to running session");
                sink.Log($"[parallel-audit] HIGH severity findings — audit recommends fixing before continuing");
            }
        }
        catch (Exception ex)
        {
            log($"parallel audit: task failed — {ex.Message}");
            _parallelAuditTask = null;
        }
    }

    // ---------------------------------------------------------------- B12.4: fix-lanes

    /// <summary>B12.4: fix-lanes run after a stage is confirmed — they consume
    /// <c>.conductor/followups.md</c> entries owned by this stage and run as Tier B mutating lanes
    /// behind merge gates.</summary>
    public async Task RunFollowupFixLanesAsync(string stageId, CancellationToken ct)
    {
        var followupsPath = Path.Combine(plan.StateDir, "followups.md");
        if (!File.Exists(followupsPath)) return;

        var entries = FollowupParser.ReadOpenForStage(followupsPath, stageId);
        if (entries.Count == 0) return;

        // Resolve the plan's default agent (per-lane overrides aren't used for fix-lanes yet).
        var agent = plan.Agent;
        log($"fix-lanes: {entries.Count} OPEN followup(s) owned by stage {stageId}");

        foreach (var entry in entries)
        {
            var lane = FollowupEntryToMutatingLane(entry);
            log($"fix-lane '{entry.Id}' starting — {entry.Item}");

            MutatingLaneResult result;
            try
            {
                result = await MutatingLaneRunner.RunAsync(
                    plan, lane, agent, stageId, events, log, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log($"fix-lane '{entry.Id}' threw: {ex.Message}");
                continue;
            }

            if (result.Merged || (result.IsSuccess && !result.AgentCommitted))
            {
                var commitRef = Git.Head(plan.Repo)[..Math.Min(7, Git.Head(plan.Repo).Length)];
                if (FollowupParser.UpdateStatus(followupsPath, entry.Id, "CLOSED", $"b{entry.Id}"))
                    log($"fix-lane '{entry.Id}' CLOSED — {entry.Item} ({commitRef})");
                else
                    log($"fix-lane '{entry.Id}' done but status update failed in followups.md");
            }
            else
            {
                log($"fix-lane '{entry.Id}' FAILED — merge gate rejected: {result.Error ?? "unknown"}");
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

    // ---------------------------------------------------------------- B12.1/B12.2: analysis lanes

    /// <summary>B12.2: Enqueue read-only analysis lanes for the current stage into the bounded
    /// worker pool. The pool respects <see cref="LimitsConfig.MaxConcurrentLanes"/> and emits
    /// <see cref="LaneStarted"/> / <see cref="LaneFinished"/> lifecycle events.
    /// Each lane runs in a scratch temp directory so it can never write the working tree.</summary>
    public void StartAnalysisLanes(StageConfig stage, string? handoff, CancellationToken ct)
    {
        if (plan.AnalysisLanes.Count == 0) return;

        var triggered = plan.AnalysisLanes
            .Where(l => l.Enabled && (l.StageTrigger == null ||
                l.StageTrigger.Equals(stage.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (triggered.Count == 0) return;

        _lanePool ??= new LaneWorkerPool(plan.Limits.MaxConcurrentLanes, events, log);

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
    public void PollLaneCompletion()
    {
        if (_lanePool == null || _lanePool.CompletedCount == 0) return;

        var results = _lanePool.DrainCompleted();
        foreach (var result in results)
        {
            if (result.IsSuccess)
                log($"analysis lane '{result.LaneId}' completed ({result.ElapsedMs}ms)" +
                    (result.ArtifactPath != null ? $" → {Path.GetFileName(result.ArtifactPath)}" : ""));
            else
                log($"analysis lane '{result.LaneId}' failed: {result.Error ?? "unknown error"}");
        }
    }

    /// <summary>B12.2: After the session ends, wait briefly for any remaining lanes, then
    /// collect their artifacts. The pool already emitted lifecycle events; we just log a summary.</summary>
    public async Task CollectLaneArtifactsAsync(string stageId, CancellationToken ct)
    {
        if (_lanePool == null || (_lanePool.ActiveCount == 0 && _lanePool.CompletedCount == 0)) return;

        var remaining = await _lanePool.WaitAllAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        var successCount = remaining.Count(r => r.IsSuccess);
        var failCount = remaining.Count - successCount;
        if (remaining.Count > 0)
            log($"analysis lanes collected: {successCount} succeeded, {failCount} failed for stage {stageId}");
    }
}
