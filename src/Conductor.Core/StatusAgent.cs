using System.Text;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// On-demand progress reporter. Gathers live context (stage, checkpoints, gates, recent commits,
/// working tree, latest agent activity) into a single prompt and asks a read-only agent to
/// summarise status / risks / next steps. Runs in a scratch cwd with everything embedded in the
/// prompt, so it can never modify the working repo even if the model tries.
/// </summary>
public static class StatusAgent
{
    public static string BuildPrompt(DashboardSnapshot snap, string gitSummary,
        IReadOnlyList<string> recentAgent, IReadOnlyList<string> recentThinking)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a read-only status reporter for an autonomous multi-session engineering run.");
        sb.AppendLine("Do NOT edit files or run commands. Using ONLY the context below, write a concise, honest");
        sb.AppendLine("status report in markdown: (1) what's happening now, (2) real progress vs claimed,");
        sb.AppendLine("(3) risks/blockers, (4) what the next session should do. Be terse and specific.");
        sb.AppendLine();
        sb.AppendLine($"## Plan: {snap.PlanName} — status {snap.Status}");
        sb.AppendLine($"Stage {snap.StageId} ({snap.StageTitle}) · session #{snap.SessionNumber} {snap.SessionKind}" +
                      (snap.Attempt > 0 ? $" · attempt {snap.Attempt}/{snap.MaxAttempts}" : ""));
        sb.AppendLine($"Checkpoints {snap.DoneCount}/{snap.TotalCount} · working ▸ {snap.CurrentCheckpoint} {snap.CurrentCheckpointTitle}");
        if (snap.AttentionReason != null) sb.AppendLine($"Attention: {snap.AttentionReason}{Staleness.Since(snap.AttentionSinceUtc)}");
        sb.AppendLine();

        if (snap.StageOverview.Count > 0)
        {
            sb.AppendLine("### Stage overview");
            foreach (var (id, done, total, st) in snap.StageOverview)
                sb.AppendLine($"- {id}: {done}/{total} [{st}]");
            sb.AppendLine();
        }
        if (snap.StageCheckpoints.Count > 0)
        {
            sb.AppendLine($"### {snap.StageId} checkpoints");
            foreach (var (id, title, status) in snap.StageCheckpoints)
                sb.AppendLine($"- {id} [{status}] {title}");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(snap.GateSummary))
            sb.AppendLine($"### Gates\n{snap.GateSummary}\n");

        sb.AppendLine("### Git");
        sb.AppendLine("```");
        sb.AppendLine(gitSummary);
        sb.AppendLine("```");
        sb.AppendLine();

