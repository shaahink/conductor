using System.ComponentModel;

using Conductor.Core.Planning;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

public class PlanSettings : CommandSettings
{
    [CommandOption("-p|--plan <PLAN>")]
    [Description("Path to the plan JSON. Falls back to CONDUCTOR_PLAN env var, then scanning the cwd/./plans for *.plan.json")]
    public string? Plan { get; init; }

    /// <summary>U0.1: -p wins, then CONDUCTOR_PLAN, then discovery — exactly one <c>*.plan.json</c> in
    /// the cwd, else exactly one under <c>./plans/</c>, else a picker (interactive console) or a
    /// listing error (redirected output), else a friendly "nothing found" pointing at `conductor init`.
    /// The discovery scan itself (<see cref="PlanDiscovery"/>) is pure and unit-tested; this method is
    /// the thin, untestable console/throw shell around it.</summary>
    public string ResolvePlanPath()
    {
        if (Plan != null) return Plan;

        var env = Environment.GetEnvironmentVariable("CONDUCTOR_PLAN");
        if (env != null) return env;

        var candidates = PlanDiscovery.Discover(Directory.GetCurrentDirectory());
        switch (candidates.Count)
        {
            case 1:
                AnsiConsole.MarkupLine($"[grey]using {Markup.Escape(candidates[0].Path)}[/]");
                return candidates[0].Path;

            case > 1 when !Console.IsInputRedirected && !Console.IsOutputRedirected:
                var chosen = AnsiConsole.Prompt(
                    new SelectionPrompt<PlanDiscovery.Candidate>()
                        .Title("Multiple plans found — pick one:")
                        .UseConverter(c => $"{c.Name}  ({c.Path})")
                        .AddChoices(candidates));
                return chosen.Path;

            case > 1:
                throw new InvalidOperationException(
                    "Multiple plan files found and output is not interactive to prompt:\n  - " +
                    string.Join("\n  - ", candidates.Select(c => $"{c.Name} ({c.Path})")) +
                    "\nUse --plan <path> or set CONDUCTOR_PLAN to choose one.");

            default:
                throw new InvalidOperationException(
                    "No plan found. Use --plan <path>, set CONDUCTOR_PLAN, or place a *.plan.json in the " +
                    "cwd or ./plans/. New here? Run `conductor init` to scaffold one.");
        }
    }
}
