using Conductor.Core.Events;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

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
            .GroupBy(t => t.CheckpointId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

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
                    "deliver" => "[grey]deliver[/]",
                    "planner" => "[grey]deliver[/]",
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
