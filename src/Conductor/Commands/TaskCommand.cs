using System.ComponentModel;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// F1.3: Task/checkpoint CRUD — agents report progress via CLI verbs instead of hand-editing
/// the tracker markdown. Writes go to the run.db checkpoints table; the tracker regenerates from
/// that source of truth (F1.2 tracker-as-view).
/// </summary>
public sealed class TaskCommand : Command<TaskCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--done <CHECKPOINT>")]
        [Description("Mark a checkpoint as DONE.")]
        public string? Done { get; init; }

        [CommandOption("--in-progress <CHECKPOINT>")]
        [Description("Mark a checkpoint as IN PROGRESS (from TODO only).")]
        public string? InProgress { get; init; }

        [CommandOption("--list")]
        [Description("List all checkpoints from run.db.")]
        public bool List { get; init; }

        [CommandOption("-c|--commit <SHA>")]
        [Description("Commit SHA to attribute (for --done).")]
        public string? Commit { get; init; }

        [CommandOption("-e|--evidence <TEXT>")]
        [Description("Evidence string (for --done).")]
        public string? Evidence { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[red]No run.db found.[/] Run the conductor at least once to initialize the database.");
            return 1;
        }

        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        if (string.IsNullOrEmpty(state.RunId))
        {
            AnsiConsole.MarkupLine("[red]state.json has no RunId.[/] Initialize the run first (conductor run --dry-run or run at least one session).");
            return 1;
        }

        try
        {
            using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);

            if (settings.Done != null)
            {
                var allCps = db.GetCheckpoints(state.RunId);
                if (!allCps.Any(c => c.Id.Equals(settings.Done, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine($"[red]Checkpoint '{Markup.Escape(settings.Done)}' not found in run.db[/]");
                    return 1;
                }
                db.UpdateCheckpoint(state.RunId, settings.Done, "DONE",
                    settings.Commit ?? "-", settings.Evidence ?? "marked done via CLI");
                AnsiConsole.MarkupLine($"[green]checkpoint {Markup.Escape(settings.Done)} → DONE[/]");
            }
            else if (settings.InProgress != null)
            {
                var allCps = db.GetCheckpoints(state.RunId);
                if (!allCps.Any(c => c.Id.Equals(settings.InProgress, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine($"[red]Checkpoint '{Markup.Escape(settings.InProgress)}' not found in run.db[/]");
                    return 1;
                }
                db.MarkCheckpointInProgress(state.RunId, settings.InProgress);
                AnsiConsole.MarkupLine($"[yellow]checkpoint {Markup.Escape(settings.InProgress)} → IN PROGRESS[/]");
            }
            else if (settings.List)
            {
                var cps = db.GetCheckpoints(state.RunId);

                if (cps.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]no checkpoints in run.db[/]");
                    return 0;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[bold aqua]Checkpoints from run.db[/]")
                    .AddColumn("Stage")
                    .AddColumn("ID")
                    .AddColumn("Title")
                    .AddColumn("Status");

                foreach (var cp in cps)
                {
                    var icon = cp.Status switch
                    {
                        var s when s.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) => "[green]DONE[/]",
                        var s when s.StartsWith("IN", StringComparison.OrdinalIgnoreCase) => "[yellow]IN PROG[/]",
                        var s when s.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) => "[red]BLOCKED[/]",
                        _ => "[grey]TODO[/]",
                    };
                    table.AddRow(Markup.Escape(cp.StageId), Markup.Escape(cp.Id), Markup.Escape(cp.Title), icon);
                }
                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]Usage: conductor task --list | --done <id> | --in-progress <id>[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        return 0;
    }
}
