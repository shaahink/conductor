using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

internal static class BgLogsHandler
{
    public static int ExecuteLogs(BgCommand.Settings settings)
    {
        var target = settings.PidOrPurpose;
        if (string.IsNullOrWhiteSpace(target))
        {
            AnsiConsole.MarkupLine("[red]Usage: conductor bg logs <pid>[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor bg logs 12345[/]");
            return 1;
        }

        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var logDir = Path.Combine(plan.StateDir, "bg-logs");
        if (!Directory.Exists(logDir))
        {
            AnsiConsole.MarkupLine("[grey]No bg-logs directory found.[/]");
            return 0;
        }

        // Find log file: if target is numeric, match by PID in filename; otherwise try partial match
        string? logFile = null;
        var files = Directory.GetFiles(logDir, "*.log").OrderByDescending(File.GetLastWriteTime).ToList();

        if (int.TryParse(target, out var pid))
        {
            var runDb = Path.Combine(plan.StateDir, "run.db");
            if (File.Exists(runDb))
            {
                try
                {
                    using var store = new SqliteRunStore(runDb,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
                    logFile = BgLogs.Resolve(logDir, pid, store, store.GetLatestRunId(plan.Name));
                }
                catch (InvalidOperationException) { /* best-effort; fall through to the fuzzy paths */ }
            }
            logFile ??= BgLogs.Resolve(logDir, pid, null, null);
        }

        if (logFile == null)
        {
            // Fuzzy match by purpose substring
            logFile = files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Contains(target, StringComparison.OrdinalIgnoreCase));
        }

        if (logFile == null)
        {
            AnsiConsole.MarkupLine($"[red]No log file found for '{Markup.Escape(target)}'.[/]");
            var availFiles = files.Select(Path.GetFileName);
            AnsiConsole.MarkupLine($"[grey]Available: {Markup.Escape(string.Join(", ", availFiles))}[/]");
            return 1;
        }

        // Read and print the last N lines — synchronous by design (CLI command).
#pragma warning disable MA0045
        try
        {
            var tail = settings.Tail > 0 ? settings.Tail : 30;
            // SC2.4 (bug 1): the whole point of `bg logs` is a log a child is STILL writing — the shell
            // doing the redirect holds a Write handle, which File.ReadAllLines' FileShare.Read does not
            // permit, so this printed "being used by another process" for every live job.
            var allLines = SharedFileRead.ReadAllLines(logFile);
            var lines = allLines.Count <= tail ? allLines : allLines.Skip(allLines.Count - tail).ToList();

            AnsiConsole.MarkupLine($"[bold aqua]Log: {Markup.Escape(Path.GetFileName(logFile))}[/] ({lines.Count}/{allLines.Count} lines)");
            AnsiConsole.WriteLine();
            foreach (var line in lines)
            {
                if (line.StartsWith("[stderr]", StringComparison.Ordinal))
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(line)}[/]");
                else
                    Console.WriteLine(line);
            }
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Cannot read log: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
#pragma warning restore MA0045

        return 0;
    }
}
