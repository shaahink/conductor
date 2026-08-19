using System.ComponentModel;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// D2 — Ad-hoc gate re-run at HEAD without spawning an agent session. Re-runs the plan's
/// gate battery directly and reports PASS/FAIL. If all required gates pass and a
/// <see cref="RunState.PendingFix"/> exists, it is cleared and the state set to Idle.
/// </summary>
public sealed class GateCommand : Command<GateCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--full")]
        [Description("Run the full battery (not just fast-tier gates). Default: fast-tier only.")]
        public bool Full { get; init; }
    }

#pragma warning disable MA0045 // sync file I/O at Spectre.Cli sync boundary (same pattern as RunCommand/StatusCommand)
    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var logPath = Path.Combine(plan.StateDir, "conductor.log");

        LogGateEvent(logPath, $"gate {(settings.Full ? "FULL" : "fast")} battery starting @ HEAD {Git.Head(plan.Repo)}");

        var fastOnly = !settings.Full;
        var ct = CancellationToken.None;
        var gates = GateRunner.RunAllAsync(plan, msg => LogGateEvent(logPath, msg), ct, fastOnly, state.CurrentStage, null)
            .GetAwaiter().GetResult();

        var allGreen = GateRunner.AllRequiredPassed(gates);
        var summary = GateRunner.Summary(gates);

        // Report results
        var verdict = allGreen ? "[green]PASS[/]" : "[red]FAIL[/]";
        AnsiConsole.MarkupLine($"[bold aqua]conductor gate[/] ({ (settings.Full ? "full" : "fast") }): {verdict} — {Markup.Escape(summary)}");
        foreach (var g in gates)
        {
            // KS4.2/KS4.3: the class glyph FIRST. A gate red for its class exited 0, so every
            // branch below would print it green and the one line this verb exists to show would be
            // the one it hides.
            var icon = g.HasClassFailure ? $"[red]{g.Glyph}[/]"
                : g.Skipped ? "[grey]-[/]"
                : g.Passed ? "[green]OK[/]"
                : g.Optional ? "[yellow]warn[/]"
                : "[red]FAIL[/]";
            AnsiConsole.MarkupLine($"  {icon} {Markup.Escape(g.Name)} ({g.Duration.TotalSeconds:0.0}s)");
            if (g.HasClassFailure)
                AnsiConsole.WriteLine(g.HasRegressions ? GateRunner.RegressionDetail(g) : GateRunner.MutationDetail(g));
            else if (!g.Passed && !g.Skipped)
                AnsiConsole.WriteLine(g.Tail);
        }

        LogGateEvent(logPath, $"gate battery done — {GateRunner.Token(gates)}: {summary}");

        // If all green and previously-red, clear pendingFix
        if (allGreen && state.PendingFix != null)
        {
            state.PendingFix = null;
            state.Status = RunStatus.Idle;
            state.SetAttention(null);
            state.Save(statePath);
            LogGateEvent(logPath, "gate: all green — cleared pendingFix, set Idle");
            AnsiConsole.MarkupLine("[green]Pending fix cleared — state set to Idle.[/]");
        }
        else if (allGreen)
        {
            AnsiConsole.MarkupLine("[green]All gates passed.[/]");
        }
        else
        {
            var details = GateRunner.FailureDetails(gates);
            LogGateEvent(logPath, $"gate FAILURE details:\n{details}");
        }

        return allGreen ? 0 : 1;
    }

    private static void LogGateEvent(string logPath, string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        try { File.AppendAllText(logPath, stamped + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
#pragma warning restore MA0045
}
