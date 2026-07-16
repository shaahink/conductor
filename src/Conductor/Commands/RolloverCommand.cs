using System.ComponentModel;
using System.Text.Json;

using Conductor.Core.Commands;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>Queue the P5 <c>set-rollover</c> verb for the running conductor: set/clear the
/// session-token rollover for THIS run only. A token count rolls sessions over past the cap,
/// "off" forces rollover off even if the plan sets a cap, "clear" hands the decision back to
/// <c>limits.maxSessionTokens</c>. Run-state only — the plan file is never written.</summary>
public sealed class RolloverCommand : Command<RolloverCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<TOKENS|off|clear>")]
        [Description("Per-session token cap for this run (e.g. 200000), 'off' (rollover disabled this run), or 'clear' (the plan decides again).")]
        public string Value { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        // Same rule the dispatcher applies — catch a typo here instead of as a toast in another process.
        if (!ControlDispatcher.ParseRolloverValue(settings.Value).Ok)
        {
            AnsiConsole.MarkupLine($"[red]'{Markup.Escape(settings.Value)}' is not a token count, 'off', or 'clear'.[/]");
            return 2;
        }
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new { command = "set-rollover", value = settings.Value, issuedUtc = DateTime.UtcNow }));
        AnsiConsole.MarkupLine($"[green]set-rollover[/] queued → {Markup.Escape(settings.Value)} (this run only — the plan file is never written)");
        return 0;
    }
}
