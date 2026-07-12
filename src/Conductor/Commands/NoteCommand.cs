using System.ComponentModel;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// F1.3: Write a finding/observation to the knowledge ledger (run.db ledger table).
/// Agents call this via CLI or MCP to persist discoveries immediately instead of
/// only at session end — kills the "stall destroys knowledge" failure (design doc §3.3).
/// </summary>
public sealed class NoteCommand : Command<NoteCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("-k|--kind <KIND>")]
        [Description("Ledger entry kind: finding, observation, trap, decision. Default: note.")]
        public string? Kind { get; init; }

        [CommandOption("-s|--stage <STAGE>")]
        [Description("Stage id to associate the note with (e.g. F1). Optional.")]
        public string? Stage { get; init; }

        [CommandArgument(0, "<TEXT>")]
        [Description("The note content.")]
        public string Text { get; init; } = "";
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
        var kind = string.IsNullOrWhiteSpace(settings.Kind) ? "note" : settings.Kind;

        try
        {
            using var db = OpenRunDb(runDbPath);
            db.WriteLedger(state.RunId, state.SessionCounter > 0 ? state.SessionCounter : null,
                settings.Stage ?? state.CurrentStage, kind, settings.Text);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Note write failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        AnsiConsole.MarkupLine($"[green]note written:[/] [{Markup.Escape(kind)}] {Markup.Escape(settings.Text)}");
        return 0;
    }

    private static RunDb OpenRunDb(string path)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance;
        try
        {
            return new RunDb(path, logger);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to open run.db at {path}: {ex.Message}", ex);
        }
    }
}
