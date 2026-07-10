using System.Text;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// F1.2: Generates TRACKER.md as a view FROM run.db (the source of truth). The checkpoint
/// table rows and handoff block are a projection of the database; everything else comes from
/// <see cref="PlanConfig"/>. Output is byte-stable for the same input data.
/// </summary>
public static class TrackerGenerator
{
    public static readonly UTF8Encoding Utf8Bom = Reporter.Utf8Bom;

    /// <summary>
    /// Generate the full TRACKER.md content. <paramref name="plan"/> provides stage definitions
    /// and structural header text; <paramref name="db"/> supplies runtime checkpoint rows and
    /// the most recent handover content; <paramref name="handoffFallback"/> is the template text
    /// to use when no handover exists in the database (e.g. first run before any session wrote one).
    /// </summary>
    public static string Generate(PlanConfig plan, RunDb db, string runId, string? handoffFallback = null)
    {
        var checkpoints = db.GetCheckpoints(runId);
        var handover = db.GetLatestHandover(runId);
        var branch = Git.Branch(plan.Repo);

        var sb = new StringBuilder();
        sb.AppendLine($"# {plan.Name} Phase Tracker");
        sb.AppendLine();
        sb.Append(plan.PromptExtra != null
            ? $"**Plan:** {plan.Name} | **Branch:** `{branch}` | **Design doc:** {plan.PlanDoc}{Environment.NewLine}{Environment.NewLine}"
            : $"**Branch:** `{branch}`{Environment.NewLine}{Environment.NewLine}");

        sb.AppendLine("## Handoff (overwrite this block, ≤12 lines, no history)");
        if (!string.IsNullOrWhiteSpace(handover))
        {
            sb.AppendLine();
            sb.AppendLine(handover);
            sb.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(handoffFallback))
        {
            sb.AppendLine();
            sb.AppendLine(handoffFallback);
            sb.AppendLine();
        }
        sb.AppendLine();

        // Baseline numbers — from DB (source of truth)
        var done = checkpoints.Count(c => c.Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine("## Baseline numbers (from run.db)");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Total checkpoints | {checkpoints.Count} |");
        sb.AppendLine($"| Done | {done} |");
        sb.AppendLine();

        sb.AppendLine("## Checkpoints");
        sb.AppendLine();
        sb.AppendLine("Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path produced by a run this" +
                       "\nphase (a code path is not evidence).");
        sb.AppendLine();

        if (checkpoints.Count == 0)
        {
            // No checkpoints in db yet — render from plan stages only
            foreach (var stage in plan.Stages)
            {
                sb.AppendLine($"### {stage.Id} — {stage.Title}");
                sb.AppendLine();
                sb.AppendLine("| # | Checkpoint | Status | Commit | Evidence |");
                sb.AppendLine("|---|-----------|--------|--------|----------|");
                sb.AppendLine($"| - | (no checkpoints seeded) | - | - | - |");
                sb.AppendLine();
            }
        }
        else
        {
            var cpsByStage = checkpoints
                .GroupBy(c => c.StageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Iterate stages in plan order, then any stages in db not in plan
            var renderedStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stage in plan.Stages)
            {
                renderedStages.Add(stage.Id);
                sb.AppendLine($"### {stage.Id} — {stage.Title}");
                sb.AppendLine();
                sb.AppendLine("| # | Checkpoint | Status | Commit | Evidence |");
                sb.AppendLine("|---|-----------|--------|--------|----------|");

                if (cpsByStage.TryGetValue(stage.Id, out var stageCheckpoints))
                {
                    foreach (var cp in stageCheckpoints)
                    {
                        var statusLabel = StatusLabel(cp.Status);
                        sb.AppendLine($"| {cp.Id} | {cp.Title} | {statusLabel} | {cp.Commit} | {cp.Evidence} |");
                    }
                }
                sb.AppendLine();
            }

            // Stages in db but not in plan (e.g. imported from a different tracker format)
            foreach (var (stageId, stageCheckpoints) in cpsByStage)
            {
                if (renderedStages.Contains(stageId)) continue;
                sb.AppendLine($"### {stageId}");
                sb.AppendLine();
                sb.AppendLine("| # | Checkpoint | Status | Commit | Evidence |");
                sb.AppendLine("|---|-----------|--------|--------|----------|");
                foreach (var cp in stageCheckpoints)
                {
                    var statusLabel = StatusLabel(cp.Status);
                    sb.AppendLine($"| {cp.Id} | {cp.Title} | {statusLabel} | {cp.Commit} | {cp.Evidence} |");
                }
                sb.AppendLine();
            }
        }

        // Dependencies — derived from plan stage DependsOn fields
        sb.AppendLine("## Dependencies");
        sb.AppendLine();
        var deps = plan.Stages
            .SelectMany(s => (s.DependsOn ?? []).Select(d => (From: d, To: s.Id)))
            .Distinct()
            .ToArray();
        if (deps.Length > 0)
        {
            sb.AppendLine("```");
            foreach (var (from, to) in deps)
                sb.AppendLine($"{from} → {to}");
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine("```");
            sb.AppendLine("(none — stages run sequentially by plan order)");
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    /// <summary>Write the generated tracker to disk.</summary>
    public static void Write(PlanConfig plan, RunDb db, string runId, string? handoffFallback = null)
    {
        var content = Generate(plan, db, runId, handoffFallback);
        File.WriteAllText(plan.TrackerPath, content, Utf8Bom);
    }

    private static string StatusLabel(string status) => (status ?? "").ToUpperInvariant() switch
    {
        "DONE" => "DONE",
        "IN PROGRESS" => "IN PROGRESS",
        "BLOCKED" => "BLOCKED",
        _ => "TODO",
    };
}
