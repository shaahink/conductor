using System.ComponentModel;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class StatusCommand : Command<StatusCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--since <DATETIME>")]
        [Description("Only show delta since the given UTC datetime (ISO 8601). Default: full report.")]
        public string? Since { get; init; }

        [CommandOption("--no-llm")]
        [Description("Skip the LLM analysis — show only tables (fast, offline).")]
        public bool NoLlm { get; init; }
    }

    private const int LogTailLines = 50;
    private const string InvocationsFile = "status-invocations.jsonl";

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);

        var totalDone = track.Checkpoints.Count(c => c.IsDone);
        var totalCk = track.Checkpoints.Count;

        // Read conductor.log tail
        var logPath = Path.Combine(plan.StateDir, "conductor.log");
        var logTail = "";
        if (File.Exists(logPath))
        {
            try { logTail = GateRunner.TailOf(File.ReadAllText(logPath), LogTailLines); }
            catch (IOException) { /* best-effort */ }
        }

        // Parse --since
        DateTime? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (DateTime.TryParse(settings.Since, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                sinceUtc = parsed;
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --since value: '{Markup.Escape(settings.Since)}'. Use ISO 8601 (e.g. 2026-07-09T12:00Z).[/]");
                return 1;
            }
        }

        // Build git summary
        var head = Git.Head(plan.Repo);
        var gitSummary = $"branch: {Git.Branch(plan.Repo)}\n" +
                         $"HEAD: {(head.Length > 7 ? head[..7] : head)}\n" +
                         $"recent commits:\n" + RecentCommits(plan.Repo, 5);

        // Show compact header
        var statusColor = state.Status switch
        {
            RunStatus.Idle or RunStatus.Completed => "green",
            RunStatus.Running or RunStatus.VerifyingGates => "yellow",
            RunStatus.Backoff => "orange1",
            RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner => "red",
            RunStatus.Aborted => "red",
            _ => "grey",
        };
        AnsiConsole.MarkupLine($"[bold aqua]Conductor[/] — [bold]{Markup.Escape(plan.Name)}[/] · [{statusColor}]{state.Status}[/]" +
                               (state.AttentionReason != null ? $" — [red]{Markup.Escape(state.AttentionReason)}[/]" : ""));
        AnsiConsole.MarkupLine($"repo {Markup.Escape(plan.Repo)} · branch {Markup.Escape(Git.Branch(plan.Repo))} · " +
                               $"checkpoints [bold]{totalDone}/{totalCk}[/] · " +
                               $"sessions {state.SessionCounter} · cost ${state.TotalCostUsd:0.00}");

        // Render stage overview table (always shown)
        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Stage"); t.AddColumn("Title"); t.AddColumn("Done"); t.AddColumn("State");
        foreach (var s in plan.Stages)
        {
            var rows = track.ForStage(s.Id).ToList();
            var done = rows.Count(r => r.IsDone);
            var st = state.SkippedStages.Contains(s.Id) ? "[red]skipped[/]"
                : rows.Count > 0 && done == rows.Count ? "[green]done[/]"
                : s.Id == state.CurrentStage ? "[yellow]active[/]" : "[grey]todo[/]";
            t.AddRow(Markup.Escape(s.Id), Markup.Escape(s.Title), $"{done}/{rows.Count}", st);
        }
        AnsiConsole.Write(t);

        // Render recent sessions table
        if (state.History.Count > 0)
        {
            var h = new Table().Border(TableBorder.Rounded).Title("recent sessions");
            h.AddColumn("#"); h.AddColumn("Stage"); h.AddColumn("Kind"); h.AddColumn("Outcome"); h.AddColumn("DONE"); h.AddColumn("Commits"); h.AddColumn("Gates");
            foreach (var r in state.History.TakeLast(10))
                h.AddRow(r.Number.ToString(), Markup.Escape(r.Stage), r.Kind.ToString(),
                         Markup.Escape(r.Outcome?.ToString() ?? "running"),
                         Markup.Escape(string.Join(" ", r.NewlyDone)),
                         r.NewCommits.Count.ToString(), Markup.Escape(r.GateSummary));
            AnsiConsole.Write(h);
        }

        // LLM analysis (unless --no-llm or statusAgent is disabled)
        var sc = plan.StatusAgent;
        if (settings.NoLlm || sc == null || !sc.Enabled)
        {
            if (settings.NoLlm)
                AnsiConsole.MarkupLine("[grey]LLM analysis skipped (--no-llm).[/]");
            else
                AnsiConsole.MarkupLine("[grey]LLM analysis disabled (statusAgent.enabled = false).[/]");
            RecordInvocation(plan.StateDir);
            return 0;
        }

        // Rate limit check
        if (sc.MaxPerHour > 0)
        {
            var recentCount = CountRecentInvocations(plan.StateDir, TimeSpan.FromHours(1));
            if (recentCount >= sc.MaxPerHour)
            {
                AnsiConsole.MarkupLine($"[red]Rate limited: {recentCount}/{sc.MaxPerHour} calls this hour.[/] Wait or increase statusAgent.maxPerHour in the plan.");
                return 1;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold aqua]Running LLM status analysis…[/]");

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, logTail, gitSummary, totalDone, totalCk, sinceUtc);
        var result = StatusAgent.Run(sc, prompt);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold aqua]── Status Report (LLM) ──[/]");
        AnsiConsole.WriteLine(result);
        AnsiConsole.MarkupLine("[bold aqua]──────────────────────────[/]");

        RecordInvocation(plan.StateDir);
        return 0;
    }

#pragma warning disable MA0045, CA1849 // short CLI-boundary helper, no concurrent async work to protect (same category as Spectre.Cli sync boundary)
    private static string RecentCommits(string repo, int count)
    {
        try
        {
            var output = ProcessRunner.Run("git", new[] { "log", $"-n{count}", "--oneline", "--no-decorate" }, repo,
                TimeSpan.FromSeconds(5)).Output;
            return string.IsNullOrWhiteSpace(output) ? "(no commits)" : output.Trim();
        }
        catch { return "(git failed)"; }
    }
#pragma warning restore MA0045, CA1849

#pragma warning disable MA0045 // sync file I/O at Spectre.Cli sync boundary (same pattern as RunCommand)
    private static void RecordInvocation(string stateDir)
    {
        try
        {
            var file = Path.Combine(stateDir, InvocationsFile);
            File.AppendAllText(file, DateTime.UtcNow.ToString("u") + Environment.NewLine);
        }
        catch (IOException) { /* best-effort */ }
    }

    private static int CountRecentInvocations(string stateDir, TimeSpan window)
    {
        try
        {
            var file = Path.Combine(stateDir, InvocationsFile);
            if (!File.Exists(file)) return 0;
            var cutoff = DateTime.UtcNow - window;
            var lines = File.ReadAllLines(file);
            return lines.Count(line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return false;
                return DateTime.TryParse(line, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dt) && dt >= cutoff;
            });
        }
        catch (IOException) { return 0; }
    }
#pragma warning restore MA0045
}
