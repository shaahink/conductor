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

public sealed class PauseCommand() : CtlCommand("pause", "the running conductor will pause after the current session");
public sealed class ResumeCtlCommand() : CtlCommand("resume", "a paused/needs-human conductor will continue");
public sealed class AbortCommand() : CtlCommand("abort", "the running conductor will kill the session and stop", dangerous: true);
public sealed class SkipCommand() : CtlCommand("skip", "the current stage will be skipped and flagged for review", dangerous: true);
public sealed class KillCommand() : CtlCommand("kill", "the current agent session will be killed (conductor keeps running)", dangerous: true);
public sealed class ApproveCommand() : CtlCommand("approve", "approve the currently owner-gated stage so the conductor advances past it");
public sealed class RetryStageCommand() : CtlCommand("retry-stage", "reset the attempt counter and re-queue a deliver session for the current stage");
public sealed class RollbackCommand() : CtlCommand("rollback", "reset the working tree to the stage's checkpoint commit (refuses if dirty)", dangerous: true);
public sealed class PauseAfterStageCommand() : CtlCommand("pause-after-stage", "park at Paused after the current stage completes rather than advancing");
