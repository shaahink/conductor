using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

internal static class BgStatusHandler
{
    public static int ExecuteStatus(BgCommand.Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[grey]No run.db found — no background processes tracked.[/]");
            return 0;
        }

        using var store = new SqliteRunStore(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
        var runId = store.GetLatestRunId(plan.Name);
        if (string.IsNullOrEmpty(runId))
        {
            AnsiConsole.MarkupLine("[grey]No run found in run.db — no background processes tracked.[/]");
            return 0;
        }

        var pids = store.GetAllPids(runId);
        if (pids.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No background processes tracked for this run.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold aqua]Background Processes[/]")
            .AddColumn("PID")
            .AddColumn("Purpose")
            .AddColumn("Status")
            .AddColumn("Started")
            .AddColumn("Runtime");

        foreach (var p in pids)
        {
            var alive = PidLiveness.LooksAlive(p.Pid, p.StartedUtc);
            var status = p.ExitedUtc != null
                ? $"[grey]exited ({p.ExitedUtc:HH:mm:ss})[/]"
                : alive
                    ? "[green]running[/]"
                    : "[red]dead[/]";
            var startStr = p.StartedUtc.ToString("HH:mm:ss");
            var runtime = p.ExitedUtc != null
                ? FormatDuration(p.ExitedUtc.Value - p.StartedUtc)
                : alive
                    ? FormatDuration(DateTime.UtcNow - p.StartedUtc)
                    : "—";

            table.AddRow(
                Markup.Escape(p.Pid.ToString()),
                Markup.Escape(p.Purpose),
                status,
                Markup.Escape(startStr),
                Markup.Escape(runtime));
        }
        AnsiConsole.Write(table);

        // Hint about log paths
        AnsiConsole.MarkupLine("[grey]Logs: .conductor/bg-logs/  (use 'conductor bg logs <pid>' to tail)[/]");
        return 0;
    }

    /// <summary>SC4.1: this had its own copy of the liveness check, and that copy let a Win32
    /// access-denied escape — `conductor bg status` died with a stack trace the moment run.db held a
    /// pid now owned by a process this one may not open. One implementation, in
    /// <see cref="PidLiveness"/>, which treats "cannot inspect" as alive rather than as a crash.</summary>
    public static bool IsProcessAlive(int pid) => PidLiveness.LooksAlive(pid, DateTime.UtcNow);

    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
    }
}
