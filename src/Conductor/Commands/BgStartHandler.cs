using System.Diagnostics;

using Conductor.Core;
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
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var runId = state.RunId ?? "bg-standalone";

        var exe = cmdArgs[0];
        var exeArgs = cmdArgs.Skip(1).ToList();
        var purpose = settings.Purpose ?? Path.GetFileNameWithoutExtension(exe);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = settings.Cwd ?? plan.Repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in exeArgs) psi.ArgumentList.Add(a);

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

        var logDir = Path.Combine(plan.StateDir, "bg-logs");
        Directory.CreateDirectory(logDir);
        var safePurpose = SanitizeFileName(purpose);
        var logPath = Path.Combine(logDir, $"{safePurpose}-{proc.Id}.log");

        // Fire-and-forget log capture: the Process object stays alive inside the closure.
        // The StreamWriter is disposed in the fire-and-forget task below — ownership transfers.
#pragma warning disable CA2000
        var logWriter = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
#pragma warning restore CA2000
        var gate = new Lock();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) logWriter.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) logWriter.WriteLine($"[stderr] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        // Track in run.db
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (File.Exists(runDbPath))
        {
            try
            {
                using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
                db.TrackPid(proc.Id, runId, $"bg:{purpose}", state.CurrentStage,
                    state.SessionCounter > 0 ? state.SessionCounter : null, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Started but run.db tracking failed: {Markup.Escape(ex.Message)}[/]");
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await proc.WaitForExitAsync().ConfigureAwait(false);
                var exitCode = 0;
                try { exitCode = proc.ExitCode; } catch { }
                if (File.Exists(runDbPath))
                {
                    try
                    {
                        using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
                        db.MarkPidExited(proc.Id, exitCode);
                    }
                    catch { }
                }
            }
            catch { }
            finally { try { await logWriter.DisposeAsync().ConfigureAwait(false); } catch { } }
        });

        AnsiConsole.MarkupLine($"[green]bg started[/] PID={proc.Id} purpose=[bold]{Markup.Escape(purpose)}[/]");
        AnsiConsole.MarkupLine($"  log: [grey]{Markup.Escape(logPath)}[/]");
        return 0;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars);
        return string.IsNullOrWhiteSpace(result) ? "bg-process" : result;
    }
}
