using System.ComponentModel;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// F2.3: Background process management — sanctioned primitive for agents to run commands
/// that take >3 min. Agents call <c>conductor bg start|status|logs|stop</c> via CLI or MCP
/// to spawn, monitor, and kill long-running child processes without blocking the session.
/// Every bg process is tracked in the run.db pids table and its stdout/stderr are captured
/// to a log file under <c>.conductor/bg-logs/</c>.
/// </summary>
public sealed class BgCommand : Command<BgCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: start, status, logs, stop. Omit to show help.")]
        public string Verb { get; init; } = "";

        [CommandArgument(1, "[PID_OR_PURPOSE]")]
        [Description("PID (number) or purpose label (for logs/stop sub-commands).")]
        public string? PidOrPurpose { get; init; }

        [CommandOption("--purpose <LABEL>")]
        [Description("Purpose label for the background process (start only). Defaults to the executable name.")]
        public string? Purpose { get; init; }

        [CommandOption("--cwd <DIR>")]
        [Description("Working directory for the background process (start only). Defaults to the plan's repo root.")]
        public string? Cwd { get; init; }

        [CommandOption("-t|--tail <N>")]
        [Description("Number of lines to tail from the log (logs only, default 30).")]
        public int Tail { get; init; } = 30;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var verb = settings.Verb.ToLowerInvariant();
        var remaining = context.Remaining;

        try
        {
            return verb switch
            {
                "start" => BgStartHandler.ExecuteStart(settings, remaining),
                "status" => BgStatusHandler.ExecuteStatus(settings),
                "logs" => BgLogsHandler.ExecuteLogs(settings),
                "stop" => BgStopHandler.ExecuteStop(settings),
                _ => PrintBgHelp(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static int PrintBgHelp()
    {
        AnsiConsole.MarkupLine("[bold aqua]conductor bg[/] — background process management");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]start[/]  [grey]Spawn a long-running background process[/]");
        AnsiConsole.MarkupLine("         [grey]Usage: conductor bg start [[--purpose <label>]] [[--cwd <dir>]] -- <command> [[args...]][/]");
        AnsiConsole.MarkupLine("  [bold]status[/] [grey]List all tracked background processes and their liveness[/]");
        AnsiConsole.MarkupLine("  [bold]logs[/]   [grey]Tail the stdout/stderr log of a background process[/]");
        AnsiConsole.MarkupLine("         [grey]Usage: conductor bg logs <pid> [[-t|--tail <N>]][/]");
        AnsiConsole.MarkupLine("  [bold]stop[/]   [grey]Kill a background process by PID[/]");
        AnsiConsole.MarkupLine("         [grey]Usage: conductor bg stop <pid>[/]");
        return 0;
    }
}
