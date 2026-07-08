using System.ComponentModel;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Models;
using Conductor.Ui;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

public class PlanSettings : CommandSettings
{
    [CommandOption("-p|--plan <PLAN>")]
    [Description("Path to the plan JSON. Falls back to CONDUCTOR_PLAN env var, then ./conductor.plan.json")]
    public string? Plan { get; init; }

    public string ResolvePlanPath()
    {
        var p = Plan
                ?? Environment.GetEnvironmentVariable("CONDUCTOR_PLAN")
                ?? (File.Exists("conductor.plan.json") ? "conductor.plan.json" : null);
        if (p == null)
            throw new InvalidOperationException("No plan given. Use --plan <path>, set CONDUCTOR_PLAN, or place conductor.plan.json in the cwd.");
        return p;
    }
}

public sealed class RunCommand : Command<RunCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--dry-run")]
        [Description("Print the next session's prompt and exit — nothing is spawned.")]
        public bool DryRun { get; init; }

        [CommandOption("--once")]
        [Description("Run exactly one session, then stop.")]
        public bool Once { get; init; }

        [CommandOption("--max-sessions <N>")]
        [Description("Stop after N sessions this run (0 = unlimited).")]
        public int MaxSessions { get; init; }

        [CommandOption("--no-dashboard")]
        [Description("Plain line output instead of the live dashboard.")]
        public bool NoDashboard { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        if (string.IsNullOrEmpty(state.RunId)) state.RunId = Guid.NewGuid().ToString("N");

        var opts = new RunOptions(settings.DryRun, settings.Once, settings.MaxSessions);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Event log is additive alongside state.json (B2.1). A dry-run previews without writing.
        IEventSink events = settings.DryRun
            ? NullEventSink.Instance
            : new EventLog(Path.Combine(plan.StateDir, "events.jsonl"), state.RunId);
        try
        {
            var usePlain = settings.NoDashboard || settings.DryRun || Console.IsOutputRedirected;
            if (usePlain)
            {
                // Host = composition + structured-logging root (B2.5). The console sink is on for plain
                // runs (no TUI to corrupt); options are validated on start inside Build.
                using var host = ConductorHost.Build(plan, state, statePath, new PlainSink(), events, opts, consoleSink: true);
                return host.Services.GetRequiredService<Orchestrator>().Run(cts.Token);
            }

            var dash = new LiveDashboard(plan);
            // Dashboard owns stdout, so the Serilog console sink is disabled here (file sink only).
            using var dashHost = ConductorHost.Build(plan, state, statePath, dash, events, opts, consoleSink: false);
            var orchestrator = dashHost.Services.GetRequiredService<Orchestrator>();
            var task = Task.Run(() => orchestrator.Run(cts.Token));
            dash.RunUiLoop(task);
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            (events as IDisposable)?.Dispose();
        }
    }
}

public sealed class StatusCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);

        AnsiConsole.MarkupLine($"[bold aqua]Conductor[/] — [bold]{Markup.Escape(plan.Name)}[/] · status [bold]{state.Status}[/]" +
                               (state.AttentionReason != null ? $" — [red]{Markup.Escape(state.AttentionReason)}[/]" : ""));
        AnsiConsole.MarkupLine($"repo {Markup.Escape(plan.Repo)} · branch {Markup.Escape(Git.Branch(plan.Repo))} · " +
                               $"checkpoints [bold]{track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count}[/] · " +
                               $"sessions {state.SessionCounter} · cost ${state.TotalCostUsd:0.00}");

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
        return 0;
    }
}

