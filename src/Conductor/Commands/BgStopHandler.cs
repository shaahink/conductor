using System.Diagnostics;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

internal static class BgStopHandler
{
    public static int ExecuteStop(BgCommand.Settings settings)
    {
        var target = settings.PidOrPurpose;
        if (string.IsNullOrWhiteSpace(target))
        {
            AnsiConsole.MarkupLine("[red]Usage: conductor bg stop <pid>[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor bg stop 12345[/]");
            return 1;
        }

        if (!int.TryParse(target, out var pid))
        {
            AnsiConsole.MarkupLine($"[red]'{Markup.Escape(target)}' is not a valid PID.[/] Use the numeric PID from 'conductor bg status'.");
            return 1;
        }

        // Kill the process
        try
        {
            using var proc = Process.GetProcessById(pid);
            AnsiConsole.MarkupLine($"[yellow]Stopping PID={pid} ({Markup.Escape(proc.ProcessName)})...[/]");
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            AnsiConsole.MarkupLine($"[green]Killed PID={pid}.[/]");
        }
        catch (ArgumentException)
        {
            AnsiConsole.MarkupLine($"[grey]PID {pid} not found (already exited).[/]");
        }
        catch (InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[grey]PID {pid} already exited.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to kill PID {pid}: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Mark as exited in run.db
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (File.Exists(runDbPath))
        {
            try
            {
                using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
                db.MarkPidExited(pid, -1);
            }
            catch { /* best-effort */ }
        }

        return 0;
    }
}
