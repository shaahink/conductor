using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

internal static class BgStartHandler
{
    public static int ExecuteStart(BgCommand.Settings settings, IRemainingArguments remaining)
    {
        var cmdArgs = remaining.Raw;
        if (cmdArgs.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]bg start requires a command after --.[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor bg start --purpose backtest -- dotnet run[/]");
            return 1;
        }

        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = plan.RunDbPath;
        using var store = File.Exists(runDbPath)
            ? new SqliteRunStore(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance)
            : null;
        var runId = store?.GetLatestRunId(plan.Name);
        if (string.IsNullOrEmpty(runId))
        {
            var statePath = Path.Combine(plan.StateDir, "state.json");
            var state = RunState.LoadOrNew(statePath, plan.Name);
            runId = state.RunId ?? "bg-standalone";
        }
        var currentStage = GetCurrentStage(store, runId);
        var sessionCounter = GetSessionCounter(store, runId);

        var exe = cmdArgs[0];
        var exeArgs = cmdArgs.Skip(1).ToList();
        var purpose = settings.Purpose ?? Path.GetFileNameWithoutExtension(exe);

        var logDir = Path.Combine(plan.StateDir, "bg-logs");
        Directory.CreateDirectory(logDir);
        // W3.3 (bug #2): one instant, used twice — it names the log AND is the pids row's
        // started_utc, which is what lets `bg logs <pid>` find this file later.
        var startedUtc = DateTime.UtcNow;
        var logPath = Path.Combine(logDir, BgLogs.NameFor(purpose, startedUtc));
        var psi = BgLogs.RedirectedSpawn(exe, exeArgs, settings.Cwd ?? plan.Repo, logPath);

        // SF0.3 (bug #12): both halves of the detach, and BOTH are needed — see
        // BgLogs.StopLeakingConsoleHandles. This one must precede the spawn.
        BgLogs.StopLeakingConsoleHandles();

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start '{Markup.Escape(exe)}': {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (proc == null)
        {
            AnsiConsole.MarkupLine("[red]Process.Start returned null.[/]");
            return 1;
        }

        // SF0.3 (bug #12): drop our ends of the child's streams. Before this, the child inherited THIS
        // process's stdout, so `conductor bg start ... | anything` saw no EOF until the background job
        // finished — a piped 60-second child held the pipe for the full 60 seconds (measured).
        BgLogs.DetachStandardStreams(proc);

        // Track in run.db. No exit watcher: this command returns in milliseconds, so anything it
        // schedules dies with it (that WAS bug #2). A finished child's row is marked exited by the
        // lazy sweep every reader now runs (PidLiveness).
        if (store != null)
        {
            try
            {
                store.TrackPid(proc.Id, runId!, $"bg:{purpose}", currentStage,
                    sessionCounter > 0 ? sessionCounter : null, startedUtc);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Started but run.db tracking failed: {Markup.Escape(ex.Message)}[/]");
            }
        }

        AnsiConsole.MarkupLine($"[green]bg started[/] PID={proc.Id} purpose=[bold]{Markup.Escape(purpose)}[/]");
        AnsiConsole.MarkupLine($"  log: [grey]{Markup.Escape(logPath)}[/]");
        return 0;
    }

    public static string SanitizeFileName(string name) => BgLogs.Sanitize(name);

    private static string? GetCurrentStage(IRunStore? store, string? runId)
    {
        if (store == null || string.IsNullOrEmpty(runId)) return null;
        try
        {
            var json = store.LoadRunStateJson(runId);
            if (string.IsNullOrEmpty(json)) return null;
            var state = System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts);
            return state?.CurrentStage;
        }
        catch { return null; }
    }

    private static int GetSessionCounter(IRunStore? store, string? runId)
    {
        if (store == null || string.IsNullOrEmpty(runId)) return 0;
        try
        {
            var json = store.LoadRunStateJson(runId);
            if (string.IsNullOrEmpty(json)) return 0;
            var state = System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts);
            return state?.SessionCounter ?? 0;
        }
        catch { return 0; }
    }
}