/// <summary>
/// B9.5 — CLI task view. Reads <c>events.jsonl</c>, folds it through <see cref="TaskGraph"/>,
/// and renders sub-tasks per checkpoint as a Spectre table with status indicators.
/// </summary>
public sealed class TasksCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var eventsPath = Path.Combine(plan.StateDir, "events.jsonl");
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);

        var graph = new TaskGraph();
        if (File.Exists(eventsPath))
            graph.Fold(EventLog.ReadAll(eventsPath));

        if (graph.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no tasks recorded yet.[/] run the planner or agent to populate the task graph.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold aqua]Conductor[/] — [bold]{Markup.Escape(plan.Name)}[/] · task graph · {graph.Count} tasks");
        AnsiConsole.WriteLine();

        var checkpoints = graph.Tasks
            .GroupBy(t => t.CheckpointId)
            .OrderBy(g => g.Key);

        foreach (var ck in checkpoints)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]{Markup.Escape(ck.Key)}[/]")
                .AddColumn("Status")
                .AddColumn("Title")
                .AddColumn("Source");

            foreach (var task in ck.OrderBy(t => t.Order))
            {
                var icon = task.Status switch
                {
                    "done" => "[green]DONE[/]",
                    "in_progress" => "[yellow]▶ ACTV[/]",
                    "skipped" => "[red]SKIP[/]",
                    _ => "[grey]TODO[/]",
                };
                var source = task.Source switch
                {
                    "planner" => "[grey]planner[/]",
                    "agent" => "[grey]agent[/]",
                    "human" => "[grey]human[/]",
                    _ => $"[grey]{Markup.Escape(task.Source)}[/]",
                };
                table.AddRow(icon, Markup.Escape(task.Title), source);
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        return 0;
    }
}

public sealed class ReportCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Reporter.ReportPath(plan), Reporter.Build(plan, state, track, null, null,
            Reporter.ReadTimeline(plan), Reporter.ReadHealth(plan),
            confidence: Reporter.ReadConfidence(track), mcp: Reporter.ReadMcpMetrics(plan),
            repo: Reporter.ReadRepoStrip(plan)), Reporter.Utf8Bom);
        AnsiConsole.MarkupLine($"report written to [bold]{Markup.Escape(Reporter.ReportPath(plan))}[/]");
        return 0;
    }
}

/// <summary>
/// B5.2 — replay / time-travel viewer. Reconstructs a past run from its append-only
/// <c>events.jsonl</c>, printing every transition in order with the run state reconstructed as of
/// that moment (the same fold the TUI <c>F8</c> modal renders). The source is an explicit path to an
/// <c>events.jsonl</c> (or a repo/dir containing <c>.conductor/events.jsonl</c>); omit it to replay
/// the current plan's log.
/// </summary>
public sealed class ReplayCommand : Command<ReplayCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[SOURCE]")]
        [Description("Path to an events.jsonl, or a repo/dir containing .conductor/events.jsonl. Omit to use the plan's log (--plan / CONDUCTOR_PLAN).")]
        public string? Source { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var logPath = ResolveLogPath(settings);
        if (logPath == null || !File.Exists(logPath))
        {
            AnsiConsole.MarkupLine($"[red]no event log found[/]{(logPath != null ? $" at {Markup.Escape(logPath)}" : "")} — pass a path to events.jsonl, or use --plan.");
            return 1;
        }

        var events = EventLog.ReadAll(logPath);
        var steps = Replay.Build(events);
        if (steps.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]event log has no transitions to replay[/] ({Markup.Escape(logPath)}).");
            return 0;
        }

        var runId = events.Select(e => e.RunId).FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? "?";
        AnsiConsole.WriteLine($"replay · {logPath}");
        AnsiConsole.WriteLine($"run {runId} · {events.Count} events · {steps.Count} transitions");
        AnsiConsole.WriteLine(new string('-', 72));
        foreach (var line in steps.SelectMany(Replay.FormatStep))
            AnsiConsole.WriteLine(line);
        AnsiConsole.WriteLine(new string('-', 72));
        AnsiConsole.WriteLine("final: " + Replay.FormatState(steps[^1].StateAsOf));
        return 0;
    }

    // A given source may be a direct events.jsonl, a repo/dir (→ .conductor/events.jsonl), or absent
    // (→ the resolved plan's log). A given-but-missing path is returned as-is so the caller's
    // File.Exists check surfaces the exact path in the error.
    private static string? ResolveLogPath(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Source))
        {
            if (File.Exists(settings.Source)) return settings.Source;
            if (Directory.Exists(settings.Source))
            {
                var nested = Path.Combine(settings.Source, ".conductor", "events.jsonl");
                if (File.Exists(nested)) return nested;
                var direct = Path.Combine(settings.Source, "events.jsonl");
                return File.Exists(direct) ? direct : nested;
            }
            return settings.Source;
        }
        try
        {
            var plan = PlanConfig.Load(settings.ResolvePlanPath());
            return Path.Combine(plan.StateDir, "events.jsonl");
        }
        catch (InvalidOperationException) { return null; }
    }
}

