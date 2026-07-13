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
            var pidSuffix = $"-{pid}.log";
            logFile = files.FirstOrDefault(f => f.EndsWith(pidSuffix, StringComparison.OrdinalIgnoreCase));
        }

        if (logFile == null)
        {
            // Fuzzy match by purpose substring
            logFile = files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Contains(target, StringComparison.OrdinalIgnoreCase));
        }

        if (logFile == null)
        {
            // Check run.db for the PID's purpose and reconstruct the filename
            var runDbPath = Path.Combine(plan.StateDir, "run.db");
            if (File.Exists(runDbPath) && int.TryParse(target, out var dbPid))
            {
                try
                {
                    using var store = new SqliteRunStore(runDbPath,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
                    var runId = store.GetLatestRunId(plan.Name);
                    if (!string.IsNullOrEmpty(runId))
                    {
                        var allPids = store.GetAllPids(runId);
                        var match = allPids.FirstOrDefault(p => p.Pid == dbPid);
                        if (match != null)
                        {
                            var safePurpose = BgStartHandler.SanitizeFileName(match.Purpose.Replace("bg:", ""));
                            var recons = Path.Combine(logDir, $"{safePurpose}-{match.Pid}.log");
                            if (File.Exists(recons)) logFile = recons;
                        }
                    }
                }
                catch { /* best-effort */ }
            }

            if (logFile == null)
            {
                AnsiConsole.MarkupLine($"[red]No log file found for '{Markup.Escape(target)}'.[/]");
                var availFiles = files.Select(Path.GetFileName);
                AnsiConsole.MarkupLine($"[grey]Available: {Markup.Escape(string.Join(", ", availFiles))}[/]");
                return 1;
            }
        }

        // Read and print the last N lines — synchronous by design (CLI command).
#pragma warning disable MA0045
        try
        {
            var tail = settings.Tail > 0 ? settings.Tail : 30;
            var allLines = File.ReadAllLines(logFile);
            var lines = allLines.Length <= tail ? allLines : allLines[^tail..];

            AnsiConsole.MarkupLine($"[bold aqua]Log: {Markup.Escape(Path.GetFileName(logFile))}[/] ({lines.Length}/{allLines.Length} lines)");
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