        if (recentThinking.Count > 0)
        {
            sb.AppendLine("### Latest agent thinking");
            foreach (var t in recentThinking.TakeLast(6)) sb.AppendLine($"- {t}");
            sb.AppendLine();
        }
        if (recentAgent.Count > 0)
        {
            sb.AppendLine("### Latest agent actions");
            foreach (var a in recentAgent.TakeLast(10)) sb.AppendLine($"- {a}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Build a CLI-status prompt from raw file data — used by <c>conductor status</c>
    /// when no live dashboard snapshot is available. Includes plan overview, state summary, log tail,
    /// tracker progress, and an optional since-cutoff for delta-only reports.</summary>
    public static string BuildCliPrompt(PlanConfig plan, RunState state, TrackerSnapshot track,
        string logTail, string gitSummary, int totalDone, int totalCk, DateTime? sinceUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a read-only status reporter for Conductor, an autonomous multi-session engineering orchestrator.");
        sb.AppendLine("Do NOT edit files or run commands. Using ONLY the context below, write a concise, honest");
        sb.AppendLine("status report in markdown. Structure:");
        sb.AppendLine("1. **Overall** — one sentence: what's happening, what phase/stage.");
        sb.AppendLine("2. **Progress** — which checkpoints are DONE vs TODO. Flag stalled/blocked stages.");
        sb.AppendLine("3. **Recent activity** — what the last few sessions did (from history + log tail).");
        sb.AppendLine("4. **Risks / blockers** — gates red, pending fixes, attention reasons, backoffs.");
        sb.AppendLine("5. **Next** — what the next session should do (which stage, which checkpoint).");
        sb.AppendLine();
        sb.AppendLine($"## Plan: {plan.Name}");
        sb.AppendLine($"Repo: {plan.Repo} · Branch: {Git.Branch(plan.Repo)}");
        sb.AppendLine($"Status: {state.Status} · Current stage: {state.CurrentStage ?? "(none)"}");
        sb.AppendLine($"Sessions: {state.SessionCounter} · Total cost: ${state.TotalCostUsd:0.00}");
        if (state.AttentionReason != null) sb.AppendLine($"Attention: {state.AttentionReason}{Staleness.Since(state.AttentionSinceUtc)}");
        if (sinceUtc.HasValue) sb.AppendLine($"Since: events after {sinceUtc.Value:u}");
        sb.AppendLine();

        sb.AppendLine("### Checkpoints");
        sb.AppendLine($"Total: {totalDone}/{totalCk} DONE");
        foreach (var s in plan.Stages)
        {
            var rows = track.ForStage(s.Id).ToList();
            var done = rows.Count(r => r.IsDone);
            var statusLabel = state.SkippedStages.Contains(s.Id) ? "skipped"
                : rows.Count > 0 && done == rows.Count ? "DONE"
                : s.Id == state.CurrentStage ? "active"
                : "todo";
            sb.AppendLine($"- {s.Id} [{statusLabel}] {s.Title}: {done}/{rows.Count}");
        }
        sb.AppendLine();

        if (state.PendingFix is { } fix)
            sb.AppendLine($"### Pending fix\nSession #{fix.FromSession} — gates failed: {fix.GateFailures}\n");
        if (state.PendingPhaseGate is { } pg)
            sb.AppendLine($"### Pending phase gate\nStage {pg.StageId} awaits full battery\n");

        if (state.History.Count > 0)
        {
            sb.AppendLine("### Recent sessions");
            foreach (var r in state.History.TakeLast(10))
                sb.AppendLine($"- #{r.Number} {r.Kind} {r.Stage} → {r.Outcome} | DONE: {string.Join(" ", r.NewlyDone)} | commits: {r.NewCommits.Count} | {r.GateSummary}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(logTail))
        {
            sb.AppendLine("### Log tail (last lines)");
            sb.AppendLine("```");
            sb.AppendLine(logTail);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("### Git");
        sb.AppendLine("```");
        sb.AppendLine(gitSummary);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("Now write the status report in markdown. Be terse. Flag problems clearly.");
        return sb.ToString();
    }

    /// <summary>Run the reporter in a scratch dir (read-only by construction). Returns its text output.</summary>
    /// <param name="onSpend">KS5.2 — what the reporter was billed, or null when its provider reported
    /// nothing. The status agent is spawned by an OPERATOR typing <c>conductor status --agent</c>, not
    /// by a run: there is no session to key a <c>costs</c> row to and the engine's budget counters live
    /// in another process. So this one states its bill to the person who asked for it rather than
    /// writing a row — see the exemption in <c>ArchitectureBoundaryTests</c>.</param>
    public static string Run(StatusAgentConfig cfg, string prompt, CancellationToken ct = default,
        Action<Accounting.SpendReceipt?>? onSpend = null)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "conductor-status-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var args = ResolveArgs(cfg, prompt);
            var r = ProcessRunner.Run(cfg.Command, args, scratch, TimeSpan.FromMinutes(cfg.TimeoutMinutes), ct);
            onSpend?.Invoke(Accounting.BilledSpend.ReadFromCommand(cfg.Command, "status", r.Output,
                (long)r.Duration.TotalMilliseconds));
            var text = r.Output.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? $"(status agent produced no output — exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")})"
                : text;
        }
        catch (Exception ex) { return $"(status agent failed: {ex.Message})"; }
        // Scratch cleanup is best-effort: a leftover temp dir (locked file) is harmless and reclaimed
        // by the OS; never let cleanup mask the agent result being returned.
        finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static IEnumerable<string> ResolveArgs(StatusAgentConfig cfg, string prompt)
    {
        var args = cfg.Args.Select(a => a.Replace("{prompt}", prompt)).ToList();
        if (!string.IsNullOrWhiteSpace(cfg.Model))
        {
            var mIdx = args.FindIndex(a => a == "-m" || a == "--model");
            if (mIdx >= 0 && mIdx + 1 < args.Count)
                args[mIdx + 1] = cfg.Model;
            else
            {
                args.Add("-m");
                args.Add(cfg.Model);
            }
        }
        return args;
    }
}
