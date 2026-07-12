using System.ComponentModel;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// P1 — Dynamic plan reconfiguration: plan set, reload, add-stage. Subcommands dispatch to
/// Set / Reload / AddStage; a bare `conductor plan` prints the current plan summary.
/// </summary>
public sealed class PlanCommand : Command<PlanCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: set, reload, or add-stage. Omit to show plan summary.")]
        public string Verb { get; init; } = "";

        [CommandArgument(1, "[KEY]")]
        [Description("Dot-notation key path (set only, e.g. limits.maxRunCostUsd).")]
        public string? Key { get; init; }

        [CommandArgument(2, "[VALUE]")]
        [Description("New value (set only).")]
        public string? Value { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var verb = settings.Verb.ToLowerInvariant();
        return verb switch
        {
            "set" => PlanSetCommand.ExecuteSet(settings.ResolvePlanPath(), settings.Key, settings.Value),
            "reload" => PlanReloadCommand.ExecuteReload(settings.ResolvePlanPath()),
            "add-stage" => PlanAddStageCommand.ExecuteAddStage(settings.ResolvePlanPath(), settings),
            "import" => PlanImportCommand.ExecuteImport(settings.ResolvePlanPath(), settings.Key),
            _ => PrintPlanSummary(settings),
        };
    }

    private static int PrintPlanSummary(Settings settings)
    {
        try
        {
            var planPath = settings.ResolvePlanPath();
            var plan = PlanConfig.Load(planPath);
            AnsiConsole.MarkupLine($"[bold aqua]conductor plan[/] — [bold]{Markup.Escape(plan.Name)}[/] v{plan.PlanVersion}");
            AnsiConsole.MarkupLine($"repo: {Markup.Escape(plan.Repo)}");
            AnsiConsole.MarkupLine($"stages: {plan.Stages.Count}   gates: {plan.Gates.Count}   gate-policy: {plan.GatePolicy}");
            AnsiConsole.MarkupLine($"limits: stall={plan.Limits.StallMinutes}m timeout={plan.Limits.SessionTimeoutMinutes}m backoff={plan.Limits.BackoffMinutes}m");
            if (plan.Limits.MaxRunCostUsd is { } cap) AnsiConsole.MarkupLine($"cost-cap: ${cap:0.00}");
            if (plan.Limits.MaxRunTokens is { } tok) AnsiConsole.MarkupLine($"token-cap: {tok / 1000}K");
            AnsiConsole.MarkupLine($"plan: [grey]{Markup.Escape(planPath)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]sub-commands: plan set <key> <value> | plan reload | plan add-stage[/]");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
