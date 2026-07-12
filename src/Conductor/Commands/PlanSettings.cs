using System.ComponentModel;

using Spectre.Console.Cli;

namespace Conductor.Commands;

public class PlanSettings : CommandSettings
{
    [CommandOption("-p|--plan <PLAN>")]
    [Description("Path to the plan JSON. Falls back to CONDUCTOR_PLAN env var, then ./conductor.plan.json")]
    public string? Plan { get; init; }

    public string ResolvePlanPath()
    {
        var p = Plan
                ?? Environment.GetEnvironmentVariable("CONDUCTOR_PLAN")
                ?? (File.Exists("conductor.plan.json") ? "conductor.plan.json" : null);
        if (p == null)
            throw new InvalidOperationException("No plan given. Use --plan <path>, set CONDUCTOR_PLAN, or place conductor.plan.json in the cwd.");
        return p;
    }
}