/// <summary>Offline dashboard preview: renders the current plan/tracker state (read-only) with
/// representative synthetic session data, so the UI can be verified without running the plan.</summary>
public sealed class PreviewCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        TrackerSnapshot track;
        // Preview is a read-only UI convenience: a missing/unreadable tracker just renders an empty
        // plan rather than aborting the preview. A malformed table is not fatal here.
        try { track = TrackerParser.ParseFile(plan.TrackerPath); }
        catch (IOException) { track = new TrackerSnapshot(); }

        var dash = new LiveDashboard(plan);
        DashboardPreview.Seed(dash, plan, state, track);
        AnsiConsole.MarkupLine("[grey]rendering preview — press any key to exit…[/]");
        dash.RunPreview();
        return 0;
    }
}

/// <summary>Writes the control file consumed by a running conductor (works from any terminal).</summary>
public abstract class CtlCommand(string command, string explanation, bool dangerous = false) : Command<CtlCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--yes")]
        [Description("Skip confirmation prompt for destructive actions (abort/kill/skip/rollback).")]
        public bool Yes { get; init; }

        [CommandOption("--force")]
        [Description("rollback only: discard an uncommitted (dirty) working tree during the reset.")]
        public bool Force { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (dangerous && !settings.Yes)
        {
            AnsiConsole.MarkupLine($"[red]DESTRUCTIVE: {Markup.Escape(command)} — {Markup.Escape(explanation)}[/]");
            AnsiConsole.MarkupLine("[yellow]Use --yes to confirm, or interact via the dashboard TUI (double-tap the key).[/]");
            return 2;
        }
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new
            {
                command,
                issuedUtc = DateTime.UtcNow,
                confirmed = dangerous ? true : (bool?)null,
                intentId = dangerous ? Guid.NewGuid().ToString("N") : null,
                force = settings.Force ? true : (bool?)null,
            }));
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(command)}[/] queued — {Markup.Escape(explanation)}");
        return 0;
    }
}

public sealed class PauseCommand() : CtlCommand("pause", "the running conductor will pause after the current session");
public sealed class ResumeCtlCommand() : CtlCommand("resume", "a paused/needs-human conductor will continue");
public sealed class AbortCommand() : CtlCommand("abort", "the running conductor will kill the session and stop", dangerous: true);
public sealed class SkipCommand() : CtlCommand("skip", "the current stage will be skipped and flagged for review", dangerous: true);
public sealed class KillCommand() : CtlCommand("kill", "the current agent session will be killed (conductor keeps running)", dangerous: true);
public sealed class ApproveCommand() : CtlCommand("approve", "approve the currently owner-gated stage so the conductor advances past it");
public sealed class RetryStageCommand() : CtlCommand("retry-stage", "reset the attempt counter and re-queue a deliver session for the current stage");
public sealed class RollbackCommand() : CtlCommand("rollback", "reset the working tree to the stage's checkpoint commit (refuses if dirty)", dangerous: true);
public sealed class PauseAfterStageCommand() : CtlCommand("pause-after-stage", "park at Paused after the current stage completes rather than advancing");

