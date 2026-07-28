using System.ComponentModel;
using System.Text.Json;

using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

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
