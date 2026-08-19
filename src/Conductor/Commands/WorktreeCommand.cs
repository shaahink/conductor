using System.ComponentModel;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Worktrees;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>KS4.4 — list and reap the attempt worktrees conductor leaves on disk.</summary>
/// <remarks>
/// <para>Read-only with no argument, which is the shape every diagnostic verb in this tree takes: a
/// human wants to know what is on their disk before anything deletes it. <c>--reap</c> acts, and even
/// then it acts only on trees conductor owns whose run is gone — a worktree a person made is invisible
/// to it, and a live run's tree is protected by the sidecar pid marker.</para>
/// <para>The engine runs the same sweep at startup (see <c>RunLoop</c>), so in normal operation this
/// verb is for looking rather than for fixing. It exists because the one failure that survives the
/// startup sweep — a tree whose build output is still locked by a process that outlived the run — needs
/// a human to close the thing holding it and say "now".</para>
/// </remarks>
public sealed class WorktreeCommand : Command<WorktreeCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--reap")]
        [Description("Remove the attempt worktrees whose run is gone. Without it, nothing is written.")]
        public bool Reap { get; init; }

        [CommandOption("--json")]
        [Description("Machine-readable output.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var survey = WorktreeSweeper.Survey(plan.Repo);

        List<string> acted = settings.Reap
            ? WorktreeSweeper.Reap(plan.Repo, dryRun: false)
            : new List<string>();

        if (settings.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(new
            {
                repo = plan.Repo,
                worktrees = survey.Select(s => new
                {
                    path = s.Entry.Path,
                    branch = s.Entry.Branch,
                    head = s.Entry.Head,
                    conductorOwned = s.ConductorOwned,
                    ownerAlive = s.OwnerAlive,
                    reapable = s.Reapable,
                    runId = s.Marker?.RunId,
                    stageId = s.Marker?.StageId,
                    attempt = s.Marker?.Attempt,
                }),
                reaped = acted,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (survey.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]no worktrees besides the primary tree at[/] {Markup.Escape(plan.Repo)}");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("path"); table.AddColumn("branch"); table.AddColumn("owner"); table.AddColumn("state");
        foreach (var s in survey)
        {
            var owner = s.ConductorOwned
                ? s.Marker is { } m ? $"conductor · run {m.RunId} · {m.StageId} attempt {m.Attempt}" : "conductor"
                : "[grey]not conductor's[/]";
            var state = !s.ConductorOwned ? "[grey]left alone[/]"
                : s.OwnerAlive ? "[green]live[/]"
                : "[yellow]orphan — reapable[/]";
            table.AddRow(Markup.Escape(s.Entry.Path), Markup.Escape(s.Entry.Branch ?? "(detached)"), owner, state);
        }
        AnsiConsole.Write(table);

        var reapable = survey.Count(s => s.Reapable);
        if (settings.Reap)
        {
            foreach (var line in acted) AnsiConsole.MarkupLine($"[green]reaped[/] {Markup.Escape(line)}");
            if (acted.Count == 0) AnsiConsole.MarkupLine("[grey]nothing to reap[/]");
        }
        else if (reapable > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{reapable}[/] orphaned attempt tree(s) — [bold]conductor worktree --reap[/] removes them.");
        }
        return 0;
    }
}
