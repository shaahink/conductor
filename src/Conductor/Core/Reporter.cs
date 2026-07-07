using System.Text;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>Writes .conductor/REPORT.md and (optionally) commits+pushes it — the AFK progress view.</summary>
public static class Reporter
{
    // BOM so Windows PowerShell 5.1 / legacy tools read the em-dashes correctly
    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static string ReportPath(PlanConfig plan) => Path.Combine(plan.StateDir, "REPORT.md");

    public static string Build(PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<GateResult>? lastGates)
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
        sb.AppendLine($"**Stage:** {state.CurrentStage ?? "-"}{(stage != null ? $" — {stage.Title}" : "")} · attempts used {state.AttemptsThisStage}");
        sb.AppendLine($"**Checkpoints:** {done}/{track.Checkpoints.Count} done · **Sessions run:** {state.SessionCounter} · **Cost:** ${state.TotalCostUsd:0.00}");
        if (state.SkippedStages.Count > 0)
            sb.AppendLine($"**⚠ Skipped stages (need human review):** {string.Join(", ", state.SkippedStages)}");
        sb.AppendLine();

        sb.AppendLine("## Stage progress");
        sb.AppendLine();
        sb.AppendLine("| Stage | Title | Done | State |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var s in plan.Stages)
        {
            var rows = track.ForStage(s.Id).ToList();
            var d = rows.Count(r => r.IsDone);
            var st = state.SkippedStages.Contains(s.Id) ? "SKIPPED ⚠"
                : rows.Count > 0 && d == rows.Count ? "done"
                : s.Id == state.CurrentStage ? "**← active**"
                : rows.Any(r => r.IsDone || r.IsInProgress) ? "partial"
                : "todo";
            sb.AppendLine($"| {s.Id} | {s.Title} | {d}/{rows.Count} | {st} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Sessions");
        sb.AppendLine();
        sb.AppendLine("| # | Stage | Kind | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var h in state.History.TakeLast(30))
        {
            var dur = h.EndedUtc.HasValue ? (h.EndedUtc.Value - h.StartedUtc).ToString(@"h\:mm") : "…";
            sb.AppendLine($"| {h.Number} | {h.Stage} | {h.Kind} | {h.StartedUtc:MM-dd HH:mm} | {dur} | {h.Outcome?.ToString() ?? "running"} | {string.Join(" ", h.NewlyDone)} | {h.NewCommits.Count} | {h.GateSummary} | {(h.CostUsd.HasValue ? "$" + h.CostUsd.Value.ToString("0.00") : "")} |");
        }
        sb.AppendLine();

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

    public static void WriteAndPublish(PlanConfig plan, RunState state, TrackerSnapshot track, IReadOnlyList<GateResult>? lastGates, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(plan.StateDir);
            File.WriteAllText(ReportPath(plan), Build(plan, state, track, lastGates), Utf8Bom);
        }
        catch (Exception ex)
        {
            log($"report write failed: {ex.Message}");
            return;
        }

        if (!plan.Report.Commit) return;
        var rel = ".conductor/REPORT.md";
        var add = Git.Exec(plan.Repo, "add", "--force", rel);
        if (add.ExitCode != 0) { log($"report git add failed: {GateRunner.TailOf(add.Output, 3)}"); return; }
        var last = state.History.LastOrDefault();
        var msg = last != null
            ? $"chore(conductor): s{last.Number} {last.Stage} {last.Outcome?.ToString() ?? "running"} — {state.Status}"
            : $"chore(conductor): {state.Status}";
        var commit = Git.Exec(plan.Repo, "commit", "-m", msg, "--", rel);
        // exit 1 with "nothing to commit" is fine
        if (commit.ExitCode == 0 && plan.Report.Push)
        {
            var push = Git.Exec(plan.Repo, "push");
            if (push.ExitCode != 0) log($"report push failed: {GateRunner.TailOf(push.Output, 3)}");
        }
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
