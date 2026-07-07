using System.ComponentModel;
using System.Text.Json;
using Conductor.Core;
using Conductor.Models;
using Conductor.Ui;
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

        var opts = new RunOptions(settings.DryRun, settings.Once, settings.MaxSessions);
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var usePlain = settings.NoDashboard || settings.DryRun || Console.IsOutputRedirected;
        if (usePlain)
        {
            var orch = new Orchestrator(plan, state, statePath, new PlainSink(), opts);
            return orch.Run(cts.Token);
        }

        var dash = new LiveDashboard(plan);
        var orchestrator = new Orchestrator(plan, state, statePath, dash, opts);
        var task = Task.Run(() => orchestrator.Run(cts.Token));
        dash.RunUiLoop(task);
        return task.GetAwaiter().GetResult();
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
        File.WriteAllText(Reporter.ReportPath(plan), Reporter.Build(plan, state, track, null), Reporter.Utf8Bom);
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
        try { track = TrackerParser.ParseFile(plan.TrackerPath); }
        catch { track = new TrackerSnapshot(); }

        var dash = new LiveDashboard(plan);
        DashboardPreview.Seed(dash, plan, state, track);
        AnsiConsole.MarkupLine("[grey]rendering preview — press any key to exit…[/]");
        dash.RunPreview();
        return 0;
    }
}

/// <summary>Writes the control file consumed by a running conductor (works from any terminal).</summary>
public abstract class CtlCommand(string command, string explanation) : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new { command, issuedUtc = DateTime.UtcNow }));
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(command)}[/] queued — {Markup.Escape(explanation)}");
        return 0;
    }
}

public sealed class PauseCommand() : CtlCommand("pause", "the running conductor will pause after the current session");
public sealed class ResumeCtlCommand() : CtlCommand("resume", "a paused/needs-human conductor will continue");
public sealed class AbortCommand() : CtlCommand("abort", "the running conductor will kill the session and stop");
public sealed class SkipCommand() : CtlCommand("skip", "the current stage will be skipped and flagged for review");
public sealed class KillCommand() : CtlCommand("kill", "the current agent session will be killed (conductor keeps running)");
