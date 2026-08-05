using System.Text;
using Conductor.Core.Store;
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
    public static string Generate(PlanConfig plan, IRunStore db, string runId, string? handoffFallback = null)
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
        var done = checkpoints.Count(c => c.Confirmed);
        var claimed = checkpoints.Count(c => c.Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) && !c.Confirmed);
        sb.AppendLine("## Baseline numbers (from run.db)");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Total checkpoints | {checkpoints.Count} |");
        sb.AppendLine($"| Done | {done} |");
        if (claimed > 0)
            sb.AppendLine($"| Claimed (unconfirmed) | {claimed} |");
        sb.AppendLine();

        sb.AppendLine("## Checkpoints");
        sb.AppendLine();
        sb.AppendLine("Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this" +
                       "\nphase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.");
        sb.AppendLine();

        if (checkpoints.Count == 0)
        {
            // No checkpoints in db yet — render from plan stages only
            foreach (var stage in (plan.Stages ?? []))
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
            foreach (var stage in (plan.Stages ?? []))
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
                        var statusLabel = StatusLabel(cp.Status, cp.Confirmed);
                        sb.AppendLine($"| {cp.Id} | {cp.Title} | {statusLabel} | {cp.Commit} | {cp.Evidence} |");
                    }
                }
                sb.AppendLine();
            }

            // Stages in db but not in plan (e.g. imported from a different tracker format). LISTED,
            // never re-emitted as table rows. This file is generated FROM the graph and read back as
            // the declared-work list, so a row here is indistinguishable from a human declaration —
            // and W1.2 refuses to retire anything the declared source still declares. Rows therefore
            // made these items immortal: bug #32's seven face-showcase checkpoints outlived the plan
            // that created them, were re-declared by every regeneration, and failed doctor's G13 work
            // check forever. A bullet cannot match the row regex (it anchors on '|' —
            // ProgressConventions.BuildRowRegex), so the next sync sees the declaration gone, archives
            // them because their stage left the plan, and this section empties itself.
            var orphanStages = cpsByStage.Where(kv => !renderedStages.Contains(kv.Key)).ToList();
            if (orphanStages.Count > 0)
            {
                sb.AppendLine("### Not in the plan");
                sb.AppendLine();
                sb.AppendLine("Work the run's history carries under stages this plan no longer has. Listed, not declared —");
                sb.AppendLine("the next work-graph sync retires it and this section empties itself.");
                sb.AppendLine();
                foreach (var (stageId, stageCheckpoints) in orphanStages)
                    foreach (var cp in stageCheckpoints)
                        sb.AppendLine($"- {stageId} · {cp.Id} — {cp.Title} ({StatusLabel(cp.Status, cp.Confirmed)})");
                sb.AppendLine();
            }
        }

        // Dependencies — derived from plan stage DependsOn fields
        sb.AppendLine("## Dependencies");
        sb.AppendLine();
        var deps = (plan.Stages ?? [])
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
    public static void Write(PlanConfig plan, IRunStore db, string runId, string? handoffFallback = null)
    {
        var content = Generate(plan, db, runId, handoffFallback);
        File.WriteAllText(plan.TrackerPath, content, Utf8Bom);
    }

    private static string StatusLabel(string status, bool confirmed = false) => (status ?? "").ToUpperInvariant() switch
    {
        "DONE" when confirmed => "DONE ✓",
        "DONE" => "DONE",
        "IN PROGRESS" => "IN PROGRESS",
        "BLOCKED" => "BLOCKED",
        // SC5.3: a skipped card used to render as TODO — the view flatly contradicting the board.
        // The word is in the status vocabulary now, so the row still parses on the way back in.
        "SKIPPED" => "SKIPPED",
        _ => "TODO",
    };
}
