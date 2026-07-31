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
            .AddColumn("Started (local)")
            .AddColumn("Runtime")
            .AddColumn("Log");

        var bgLogDir = Path.Combine(plan.StateDir, "bg-logs");
        foreach (var p in pids)
        {
            var alive = PidLiveness.LooksAlive(p.Pid, p.StartedUtc);
            var status = p.ExitedUtc != null
                ? $"[grey]exited ({p.ExitedUtc.Value.ToLocalTime():HH:mm:ss})[/]"
                : alive
                    ? "[green]running[/]"
                    : "[red]dead[/]";
            // SC5.4: clock times render LOCAL, matching `status` and the log lines; durations are
            // computed entirely in UTC. Mixing the two is what printed `-1694s` for a live job
            // (round-four #4) — see SqliteRunStore.ParseUtc for why the row read local to begin with.
            var startStr = p.StartedUtc.ToLocalTime().ToString("HH:mm:ss");
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
                Markup.Escape(runtime),
                Markup.Escape(LogTargetFor(bgLogDir, store, runId, p)));
        }
        AnsiConsole.Write(table);

        // Hint about log paths
        AnsiConsole.MarkupLine("[grey]Logs: .conductor/bg-logs/  (use 'conductor bg logs <pid>' to tail)[/]");
        return 0;
    }

    /// <summary>SC5.4: the file `bg logs &lt;pid&gt;` will actually read for this row, named in the
    /// table so the operator never has to guess. An agent row points at its session stream under
    /// <c>logs/</c>; everything else at its child log under <c>bg-logs/</c>.</summary>
    private static string LogTargetFor(string bgLogDir, IRunStore store, string runId, PidRow p)
    {
        if (BgLogs.IsAgentRow(p))
            return BgLogs.SessionNumberFor(p) is { } n ? $"logs/{BgLogs.StreamName(n)}" : "—";
        var log = BgLogs.Resolve(bgLogDir, p.Pid, store, runId);
        return log == null ? "—" : $"bg-logs/{Path.GetFileName(log)}";
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
