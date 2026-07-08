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

public sealed class ReportCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Reporter.ReportPath(plan), Reporter.Build(plan, state, track, null, null), Reporter.Utf8Bom);
        AnsiConsole.MarkupLine($"report written to [bold]{Markup.Escape(Reporter.ReportPath(plan))}[/]");
        return 0;
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
        [Description("Skip confirmation prompt for destructive actions (abort/kill/skip).")]
        public bool Yes { get; init; }
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