/// <summary>Jump to a specific stage (clears pending fix/resume/gates for the old stage).</summary>
public sealed class GotoCommand : Command<GotoCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<STAGE>")]
        [Description("The stage ID to jump to (e.g. B3).")]
        public string StageId { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new { command = "goto", stageId = settings.StageId, issuedUtc = DateTime.UtcNow }));
        AnsiConsole.MarkupLine($"[green]goto[/] queued → stage {Markup.Escape(settings.StageId)}");
        return 0;
    }
}

/// <summary>Queues a human instruction for the agent (from any terminal) — injected into the next session.</summary>
public sealed class InjectCommand : Command<InjectCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<INSTRUCTION>")]
        [Description("The instruction to queue for the agent's next session.")]
        public string Instruction { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var prev = InstructionQueue.List(plan).LastOrDefault()?.File;
        var entry = InstructionQueue.Write(plan, settings.Instruction, prev);
        AnsiConsole.MarkupLine($"[green]queued[/] {Markup.Escape(entry.File)} — injected into the next session prompt");
        return 0;
    }
}

/// <summary>Scaffolds a new plan + TRACKER.md from a built-in template (B1.6).</summary>
public sealed class NewPlanCommand : Command<NewPlanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--template <TEMPLATE>")]
        [Description("Template name: minimal, dotnet, node, or shamshir. Default: dotnet.")]
        public string Template { get; init; } = "dotnet";

        [CommandOption("-o|--output <DIR>")]
        [Description("Directory to create the files in. Created if missing. Default: cwd.")]
        public string? Output { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Plan name. Default: directory name or 'plan'.")]
        public string? Name { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Absolute path to the repo. Default: output directory.")]
        public string? Repo { get; init; }

        public static readonly string[] ValidTemplates = ["minimal", "dotnet", "node", "shamshir"];
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var template = settings.Template.ToLowerInvariant();
        if (!Settings.ValidTemplates.Contains(template))
        {
            AnsiConsole.MarkupLine($"[red]Unknown template '{Markup.Escape(settings.Template)}'.[/] Valid: {string.Join(", ", Settings.ValidTemplates)}");
            return 1;
        }

        var outputDir = Path.GetFullPath(settings.Output ?? ".");
        var name = settings.Name ?? Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "plan";
        var repo = settings.Repo ?? outputDir;

        Directory.CreateDirectory(outputDir);

        var planPath = Path.Combine(outputDir, "conductor.plan.json");
        var trackerPath = Path.Combine(outputDir, "TRACKER.md");

        if (File.Exists(planPath) || File.Exists(trackerPath))
        {
            AnsiConsole.MarkupLine("[red]Plan file(s) already exist — delete them first or use a different output directory.[/]");
            return 1;
        }

        File.WriteAllText(planPath, BuildPlanJson(template, name, repo), System.Text.Encoding.UTF8);
        File.WriteAllText(trackerPath, BuildTrackerMd(template, name), System.Text.Encoding.UTF8);

        // Verify the output loads (A6 ship-without-launch). Don't leave a half-written scaffold on
        // disk if the self-check fails — clean up and surface the reason.
        try
        {
            PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            File.Delete(planPath);
            File.Delete(trackerPath);
            AnsiConsole.MarkupLine($"[red]Scaffold failed self-check and was removed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(planPath)}");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(trackerPath)}");
        AnsiConsole.MarkupLine($"[grey]Template: {template}. Edit the plan + tracker, then run: conductor run -p {Markup.Escape(planPath)}[/]");
        return 0;
    }

    internal static string BuildPlanJson(string template, string name, string repo)
    {
        var repoNormalised = repo.Replace("\\", "/");
        return template switch
        {
            "minimal" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "stages": [
                { "id": "S1", "title": "First phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [],
              "report": { "commit": true, "push": true }
            }
            """,
            "dotnet" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "stages": [
                { "id": "S1", "title": "First phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [
                { "name": "build", "command": "dotnet build", "tier": "fast", "timeoutMinutes": 10 },
                { "name": "tests", "command": "dotnet test", "timeoutMinutes": 20 }
              ],
              "limits": { "stallMinutes": 12, "sessionTimeoutMinutes": 180, "stageSlackFactor": 2 },
              "report": { "commit": true, "push": true }
            }
            """,
            "node" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "stages": [
                { "id": "S1", "title": "First phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [
                { "name": "lint",  "command": "npm run lint",  "tier": "fast", "timeoutMinutes": 5 },
                { "name": "test",  "command": "npm test",       "timeoutMinutes": 15 },
                { "name": "build", "command": "npm run build",  "timeoutMinutes": 10 }
              ],
              "limits": { "stallMinutes": 12, "sessionTimeoutMinutes": 180, "stageSlackFactor": 2 },
              "report": { "commit": true, "push": true }
            }
            """,
            "shamshir" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "conventions": {
                "stageIdPattern": "(?<stage>[A-Za-z]+-?\\d+)(?:\\.\\d+)?[a-z]?",
                "status": { "inProgress": ["IN PROGRESS"] }
              },
              "stages": [
                { "id": "P-0", "title": "Land the tree", "sessions": 2 },
                { "id": "P0",  "title": "First detail phase", "sessions": 2 },
                { "id": "P1",  "title": "Second phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [
                { "name": "build", "command": "dotnet build", "tier": "fast", "timeoutMinutes": 10 },
                { "name": "tests", "command": "dotnet test", "timeoutMinutes": 20 }
              ],
              "limits": { "stallMinutes": 12, "sessionTimeoutMinutes": 180, "stageSlackFactor": 2 },
              "report": { "commit": true, "push": true }
            }
            """,
            _ => throw new ArgumentException($"Unknown template: {template}", nameof(template)),
        };
    }

    internal static string BuildTrackerMd(string template, string name)
    {
        // Checkpoints + handoff MUST match the stages declared in BuildPlanJson for this template,
        // otherwise the scaffold produces a plan whose stages own no rows and can never complete.
        // shamshir declares irregular stage ids (P-0/P0/P1); the others declare a single S1 stage.
        var (firstStage, conventionsNote, rows) = template == "shamshir"
            ? ("P-0",
               "\n\n> Conventions configured in the plan: irregular stage ids (`P-0`, `P0.1`, `P3.4b`, `F5`) supported.\n",
               "| P-0  | Land the tree           | TODO | | |\n" +
               "| P0.1 | First detail-phase task | TODO | | |\n" +
               "| P1.1 | Second-phase task       | TODO | | |")
            : ("S1",
               "",
               "| S1.1 | First task  | TODO | | |\n" +
               "| S1.2 | Second task | TODO | | |");

        return $$"""
        # {{name}} — Tracker (resume here)

        **Read order for a fresh session:** this file.{{conventionsNote}}
        ## Handoff  (overwrite this block, ≤12 lines, no history)
        last: (none) — scaffolded by conductor new-plan --template {{template}}.
        stage: **{{firstStage}} NOT STARTED**.
        gate: not yet run.
        next: **{{firstStage}}** — first checkpoint.

        ## Checkpoints

        Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path from a run this phase.

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        {{rows}}
        """;
    }
}

/// <summary>
/// B11.2 — doctor: prints exactly what will happen on resume (pending fix/resume/phase-gate/audit/owner-gate).
/// Read-only; never writes state.
/// </summary>
public sealed class DoctorCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);

        AnsiConsole.MarkupLine($"[bold aqua]conductor doctor[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.MarkupLine($"repo: {Markup.Escape(plan.Repo)}");
        AnsiConsole.MarkupLine($"branch: {Markup.Escape(Git.Branch(plan.Repo))}");
        AnsiConsole.MarkupLine($"state dir: {Markup.Escape(plan.StateDir)}");
        AnsiConsole.WriteLine();

        var statusColor = state.Status switch
        {
            RunStatus.Idle or RunStatus.Completed => "green",
            RunStatus.Running or RunStatus.VerifyingGates => "yellow",
            RunStatus.Backoff => "orange1",
            RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner => "red",
            RunStatus.Aborted => "red",
            _ => "grey",
        };
        AnsiConsole.MarkupLine($"[bold]Status:[/] [{statusColor}]{Markup.Escape(state.Status.ToString())}[/]");
        AnsiConsole.MarkupLine($"[bold]Current stage:[/] {Markup.Escape(state.CurrentStage ?? "(none)")}");
        AnsiConsole.MarkupLine($"[bold]Session counter:[/] {state.SessionCounter}");
        AnsiConsole.MarkupLine($"[bold]Total cost:[/] ${state.TotalCostUsd:0.00}");

        if (state.AttentionReason is { } reason)
            AnsiConsole.MarkupLine($"[bold]Attention:[/] [red]{Markup.Escape(reason)}[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold aqua]On resume, this will happen:[/]");

        var step = 1;
        if (state.PendingFix is { } fix)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Fix session[/] for stage {Markup.Escape(state.CurrentStage ?? "?")} (from session #{fix.FromSession})");
            AnsiConsole.MarkupLine($"      gates failed: {Markup.Escape(fix.GateFailures)}");
        }
        if (state.PendingResume is { } resume)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Resume session[/] from session #{resume.FromSession} — {Markup.Escape(resume.Reason)}");
        }
        if (state.Status == RunStatus.AwaitingOwner)
        {
            var awaitReason = state.AwaitingOwnerReason?.ToString() ?? "OwnerGate";
            AnsiConsole.MarkupLine($"  {step++}. [green]Awaiting owner approval[/] for stage {Markup.Escape(state.CurrentStage ?? "?")} (reason: {Markup.Escape(awaitReason)})");
            AnsiConsole.MarkupLine($"      approve: conductor approve -p <plan>");
        }
        if (state.PendingPhaseGate is { } pg)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Phase gate pending[/] for stage {Markup.Escape(pg.StageId)} — full battery will run");
        }
        if (state.PendingAudit is { } audit)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Audit pending[/] for stage {Markup.Escape(audit.StageId)}");
        }

        if (step == 1)
        {
            AnsiConsole.MarkupLine($"  {step++}. Next session: deliver for stage {Markup.Escape(state.CurrentStage ?? "?")}");
        }

        // Remaining stages
        var track = SafeParseTracker(plan);
        var remaining = plan.Stages
            .Where(s =>
            {
                if (state.SkippedStages.Contains(s.Id)) return false;
                if (state.ConfirmedStages.Contains(s.Id)) return false;
                if (track != null)
                {
                    var rows = track.ForStage(s.Id).ToList();
                    if (rows.Count == 0) return true;
                    return !rows.All(r => r.IsDone);
                }
                return true;
            })
            .Select(s => s.Id)
            .ToList();

        if (remaining.Count > 0)
            AnsiConsole.MarkupLine($"  {step}. [grey]Remaining stages:[/] {string.Join(" → ", remaining)}");
        else
            AnsiConsole.MarkupLine($"  {step}. [green]All stages complete[/]");

        return 0;
    }

    private static TrackerSnapshot? SafeParseTracker(PlanConfig plan)
    {
        try { return TrackerParser.ParseFile(plan.TrackerPath); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[grey]Note:[/] could not parse tracker at {Markup.Escape(plan.TrackerPath)} — {Markup.Escape(ex.GetType().Name)}. Shown remaining stages are state-based only.");
            return null;
        }
    }
}

