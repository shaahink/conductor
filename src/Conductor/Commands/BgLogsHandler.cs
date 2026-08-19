using Conductor.Core;
using Conductor.Core.Providers;
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
        var tailCount = settings.Tail > 0 ? settings.Tail : 30;

        // Find log file: if target is numeric, match by PID in filename; otherwise try partial match
        string? logFile = null;
        var files = Directory.Exists(logDir)
            ? Directory.GetFiles(logDir, "*.log").OrderByDescending(File.GetLastWriteTime).ToList()
            : [];

        if (int.TryParse(target, out var pid))
        {
            var runDb = plan.RunDbPath;
            if (File.Exists(runDb))
            {
                try
                {
                    using var store = new SqliteRunStore(runDb,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
                    var runId = store.GetLatestRunId(plan.Name);
                    // SC5.4 (round-four #4): `bg status` lists the live agent, so `bg logs <that pid>`
                    // is the obvious next move — and it used to answer "No log file found" plus 67
                    // unrelated names, because an agent never writes to bg-logs/. Its stream is
                    // logs/session-NNN.jsonl, and this is the branch that says so.
                    var row = BgLogs.FindRow(store, runId, pid);
                    if (row != null && BgLogs.IsAgentRow(row))
                        return PrintAgentStream(plan, row, tailCount);
                    logFile = BgLogs.Resolve(logDir, pid, store, runId);
                }
                catch (InvalidOperationException) { /* best-effort; fall through to the fuzzy paths */ }
            }
            logFile ??= BgLogs.Resolve(logDir, pid, null, null);
        }

        if (logFile == null && !Directory.Exists(logDir))
        {
            AnsiConsole.MarkupLine("[grey]No bg-logs directory found.[/]");
            return 0;
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

        // Read and print the last N lines.
        try
        {
            var tail = tailCount;
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

        return 0;
    }

    /// <summary>SC5.4: what `bg logs` shows for an AGENT row. Names the stream and the prompt beside
    /// it — the two files an operator watching a session actually wants — then folds the tail through
    /// the plan's own provider so the envelopes read like the live feed instead of like NDJSON.</summary>
    private static int PrintAgentStream(PlanConfig plan, PidRow row, int tail)
    {
        var number = BgLogs.SessionNumberFor(row);
        var stream = BgLogs.ResolveAgentStream(plan.StateDir, row);
        if (stream == null)
        {
            AnsiConsole.MarkupLine(
                $"[red]pid {row.Pid} is agent session #{(number?.ToString() ?? "?")}, but its stream is not on disk.[/]");
            AnsiConsole.MarkupLine(
                $"[grey]Expected: {Markup.Escape(Path.Combine(plan.StateDir, "logs", BgLogs.StreamName(number ?? 0)))}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[bold aqua]Agent session #{number}[/] [grey](pid {row.Pid}" +
            (string.IsNullOrEmpty(row.StageId) ? "" : $", stage {Markup.Escape(row.StageId)}") + ")[/]");
        AnsiConsole.MarkupLine($"[grey]Stream: {Markup.Escape(stream)}[/]");
        var prompt = Path.Combine(plan.StateDir, "logs", BgLogs.PromptName(number!.Value));
        if (File.Exists(prompt))
            AnsiConsole.MarkupLine($"[grey]Prompt: {Markup.Escape(prompt)}[/]");

        try
        {
            var lines = SessionStreamTail.Render(stream, AgentProviderFactory.Create(plan.Agent), tail);
            AnsiConsole.WriteLine();
            foreach (var line in lines) Console.WriteLine(line);
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Cannot read stream: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        return 0;
    }
}
