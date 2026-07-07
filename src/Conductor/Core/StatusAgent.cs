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
        if (snap.AttentionReason != null) sb.AppendLine($"Attention: {snap.AttentionReason}");
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

    /// <summary>Run the reporter in a scratch dir (read-only by construction). Returns its text output.</summary>
    public static string Run(StatusAgentConfig cfg, string prompt, CancellationToken ct = default)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "conductor-status-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var args = cfg.Args.Select(a => a.Replace("{prompt}", prompt));
            var r = ProcessRunner.Run(cfg.Command, args, scratch, TimeSpan.FromMinutes(cfg.TimeoutMinutes), ct);
            var text = r.Output.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? $"(status agent produced no output — exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")})"
                : text;
        }
        catch (Exception ex) { return $"(status agent failed: {ex.Message})"; }
        finally { try { Directory.Delete(scratch, recursive: true); } catch { } }
    }
}
