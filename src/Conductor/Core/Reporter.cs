using System.Text;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>Writes .conductor/REPORT.md and (optionally) commits+pushes it — the AFK progress view.</summary>
public static class Reporter
{
    // BOM so Windows PowerShell 5.1 / legacy tools read the em-dashes correctly
    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static string ReportPath(PlanConfig plan) => Path.Combine(plan.StateDir, "REPORT.md");

    public static string Build(PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<GateResult>? lastGates, string? liveActivity = null,
        IReadOnlyList<Timeline.TimelineEntry>? timeline = null, HealthMetrics.HealthReport? health = null,
        IReadOnlyList<Confidence.Entry>? confidence = null, McpMetrics.McpReport? mcp = null, RepoStrip.RepoInfo? repo = null)
    {
        var sb = new StringBuilder();
        var done = track.Checkpoints.Count(c => c.IsDone);
        var branch = Git.Branch(plan.Repo);
        var head = Git.Head(plan.Repo);
        var stage = plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage);

        sb.AppendLine($"# Conductor — {plan.Name} run report");
        sb.AppendLine();
        sb.AppendLine($"_Updated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · branch `{branch}` · HEAD `{Short(head)}`_");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {state.Status}{(state.AttentionReason != null ? $" — {state.AttentionReason}" : "")}");
        sb.AppendLine($"**Stage:** {state.CurrentStage ?? "-"}{(stage != null ? $" — {stage.Title}" : "")} · attempts used {state.AttemptsThisStage}" +
                      (NextCheckpoint(track, state.CurrentStage) is { } nc ? $" · working ▸ {nc}" : ""));
        sb.AppendLine($"**Checkpoints:** {done}/{track.Checkpoints.Count} done · **Sessions run:** {state.SessionCounter} · **Cost:** ${state.TotalCostUsd:0.0000}" +
                      (state.TotalTokensInput + state.TotalTokensOutput > 0
                          ? $" · **Tokens:** {state.TotalTokensInput:n0} in / {state.TotalTokensOutput:n0} out" + (state.TotalTokensReasoning > 0 ? $" / {state.TotalTokensReasoning:n0} think" : "")
                          : ""));
        if (state.ConfirmedStages.Count > 0)
            sb.AppendLine($"**Confirmed phases:** {string.Join(", ", state.ConfirmedStages)}");
        if (state.PendingPhaseGate != null)
            sb.AppendLine($"**Pending:** full-battery phase gate for {state.PendingPhaseGate.StageId}");
        if (state.PendingAudit != null)
            sb.AppendLine($"**Pending:** auto-fix audit for {state.PendingAudit.StageId}");
        if (state.SkippedStages.Count > 0)
            sb.AppendLine($"**⚠ Skipped stages (need human review):** {string.Join(", ", state.SkippedStages)}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(liveActivity))
        {
            sb.AppendLine("## Latest activity (live)");
            sb.AppendLine();
            sb.AppendLine(liveActivity);
            sb.AppendLine();
        }

        sb.AppendLine("## Stage progress");
        sb.AppendLine();
        sb.AppendLine("| Stage | Title | Done | State |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var s in plan.Stages)
        {
            var rows = track.ForStage(s.Id).ToList();
            var d = rows.Count(r => r.IsDone);
            var st = state.SkippedStages.Contains(s.Id) ? "SKIPPED ⚠"
                : state.ConfirmedStages.Contains(s.Id) ? "confirmed ✓"
                : rows.Count > 0 && d == rows.Count ? (plan.PerPhaseGates ? "gating…" : "done")
                : s.Id == state.CurrentStage ? "**← active**"
                : rows.Any(r => r.IsDone || r.IsInProgress) ? "partial"
                : "todo";
            sb.AppendLine($"| {s.Id} | {s.Title} | {d}/{rows.Count} | {st} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Sessions");
        sb.AppendLine();
        sb.AppendLine("| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Tokens |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var h in state.History.TakeLast(30))
        {
            var dur = h.EndedUtc.HasValue ? (h.EndedUtc.Value - h.StartedUtc).ToString(@"h\:mm") : "…";
            var att = h.Attempt > 0 ? h.Attempt.ToString() + (h.ResumeCount > 0 ? $"r{h.ResumeCount}" : "") : "";
            var toks = (h.TokensInput ?? 0) + (h.TokensOutput ?? 0) > 0 ? $"{h.TokensInput ?? 0:n0}/{h.TokensOutput ?? 0:n0}" : "";
            sb.AppendLine($"| {h.Number} | {h.Stage} | {h.Kind} | {att} | {h.StartedUtc:MM-dd HH:mm} | {dur} | {h.Outcome?.ToString() ?? "running"} | {string.Join(" ", h.NewlyDone)} | {h.NewCommits.Count} | {h.GateSummary} | {(h.CostUsd.HasValue ? "$" + h.CostUsd.Value.ToString("0.0000") : "")} | {toks} |");
        }
        sb.AppendLine();

        // Timeline (B5.1): state transitions with durations, folded from the event log. Every row here
        // derives from .conductor/events.jsonl — the single source (B5 trap: no parallel store).
        if (timeline is { Count: > 0 })
        {
            sb.AppendLine("## Timeline");
            sb.AppendLine();
            sb.AppendLine("_Transitions with duration, from the event log (`.conductor/events.jsonl`)._");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var e in timeline.TakeLast(40))
                sb.AppendLine(Timeline.Format(e));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Health (B5.3): execution-health signals folded from the same event log — retry rate plus any
        // same-failure loop / gate repetition / oscillation / context-saturation flags (B5 trap: a pure
        // fold, no parallel store). Conservative thresholds so a normal fix cycle never false-alarms.
        if (health is { Sessions: > 0 })
        {
            sb.AppendLine("## Health");
            sb.AppendLine();
            sb.AppendLine("_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var line in HealthMetrics.Format(health))
                sb.AppendLine(line);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Confidence (B5.4): evidence count per confirmed checkpoint — replaces bare "DONE" with a
        // metric the human/ingestor can audit (how many artifacts back each claim?).
        if (confidence is { Count: > 0 })
        {
            sb.AppendLine("## Confidence");
            sb.AppendLine();
            sb.AppendLine("_Evidence-based confidence per checkpoint. A checkpoint without evidence is marked (none)._");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var line in Confidence.Format(confidence))
                sb.AppendLine(line);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // MCP (B5.4): tool-call metrics folded from McpCallFinished events — total calls, success
        // rate, per-tool breakdown, average latency. Forward-looking: populates once B9 MCP events land.
        if (mcp is { TotalCalls: > 0 })
        {
            sb.AppendLine("## MCP");
            sb.AppendLine();
            sb.AppendLine("_Tool-call metrics from the event log (`.conductor/events.jsonl`)._");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var line in McpMetrics.Format(mcp))
                sb.AppendLine(line);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Repo (B5.4): live git snapshot — branch, HEAD, working-tree, ahead/behind vs upstream.
        if (repo != null)
        {
            sb.AppendLine("## Repo");
            sb.AppendLine();
            sb.AppendLine("_Live git snapshot (branch, working tree, sync vs upstream)._");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var line in RepoStrip.FormatStable(repo))
                sb.AppendLine(line);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        var withCommits = state.History.Where(h => h.NewCommits.Count > 0).TakeLast(8).ToList();
        if (withCommits.Count > 0)
        {
            sb.AppendLine("### Commits by session");
            sb.AppendLine();
            foreach (var h in withCommits)
            {
                sb.AppendLine($"- **s{h.Number} ({h.Stage} {h.Kind})** — {h.NewCommits.Count} commit(s):");
                foreach (var c in h.NewCommits.Take(12)) sb.AppendLine($"  - {c}");
            }
            sb.AppendLine();
        }

        // phase handovers written by audit sessions
        var handoverDir = Path.Combine(plan.StateDir, "handovers");
        if (Directory.Exists(handoverDir))
        {
            var files = Directory.GetFiles(handoverDir, "*.md").OrderBy(f => f).ToList();
            if (files.Count > 0)
            {
                sb.AppendLine("## Phase handovers (audit)");
                sb.AppendLine();
                foreach (var f in files)
                    sb.AppendLine($"- `.conductor/handovers/{Path.GetFileName(f)}`");
                sb.AppendLine();
            }
        }

        if (lastGates is { Count: > 0 })
        {
            sb.AppendLine("## Last gate run");
            sb.AppendLine();
            sb.AppendLine(GateRunner.Summary(lastGates));
            var failures = lastGates.Where(g => !g.Passed && !g.Skipped).ToList();
            foreach (var f in failures)
            {
                sb.AppendLine();
                sb.AppendLine($"<details><summary>{f.Name} — exit {f.ExitCode}</summary>");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(GateRunner.TailOf(f.Tail, 40));
                sb.AppendLine("```");
                sb.AppendLine("</details>");
            }
            sb.AppendLine();
        }

        var lastResult = state.History.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.ResultSummary))?.ResultSummary;
        if (!string.IsNullOrWhiteSpace(lastResult))
        {
            sb.AppendLine("## Last session result");
            sb.AppendLine();
            sb.AppendLine("> " + lastResult.Replace("\n", "\n> "));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(track.HandoffBlock))
        {
            sb.AppendLine("## Tracker handoff");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(track.HandoffBlock);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    public static void WriteAndPublish(PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<GateResult>? lastGates, Action<string> log, string? liveActivity = null, string? commitMessage = null)
    {
        string newContent;
        string path = ReportPath(plan);
        string? old;
        try
        {
            Directory.CreateDirectory(plan.StateDir);
            newContent = Build(plan, state, track, lastGates, liveActivity,
                ReadTimeline(plan), ReadHealth(plan), ReadConfidence(track), ReadMcpMetrics(plan), ReadRepoStrip(plan));
            old = File.Exists(path) ? File.ReadAllText(path) : null;
            File.WriteAllText(path, newContent, Utf8Bom);
        }
        catch (Exception ex)
        {
            log($"report write failed: {ex.Message}");
            return;
        }

        if (!plan.Report.Commit) return;
        // Skip no-op commits: if nothing but the timestamp changed, don't add to the git history
        // (this removes the duplicate "chore(conductor): … — Idle" commits).
        if (old != null && Normalize(old) == Normalize(newContent)) return;

        var rel = ".conductor/REPORT.md";
        var add = Git.Exec(plan.Repo, "add", "--force", rel);
        if (add.ExitCode != 0) { log($"report git add failed: {GateRunner.TailOf(add.Output, 3)}"); return; }
        var last = state.History.LastOrDefault();
        var msg = commitMessage ?? (last != null
            ? $"chore(conductor): s{last.Number} {last.Stage} {last.Outcome?.ToString() ?? "running"} — {state.Status}"
            : $"chore(conductor): {state.Status}");
        var commit = Git.Exec(plan.Repo, "commit", "-m", msg, "--", rel);
        // exit 1 with "nothing to commit" is fine
        if (commit.ExitCode == 0 && plan.Report.Push)
        {
            var push = Git.Exec(plan.Repo, "push");
            if (push.ExitCode != 0) log($"report push failed: {GateRunner.TailOf(push.Output, 3)}");
        }
    }

    /// <summary>Strip the volatile timestamp line so timestamp-only rewrites don't produce commits.</summary>
    private static string Normalize(string s)
        => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("_Updated ", StringComparison.Ordinal)));

    /// <summary>Fold the append-only event log into a timeline for the report, tolerating a missing or
    /// unreadable log (a run may not have emitted events yet, or the log may be locked mid-write) —
    /// the report renders without the Timeline section rather than failing (A15: no crash on I/O).</summary>
    public static IReadOnlyList<Timeline.TimelineEntry> ReadTimeline(PlanConfig plan)
    {
        try
        {
            var path = Path.Combine(plan.StateDir, "events.jsonl");
            return Timeline.Build(EventLog.ReadAll(path));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Fold the event log into replay/time-travel steps (B5.2) — every transition paired with
    /// the run state reconstructed as of that point. Same tolerant read as <see cref="ReadTimeline"/>
    /// (a run may not have emitted events yet, or the log may be locked mid-write).</summary>
    public static IReadOnlyList<Replay.ReplayStep> ReadReplay(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            var path = Path.Combine(plan.StateDir, "events.jsonl");
            return Replay.Build(EventLog.ReadAll(path));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Fold the event log into execution-health signals (B5.3) — retry rate plus any
    /// same-failure loop / gate repetition / oscillation / context-saturation flags. Same tolerant
    /// read as <see cref="ReadTimeline"/> (a run may not have emitted events yet, or the log may be
    /// locked mid-write) — the report/panel render nothing rather than failing (A15: no crash on I/O).</summary>
    public static HealthMetrics.HealthReport ReadHealth(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            var path = Path.Combine(plan.StateDir, "events.jsonl");
            return HealthMetrics.Compute(EventLog.ReadAll(path));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new HealthMetrics.HealthReport(0, 0, 0, []);
        }
    }

    /// <summary>Evidence-based confidence per checkpoint (B5.4) — derived from the tracker snapshot,
    /// not the event log (evidence paths are recorded in the tracker row's Evidence column).</summary>
    public static IReadOnlyList<Confidence.Entry> ReadConfidence(TrackerSnapshot track)
        => Confidence.Compute(track);

    /// <summary>Fold MCP tool-call events from the event log into call-count + latency metrics (B5.4).
    /// Tolerant read: returns an empty report when the log is missing/locked/empty (A15).</summary>
    public static McpMetrics.McpReport ReadMcpMetrics(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            var path = Path.Combine(plan.StateDir, "events.jsonl");
            return McpMetrics.Compute(EventLog.ReadAll(path));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new McpMetrics.McpReport(0, 0, 0, 0, 0, "", 0, []);
        }
    }

    /// <summary>Live git snapshot for the repo-awareness strip (B5.4) — branch, HEAD, dirty/ahead/behind.
    /// Not an event fold; this is a live query (B5 trap explicitly exempts it). Catches git I/O errors
    /// and returns a degraded snapshot rather than throwing (A15).</summary>
    public static RepoStrip.RepoInfo ReadRepoStrip(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RepoStrip.Compute(plan.Repo);
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private static string? NextCheckpoint(TrackerSnapshot track, string? stageId)
        => stageId == null ? null : track.ForStage(stageId).FirstOrDefault(c => !c.IsDone)?.Id;
}
