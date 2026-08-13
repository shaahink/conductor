using System.ComponentModel;

using Conductor.Core.Planning;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

public class PlanSettings : CommandSettings
{
    [CommandOption("-p|--plan <PLAN>")]
    [Description("Path to the plan JSON. Falls back to a single *.plan.json in the cwd (or ./plans), then the CONDUCTOR_PLAN env var")]
    public string? Plan { get; init; }

    /// <summary>U0.1, amended by KS0.3 (bug #20): <c>-p</c> wins, then a cwd that names exactly one
    /// plan, then <c>CONDUCTOR_PLAN</c>, then discovery — exactly one <c>*.plan.json</c> in the cwd,
    /// else exactly one under <c>./plans/</c>, else a picker (interactive console) or a listing error
    /// (redirected output), else a friendly "nothing found" pointing at `conductor init`.
    /// The scan (<see cref="PlanDiscovery"/>) and the precedence rule (<see cref="PlanResolution"/>)
    /// are both pure and unit-tested; this method is the thin console/throw shell around them.</summary>
    public string ResolvePlanPath()
    {
        if (Plan != null) return Plan;

        var candidates = PlanDiscovery.Discover(Directory.GetCurrentDirectory());
        var choice = PlanResolution.Decide(
            Plan, Environment.GetEnvironmentVariable("CONDUCTOR_PLAN"), candidates);

        // The warning goes to stderr on purpose: a --json verb must stay parseable, and the operator
        // still sees it. Silence here is how a scratch rig edits the plan that spawned it.
        if (choice.Warning is { Length: > 0 } warning) Console.Error.WriteLine("warning: " + warning);
        if (choice.Note is { Length: > 0 } note) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(note)}[/]");
        if (choice.Path is { Length: > 0 } path) return path;

        switch (candidates.Count)
        {
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