/// <summary>
/// B11.2 — tab completion: generates shell completion scripts for PowerShell and bash.
/// </summary>
public sealed class CompletionCommand : Command<CompletionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SHELL>")]
        [Description("Target shell: powershell or bash")]
        public string Shell { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var shell = settings.Shell.ToLowerInvariant();
        return shell switch
        {
            "powershell" => WritePowerShellCompletion(),
            "bash" => WriteBashCompletion(),
            _ => WriteUsageError(),
        };
    }

    private static int WritePowerShellCompletion()
    {
        Console.WriteLine(GeneratePowerShell());
        return 0;
    }

    internal static string GeneratePowerShell()
    {
        var verbs = "run status report replay preview pause resume approve kill skip inject abort retry-stage rollback pause-after-stage goto tasks new-plan doctor completion";
        var opts = "-p --plan --yes --force --dry-run --once --max-sessions --no-dashboard -o --output --name --repo";
        var newPlanOpts = "--template -o --output --name --repo";
        return $$"""
            # conductor tab completion for PowerShell — generated by 'conductor completion powershell'
            # Source: conductor completion powershell | Invoke-Expression
            # Or save to a file and dot-source in $PROFILE: conductor completion powershell > conductor-completion.ps1

            Register-ArgumentCompleter -Native -CommandName conductor -ScriptBlock {
                param($wordToComplete, $commandAst, $cursorPosition)
                $verbs = @('{{verbs}}' -split ' ')
                $opts = @('{{opts}}' -split ' ')
                $newPlanOpts = @('{{newPlanOpts}}' -split ' ')
                $tokens = $commandAst.ToString() -split '\s+' | Where-Object { $_ }
                if ($tokens.Count -eq 1) {
                    $verbs | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                    }
                }
                elseif ($tokens[1] -eq 'new-plan') {
                    $newPlanOpts | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_)
                    }
                }
                elseif ($tokens[1] -eq 'completion') {
                    @('powershell','bash') | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                    }
                }
                elseif ($tokens[1] -in @('run','status','report','replay','preview','pause','resume',
                        'approve','kill','skip','inject','abort','retry-stage','rollback','pause-after-stage',
                        'goto','tasks','doctor')) {
                    $opts | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_)
                    }
                }
            }
            """;
    }

    private static int WriteBashCompletion()
    {
        Console.WriteLine(GenerateBash());
        return 0;
    }

    internal static string GenerateBash()
    {
        return """
            # conductor tab completion for bash — generated by 'conductor completion bash'
            # Source: source <(conductor completion bash)

            _conductor_completion() {
                local cur prev words cword
                _init_completion || return
                COMPREPLY=()
                cur="${COMP_WORDS[COMP_CWORD]}"

                if [[ $COMP_CWORD -eq 1 ]]; then
                    COMPREPLY=($(compgen -W "run status report replay preview pause resume approve kill skip inject abort retry-stage rollback pause-after-stage goto tasks new-plan doctor completion" -- "$cur"))
                    return
                fi
                case "${COMP_WORDS[1]}" in
                    run|status|report|replay|preview|pause|resume|approve|kill|skip|inject|abort|retry-stage|rollback|pause-after-stage|goto|tasks|doctor)
                        COMPREPLY=($(compgen -W "-p --plan --yes --force --dry-run --once --max-sessions --no-dashboard -o --output --name --repo" -- "$cur"))
                        ;;
                    completion)
                        COMPREPLY=($(compgen -W "powershell bash" -- "$cur"))
                        ;;
                    new-plan)
                        COMPREPLY=($(compgen -W "--template -o --output --name --repo" -- "$cur"))
                        ;;
                esac
            }
            complete -F _conductor_completion conductor
            """;
    }

    private static int WriteUsageError()
    {
        AnsiConsole.MarkupLine("[red]Unknown shell.[/] Valid: powershell, bash.");
        AnsiConsole.MarkupLine("Usage: conductor completion <powershell|bash>");
        return 1;
    }
}
