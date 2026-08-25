using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>Writes .conductor/REPORT.md and (optionally) commits+pushes it — the AFK progress view.</summary>
public static partial class Reporter
{
    // BOM so Windows PowerShell 5.1 / legacy tools read the em-dashes correctly
    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static string ReportPath(PlanConfig plan) => Path.Combine(plan.StateDir, "REPORT.md");

    public static string Build(PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<GateResult>? lastGates, string? liveActivity = null,
        IReadOnlyList<Timeline.TimelineEntry>? timeline = null, HealthMetrics.HealthReport? health = null,
        McpMetrics.McpReport? mcp = null, RepoStrip.RepoInfo? repo = null, Money.MoneyRun? money = null)
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
        sb.AppendLine($"**Status:** {state.Status}{(state.AttentionReason != null ? $" — {state.AttentionReason}{Staleness.Since(state.AttentionSinceUtc)}" : "")}");
        var stagePersona = stage != null ? plan.ResolvePersona(stage) : null;
        sb.AppendLine($"**Stage:** {state.CurrentStage ?? "-"}{(stage != null ? $" — {stage.Title}" : "")}{(stagePersona != null ? $" · persona: {stagePersona}" : "")} · attempts used {state.AttemptsThisStage}" +
                      (NextCheckpoint(track, state.CurrentStage) is { } nc ? $" · working ▸ {nc}" : ""));
        sb.AppendLine($"**Checkpoints:** {done}/{track.Checkpoints.Count} done · **Sessions run:** {state.SessionCounter} · **Cost:** ${state.TotalCostUsd + state.TotalOverheadCostUsd:0.0000} (agent ${state.TotalCostUsd:0.0000} + gates ${state.TotalOverheadCostUsd:0.0000})" +
                      (state.TotalTokensInput + state.TotalTokensOutput > 0
                          ? $" · **Tokens:** {state.TotalTokensInput:n0} in / {state.TotalTokensOutput:n0} out" + (state.TotalTokensReasoning > 0 ? $" / {state.TotalTokensReasoning:n0} think" : "")
                          : ""));
        // SC5.1: a sleeping run must read as sleeping. Without this the report of a run waiting on a
        // rate-limit window is indistinguishable from one that has quietly stopped.
        if (state.BlockedUntilUtc is { } blockedUntil)
            sb.AppendLine($"**Waiting:** {Events.BlockedUntilRequest.Describe(new DateTimeOffset(blockedUntil, TimeSpan.Zero), state.BlockedReason)}{Staleness.Since(state.BlockedSinceUtc)}");
        if (state.ConfirmedStages.Count > 0)
            sb.AppendLine($"**Confirmed phases:** {string.Join(", ", state.ConfirmedStages)}");
        if (state.PendingPhaseGate != null)
            sb.AppendLine($"**Pending:** full-battery phase gate for {state.PendingPhaseGate.StageId}");
        if (state.PendingAudit != null)
            sb.AppendLine($"**Pending:** auto-fix audit for {state.PendingAudit.StageId}");
        if (state.SkippedStages.Count > 0)
            sb.AppendLine($"**⚠ Skipped stages (need human review):** {string.Join(", ", state.SkippedStages)}");
        AppendChannels(sb, plan);
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
        sb.AppendLine("| Stage | Title | Progress | State |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var s in plan.Stages)
        {
            var rows = track.ForStage(s.Id).ToList();
            var d = rows.Count(r => r.IsDone);
            var bar = ProgressBar(d, rows.Count);
            var st = state.SkippedStages.Contains(s.Id) ? "SKIPPED ⚠"
                : state.ConfirmedStages.Contains(s.Id) ? "confirmed ✓"
                : rows.Count > 0 && d == rows.Count ? (plan.PerPhaseGates ? "gating…" : "done")
                : s.Id == state.CurrentStage ? "**← active**"
                : rows.Any(r => r.IsDone || r.IsInProgress) ? "partial"
                : "todo";
            var depth = SnapshotBuilder.ComputeDepth(s.Id, plan.Stages);
            var indent = new string(' ', depth * 2);
            sb.AppendLine($"| {indent}{s.Id} | {indent}{s.Title} | {bar} {d}/{rows.Count} | {st} |");
        }
        sb.AppendLine();

        // Collapsible per-stage checkpoint details
        foreach (var s in plan.Stages)
        {
            var rows = track.ForStage(s.Id).ToList();
            if (rows.Count == 0) continue;
            var d = rows.Count(r => r.IsDone);
            sb.AppendLine($"<details>{(d == rows.Count ? " ✅" : "")}<summary>{s.Id} — {s.Title} ({d}/{rows.Count})</summary>");
            sb.AppendLine();
            sb.AppendLine("| # | Title | Status | Commit |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in rows)
            {
                var statusIcon = r.IsDone ? "✅ DONE" : r.IsInProgress ? "🔄 IN PROGRESS" : r.IsBlocked ? "🚫 BLOCKED" : "⬜ TODO";
                var commitLink = FormatCommitLink(plan, r.Commit);
                sb.AppendLine($"| {r.Id} | {r.Title} | {statusIcon} | {commitLink} |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        sb.AppendLine("## Sessions");
        sb.AppendLine();
        sb.AppendLine("| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var h in state.History.TakeLast(30))
        {
            var dur = h.EndedUtc.HasValue ? (h.EndedUtc.Value - h.StartedUtc).ToString(@"h\:mm") : "…";
            var att = h.Attempt > 0 ? h.Attempt.ToString() + (h.ResumeCount > 0 ? $"r{h.ResumeCount}" : "") : "";
            var toks = (h.TokensInput ?? 0) + (h.TokensOutput ?? 0) > 0 ? $"{h.TokensInput ?? 0:n0}/{h.TokensOutput ?? 0:n0}" : "";
            var overhead = h.OverheadCostUsd.HasValue && h.OverheadCostUsd.Value > 0 ? "$" + h.OverheadCostUsd.Value.ToString("0.0000") : "";
            sb.AppendLine($"| {h.Number} | {h.Stage} | {h.Kind} | {att} | {h.StartedUtc:MM-dd HH:mm} | {dur} | {h.Outcome?.ToString() ?? "running"} | {string.Join(" ", h.NewlyDone)} | {h.NewCommits.Count} | {h.GateSummary} | {(h.CostUsd.HasValue ? "$" + h.CostUsd.Value.ToString("0.0000") : "")} | {overhead} | {toks} |");
        }
        sb.AppendLine();

        // Money (K4.3): the same rows `conductor money` prints, from the same analyzer — the report and
        // the verb must not be able to disagree about what a checkpoint cost. Billed dollars and
        // recorded tokens only; the engine has no price table by design.
        MoneySection.Append(sb, money);

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

        // SC4.3: a session that delivered in a declared satelliteRepo landed nothing here, and this
        // section used to skip it entirely — the exact reading that made sk #3's delivered stage look
        // like two sessions of nothing. Satellite commits are listed and labelled as such; the
        // per-session count stays the count for THIS repo, so neither number quietly means the other.
        var withCommits = state.History
            .Where(h => h.NewCommits.Count > 0 || h.SatelliteCommits.Count > 0).TakeLast(8).ToList();
        if (withCommits.Count > 0)
        {
            sb.AppendLine("### Commits by session");
            sb.AppendLine();
            foreach (var h in withCommits)
            {
                var satNote = h.SatelliteCommits.Count > 0 ? $" (+{h.SatelliteCommits.Count} in satellite repo(s))" : "";
                sb.AppendLine($"- **s{h.Number} ({h.Stage} {h.Kind})** — {h.NewCommits.Count} commit(s){satNote}:");
                foreach (var c in h.NewCommits.Take(12))
                {
                    var sha = c.Split(' ')[0];
                    var link = FormatCommitLink(plan, sha);
                    sb.AppendLine($"  - {link} {c[(sha.Length)..].TrimStart()}");
                }
                // Satellite shas belong to another repo — never link them against this repo's remote.
                foreach (var c in h.SatelliteCommits.Take(12))
                    sb.AppendLine($"  - `{c.Split(' ')[0]}` {c[Math.Min(c.Length, c.Split(' ')[0].Length)..].TrimStart()}");
            }
            sb.AppendLine();
        }

        // phase handovers written by audit sessions
        var handoverDir = Path.Combine(plan.StateDir, "handovers");
        if (Directory.Exists(handoverDir))
        {
            var files = Directory.GetFiles(handoverDir, "*.md").OrderBy(f => f, StringComparer.Ordinal).ToList();
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
            var failures = lastGates.Where(g => (!g.Passed || g.HasClassFailure) && !g.Skipped).ToList();
            foreach (var f in failures)
            {
                // KS4.2/KS4.3: a class failure has no useful tail — its command reported success —
                // so the report shows the class's own finding instead of forty lines of "PASS".
                var body = GateRunner.ClassDetail(f) ?? $"```\n{GateRunner.TailOf(f.Tail, 40)}\n```";
                sb.AppendLine();
                sb.AppendLine($"<details><summary>{f.Name} — {(f.HasClassFailure ? f.Glyph : "exit " + f.ExitCode)}</summary>");
                sb.AppendLine();
                sb.AppendLine(body);
                sb.AppendLine("</details>");
            }
            sb.AppendLine();
        }

        var lastResult = state.History.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.ResultSummary))?.ResultSummary;
        if (!string.IsNullOrWhiteSpace(lastResult))
        {
            sb.AppendLine("## Last session result");
            sb.AppendLine();
            // K5.1: a structured result renders as its fields; anything else keeps the old blockquote.
            sb.AppendLine(SessionResult.Parse(lastResult).ToMarkdown());
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

    /// <param name="onNewOwnerItems">SF4.2 — forwarded to <see cref="OwnerQueue.Write"/>: the report
    /// write path IS the run's session boundary, so it is where a newly-arrived owner obligation is
    /// noticed and pushed. See <c>RunLoop.NotifyNewOwnerQueueItems</c>.</param>
    public static void WriteAndPublish(PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<GateResult>? lastGates, Action<string> log, string? liveActivity = null, string? commitMessage = null, IRunStore? store = null, Action<IReadOnlyList<OwnerQueueItem>>? onNewOwnerItems = null)
    {
        string newContent;
        string path = ReportPath(plan);
        string? old;
        try
        {
            Directory.CreateDirectory(plan.StateDir);
            newContent = Build(plan, state, track, lastGates, liveActivity,
                ReadTimeline(store, state.RunId), ReadHealth(store, state.RunId), ReadMcpMetrics(store, state.RunId),
                ReadRepoStrip(plan), MoneySection.Read(plan, state.RunId));
            old = File.Exists(path) ? File.ReadAllText(path) : null;
            File.WriteAllText(path, newContent, Utf8Bom);
            // SF4.1: the owner queue rides the report's write path, which is the run's session
            // boundary. It is deliberately NOT committed with the report — it is derived state that
            // goes stale the instant an approval lands, and a committed copy would be a second
            // source of truth to disbelieve.
            OwnerQueue.Write(plan, state, track, log, onNewItems: onNewOwnerItems);
        }
        catch (Exception ex)
        {
            log($"report write failed: {ex.Message}");
            return;
        }

        if (!plan.Report.Commit) return;
        // Skip no-op commits: if nothing but the timestamp changed, don't add to the git history
        if (old != null && Normalize(old) == Normalize(newContent)) return;

        // SC6.1: and skip commits that carry no WORK either. Everything above this line still ran —
        // the report on disk is current — but a rewrite whose only news is the engine's own status,
        // attention sentence, timeline or cost is not history. devcontext #14 watched three such
        // commits land in eight minutes, two of them four seconds apart.
        var substance = ReportSubstance.Of(state, track);
        if (state.LastReportSubstance == substance) return;

        var rel = ".conductor/REPORT.md";
        var add = Git.Exec(plan.Repo, "add", "--force", rel);
        if (add.ExitCode != 0) { log($"report git add failed: {GateRunner.TailOf(add.Output, 3)}"); return; }
        var last = state.History.LastOrDefault();
        var msg = commitMessage ?? (last != null
            ? $"chore(conductor): s{last.Number} {last.Stage} {last.Outcome?.ToString() ?? "running"} — {state.Status}"
            : $"chore(conductor): {state.Status}");

        // SC6.1 coalescing: if the previous bookkeeping commit is still the tip, fold this one into it
        // rather than stacking a near-identical subject beside it. The pathspec keeps the amend to the
        // report alone, so work the agent has already staged is neither swept in nor disturbed.
        var amend = CanAmendReportCommit(plan, state, rel);
        var commit = amend
            ? Git.Exec(plan.Repo, "commit", "--amend", "-m", msg, "--", rel)
            : Git.Exec(plan.Repo, "commit", "-m", msg, "--", rel);
        // exit 1 with "nothing to commit" is fine
        if (commit.ExitCode != 0) return;
        state.LastReportSubstance = substance;
        state.LastReportCommitSha = Git.Head(plan.Repo);
        if (plan.Report.Push)
        {
            // A plain push, still — an amend only ever happens to a commit the upstream has not seen
            // (see CanAmendReportCommit), so this never needs to become a force.
            var push = Git.Exec(plan.Repo, "push");
            if (push.ExitCode != 0) log($"report push failed: {push.FailureReason()}");   // #66: git writes refusals to STDERR
        }
    }

    /// <summary>SC6.1: true when the last report commit this run made is still exactly HEAD, still
    /// touches nothing but the report, and has not left this machine. All three matter — a sha that is
    /// no longer HEAD means someone else's commit (the agent's, or a rebase's) sits on top and amending
    /// would rewrite THAT; a tip already on the upstream means an amend turns every later push into a
    /// force. No upstream at all is safe, and is the common case for a scratch or local-only branch.
    /// <para>The name check is deliberately re-read from git rather than trusted from the sha alone:
    /// an agent session can be committing concurrently, and re-asking git what the tip actually
    /// contains is the last thing this does before handing the amend over.</para></summary>
    private static bool CanAmendReportCommit(PlanConfig plan, RunState state, string rel)
    {
        if (string.IsNullOrWhiteSpace(state.LastReportCommitSha)) return false;
        if (!string.Equals(Git.Head(plan.Repo), state.LastReportCommitSha, StringComparison.OrdinalIgnoreCase))
            return false;
        var ab = Git.AheadBehind(plan.Repo);
        if (ab is { Ahead: 0 }) return false;
        var touched = Git.Exec(plan.Repo, "show", "--name-only", "--format=", "HEAD").Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return touched.Count == 1 && touched[0].Equals(rel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Write REPORT.md to disk only — no git commit, no push. Used for mid-session
    /// report refreshes (the report lives in .conductor/, not on the feature branch).</summary>
    public static void WriteReport(PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<GateResult>? lastGates, Action<string> log, string? liveActivity = null, IRunStore? store = null, Action<IReadOnlyList<OwnerQueueItem>>? onNewOwnerItems = null)
    {
        try
        {
            Directory.CreateDirectory(plan.StateDir);
            var content = Build(plan, state, track, lastGates, liveActivity,
                ReadTimeline(store, state.RunId), ReadHealth(store, state.RunId), ReadMcpMetrics(store, state.RunId),
                ReadRepoStrip(plan), MoneySection.Read(plan, state.RunId));
            File.WriteAllText(ReportPath(plan), content, Utf8Bom);
            OwnerQueue.Write(plan, state, track, log, onNewItems: onNewOwnerItems);   // SF4.1 — see WriteAndPublish
        }
        catch (Exception ex)
        {
            log($"report write failed: {ex.Message}");
        }
    }

    /// <summary>Strip the volatile timestamp line so timestamp-only rewrites don't produce commits.</summary>
    private static string Normalize(string s)
        => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("_Updated ", StringComparison.Ordinal)));

    /// <summary>Fold the append-only event log into a timeline for the report. When no store is
    /// available (e.g. standalone report generation) returns an empty list, same tolerance as the
    /// old file-based read.</summary>
    public static IReadOnlyList<Timeline.TimelineEntry> ReadTimeline(IRunStore? store, string runId)
    {
        try
        {
            if (store == null || string.IsNullOrEmpty(runId)) return [];
            return Timeline.Build(store.ReadAllEvents(runId));
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
    public static HealthMetrics.HealthReport ReadHealth(IRunStore? store, string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        try
        {
            if (store == null || string.IsNullOrEmpty(runId))
                return new HealthMetrics.HealthReport(0, 0, 0, []);
            return HealthMetrics.Compute(store.ReadAllEvents(runId));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new HealthMetrics.HealthReport(0, 0, 0, []);
        }
    }

    /// <summary>Fold MCP tool-call events from the event log into call-count + latency metrics (B5.4).
    /// Tolerant read: returns an empty report when the store is unavailable or the log is empty (A15).</summary>
    public static McpMetrics.McpReport ReadMcpMetrics(IRunStore? store, string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        try
        {
            if (store == null || string.IsNullOrEmpty(runId))
                return new McpMetrics.McpReport(0, 0, 0, 0, 0, "", 0, []);
            return McpMetrics.Compute(store.ReadAllEvents(runId));
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

    /// <summary>Unicode progress bar: █ for done, ░ for remaining, 10 chars wide.</summary>
    private static string ProgressBar(int done, int total)
    {
        if (total <= 0) return "";
        const int width = 10;
        var filled = (int)Math.Round((double)done / total * width);
        return new string('█', Math.Min(filled, width)) + new string('░', width - filled);
    }

    /// <summary>Format a commit SHA as a link to the remote if a GitHub/remote URL is available,
    /// otherwise just the short SHA.</summary>
    private static string FormatCommitLink(PlanConfig plan, string commit)
    {
        var sha = Short(commit);
        if (string.IsNullOrWhiteSpace(commit) || commit == "-" || commit == "?") return sha;
        var remote = RemoteUrl(plan.Repo);
        if (remote == null) return $"`{sha}`";
        return $"[`{sha}`]({remote}/commit/{commit})";
    }

    private static string? _cachedRemoteUrl;
    private static string? _cachedRemoteRepo;
    private static readonly Lock _remoteUrlLock = new();

    /// <summary>K5.4: internal, not private — the notification path needs the same remote, and a
    /// second implementation would be a second thing to get wrong.</summary>
    internal static string? RemoteUrl(string repo)
    {
        lock (_remoteUrlLock)
        {
            if (_cachedRemoteRepo == repo && _cachedRemoteUrl != null) return _cachedRemoteUrl;
        }
        try
        {
            var result = Git.Exec(repo, "remote", "get-url", "origin");
            if (result.ExitCode != 0) return null;
            var raw = result.Output.Trim();
            // KS9.1: the git@/https normalisation moved to GithubIdentity so the mirror derives
            // owner/repo from the SAME rule this link uses, without a second `git remote get-url`
            // and without a second opinion about what a remote URL means. The shelling-out and the
            // cache stay here. A URL it cannot normalise (a local path remote) falls back to the raw
            // string, which is what this method has always returned for one.
            var url = Integrations.Github.GithubIdentity.NormaliseRemoteUrl(raw) ?? raw;
            lock (_remoteUrlLock)
            {
                _cachedRemoteUrl = url;
                _cachedRemoteRepo = repo;
            }
            return url;
        }
        catch { return null; }
    }
}
