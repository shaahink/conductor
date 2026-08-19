using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Accounting;
using Conductor.Core.Integrations;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// P5 — Post-hoc audit replay. Runs a read-only audit prompt against a completed stage,
/// capturing the output to <c>.conductor/audits/&lt;stage&gt;-replay-&lt;timestamp&gt;.md</c>.
/// Never modifies RunState. Use --replay to enable replay mode; the agent reviews the
/// stage's checkpoints, git history, evidence artifacts, and design context.
/// </summary>
public sealed class AuditCommand : Command<AuditCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<STAGE>")]
        [Description("Stage ID to replay-audit (e.g., D1, P4).")]
        public string Stage { get; init; } = "";

        [CommandOption("--replay")]
        [Description("Run as a read-only diagnostic audit replay — does not affect RunState.")]
        public bool Replay { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var stageId = settings.Stage.Trim();
        if (string.IsNullOrWhiteSpace(stageId))
        {
            AnsiConsole.MarkupLine("[red]A stage id is required.[/] Usage: conductor audit <STAGE> --replay");
            return 1;
        }

        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);

        // Find the stage config
        var stage = plan.Stages.Find(s => s.Id.Equals(stageId, StringComparison.OrdinalIgnoreCase));
        if (stage == null)
        {
            AnsiConsole.MarkupLine($"[red]Stage '{Markup.Escape(stageId)}' not found in the plan.[/]");
            return 1;
        }
        stageId = stage.Id; // canonical casing

        if (!settings.Replay)
        {
            AnsiConsole.MarkupLine("[yellow]Use --replay to run a post-hoc audit replay. Without --replay, this command is a no-op (regular audits are orchestrated, not CLI-driven).[/]");
            return 0;
        }

        // Gather stage context
        var rows = track.ForStage(stageId).ToList();
        var doneCount = rows.Count(r => r.IsDone);
        var totalCkForStage = rows.Count;

        // Git history: recent log bounded by this branch's scope
        var gitLog = "";
        try
        {
            var branch = Git.Branch(plan.Repo);
            var logResult = ProcessRunner.Run("git", new[] { "-C", plan.Repo, "log", "-n", "20", "--format=%h %s (%an, %ar)", "--no-decorate", "--no-merges" },
                plan.Repo, TimeSpan.FromSeconds(10));
            gitLog = string.IsNullOrWhiteSpace(logResult.Output) ? "(no commits)" : logResult.Output.Trim();
        }
        catch
        {
            gitLog = "(git failed)";
        }

        // Build evidence tail: read the stage's evidence files if any.
        //
        // This used to hardcode docs/era3/evidence/<stage> — Conductor's OWN third era — which meant
        // `conductor audit` went looking for a directory named after this project's history inside
        // whatever repo you pointed it at, and therefore found evidence for exactly one repo in the
        // world. The convention the tracker docs actually teach is docs/evidence/<stage>, so look
        // there, and in .conductor/evidence/<stage> for runs that keep evidence with the run state.
        var evidenceTail = "";
        var repoRoot = Path.Combine(plan.StateDir, "..");
        string[] evidenceDirs =
        [
            Path.Combine(repoRoot, "docs", "evidence", stageId),
            Path.Combine(plan.StateDir, "evidence", stageId),
        ];
        try
        {
            foreach (var evidenceDir in evidenceDirs)
            {
                if (!Directory.Exists(evidenceDir)) continue;
                var evidenceFiles = Directory.EnumerateFiles(evidenceDir, "*.txt", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .Take(3)
                    .ToList();
                foreach (var ef in evidenceFiles)
                {
                    var content = File.ReadAllText(ef);
                    if (content.Length > 4000) content = content[..4000] + "\n…(truncated)";
                    evidenceTail += $"## Evidence: {Path.GetFileName(ef)}\n```\n{content}\n```\n\n";
                }
                if (evidenceTail.Length > 0) break;   // first directory that actually has evidence wins
            }
        }
        catch (IOException) { /* best-effort */ }

        // Build the replay audit prompt
        var prompt = BuildReplayPrompt(plan.Name, stage, rows, doneCount, totalCkForStage, gitLog, evidenceTail, state.SessionCounter);
        AnsiConsole.MarkupLine($"[bold aqua]conductor audit replay[/] — stage [bold]{Markup.Escape(stageId)}[/] ({doneCount}/{totalCkForStage} checkpoints DONE)");
        AnsiConsole.MarkupLine($"[grey]Prompt length: {prompt.Length} chars. Running agent…[/]");
        AnsiConsole.WriteLine();

        // Run the agent (read-only, in a scratch dir)
        var result = RunAgent(plan.Agent, prompt, TimeSpan.FromMinutes(30));
        var outputDir = Path.Combine(plan.StateDir, "audits");
        Directory.CreateDirectory(outputDir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outputPath = Path.Combine(outputDir, $"{stageId}-replay-{timestamp}.md");
        File.WriteAllText(outputPath, result, System.Text.Encoding.UTF8);

        AnsiConsole.WriteLine(result);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Audit replay written to[/] [bold]{Markup.Escape(outputPath)}[/]");

        return 0;
    }

    private static string BuildReplayPrompt(string planName, StageConfig stage, List<CheckpointRow> rows,
        int doneCount, int totalCk, string gitLog, string evidenceTail, int sessionCounter)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are a post-hoc AUDIT REPLAY session for the \"{planName}\" mega plan.");
        sb.AppendLine();
        sb.AppendLine("The following stage is complete (all checkpoints DONE). Review what was built and");
        sb.AppendLine("provide an honest, critical post-hoc diagnostic assessment.");
        sb.AppendLine("Do NOT modify files or run commands. This is a READ-ONLY diagnostic.");
        sb.AppendLine();
        sb.AppendLine($"## Stage: {stage.Id} — {stage.Title}");
        if (!string.IsNullOrWhiteSpace(stage.Notes))
            sb.AppendLine($"Notes: {stage.Notes}");
        sb.AppendLine($"Checkpoints: {doneCount}/{totalCk} DONE");
        sb.AppendLine($"Total sessions across the entire plan so far: {sessionCounter}");
        sb.AppendLine();
        sb.AppendLine("### Checkpoints");
        foreach (var r in rows)
            sb.AppendLine($"- {r.Id} [{r.Status}] {r.Title}" +
                (r.Commit != null ? $" (commit: {r.Commit})" : "") +
                (r.Evidence != null ? $" Evidence: {r.Evidence}" : ""));
        sb.AppendLine();
        sb.AppendLine("### Recent Git History");
        sb.AppendLine("```");
        sb.AppendLine(gitLog);
        sb.AppendLine("```");
        sb.AppendLine();
        if (evidenceTail.Length > 0)
            sb.Append(evidenceTail);
        sb.AppendLine("## Instructions");
        sb.AppendLine("Write a comprehensive but terse diagnostic audit covering:");
        sb.AppendLine("1. **What was built** — factual summary of changes and deliverables.");
        sb.AppendLine("2. **Correctness** — bugs, risks, edge cases, regressions you spot.");
        sb.AppendLine("3. **Code quality** — patterns, conventions, duplication, maintainability.");
        sb.AppendLine("4. **Testing** — coverage assessment, gaps, brittle tests.");
        sb.AppendLine("5. **Risks and followups** — what could bite later, concrete improvements.");
        sb.AppendLine("6. **Verdict** — HONEST one-line assessment: SOLID / GOOD / ADEQUATE / WEAK.");
        sb.AppendLine();
        sb.AppendLine("Be critical. Don't oversell. If something looks thin, stubbed, or shortcut, say so plainly.");
        sb.AppendLine("End with a one-line verdict line starting with VERDICT:");
        return sb.ToString();
    }

    private static string RunAgent(AgentConfig cfg, string prompt, TimeSpan timeout)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "conductor-audit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var args = cfg.Args.Select(a => a.Replace("{prompt}", prompt)).ToList();
            // If a model arg exists, ensure it's set; defaults from plan config are fine.
            var r = ProcessRunner.Run(cfg.Command, args, scratch, timeout);
            // KS5.2: the replay is a full agent invocation and it says what it cost. Stated, not
            // recorded: `audit --replay` is an operator's question about a FINISHED stage, keyed to no
            // session and accruing against no live cap. See the exemption in ArchitectureBoundaryTests.
            var spend = BilledSpend.Read(cfg, SpendCategory.AuditReplay, r.Output, (long)r.Duration.TotalMilliseconds);
            AnsiConsole.MarkupLine(spend is null
                ? "[grey]audit agent: the provider reported no billed figure (unknown, not zero)[/]"
                : $"[grey]audit agent: ${spend.CostUsd:0.0000} billed, {spend.Tokens} tokens — not recorded against the run[/]");
            var text = r.Output.Trim();
            if (r.TimedOut) text += $"\n\n(audit agent timed out after {timeout.TotalMinutes:0} minutes)";
            if (!string.IsNullOrWhiteSpace(r.StdErr)) text += $"\n\n--- stderr ---\n{r.StdErr.Trim()}";
            return string.IsNullOrWhiteSpace(text)
                ? $"(audit agent produced no output — exit {r.ExitCode})"
                : text;
        }
        catch (Exception ex) { return $"(audit agent failed: {ex.Message})"; }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
