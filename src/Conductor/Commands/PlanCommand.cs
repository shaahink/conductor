using System.ComponentModel;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// P1 — Dynamic plan reconfiguration: plan set, reload, add-stage, import; and, since KS3.1, the
/// authoring end of the same verb, <c>plan new</c>. Sub-commands dispatch by name; a bare
/// <c>conductor plan</c> prints the current plan summary, and a name the dispatcher does not know is
/// refused rather than quietly answered with that summary.
/// </summary>
public sealed class PlanCommand : Command<PlanCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: new, set, reload, add-stage, or import. Omit to show plan summary.")]
        public string Verb { get; init; } = "";

        [CommandArgument(1, "[KEY]")]
        [Description("Dot-notation key path (set), the import source (import), or the idea/document (new).")]
        public string? Key { get; init; }

        [CommandArgument(2, "[VALUE]")]
        [Description("New value (set only).")]
        public string? Value { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description("Model to use when importing freeform prose (import only). Fills a {model} placeholder in advisor args.")]
        public string? Model { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Apply an import without the confirm prompt (import only).")]
        public bool Yes { get; init; }

        [CommandOption("--create")]
        [Description("Write a key the plan schema does not declare (set only). Without it, an undeclared key is refused — nothing reads one.")]
        public bool Create { get; init; }

        // ---- KS3.1: `plan new` ------------------------------------------------------------------

        [CommandOption("-o|--output <DIR>")]
        [Description("Directory to scaffold into (new only). Created if missing. Default: cwd.")]
        public string? Output { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Plan name (new only). Default: the output directory's name.")]
        public string? Name { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Absolute path to the repo to drive (new only). Default: the output directory.")]
        public string? Repo { get; init; }

        [CommandOption("--from-idea <TEXT_OR_FILE>")]
        [Description("The idea, PRD or existing tracker to build the plan from (new only). Quoted prose, or a path to a document.")]
        public string? FromIdea { get; init; }

        [CommandOption("--agent <COMMAND>")]
        [Description("Agent CLI to write into the scaffold (new only). Default: whichever of claude/opencode this machine has.")]
        public string? Agent { get; init; }

        [CommandOption("--advisor <COMMAND>")]
        [Description("Enable the advisor in the scaffold, pointed at this CLI (new only) — what freeform prose needs to become stages. Omit and the advisor stays a commented hint.")]
        public string? Advisor { get; init; }
    }

    /// <summary>The sub-commands this verb dispatches, in the order they are offered. Named once so
    /// the refusal, the summary footer and the help text cannot drift apart.</summary>
    private static readonly string[] KnownVerbs = ["new", "set", "reload", "add-stage", "import"];

    public override int Execute(CommandContext context, Settings settings) => Dispatch(settings);

    /// <summary>The dispatch, separated from Spectre's entry point so the routing itself is testable:
    /// a <c>CommandContext</c> cannot be constructed from a test, and the thing worth pinning here is
    /// which sub-command a name reaches — including the ones that reach none.</summary>
    internal static int Dispatch(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var verb = settings.Verb.Trim().ToLowerInvariant();
        return verb switch
        {
            "" => PrintPlanSummary(settings),
            "new" => PlanNewCommand.Execute(settings),
            "set" => PlanSetCommand.ExecuteSet(settings.ResolvePlanPath(), settings.Key, settings.Value, settings.Create),
            "reload" => PlanReloadCommand.ExecuteReload(settings.ResolvePlanPath()),
            "add-stage" => PlanAddStageCommand.ExecuteAddStage(settings.ResolvePlanPath(), settings),
            "import" => PlanImportCommand.ExecuteImport(settings.ResolvePlanPath(), settings.Key, settings.Model, settings.Yes),
            _ => UnknownVerb(verb),
        };
    }

    /// <summary>KS3.1 — a mistyped sub-command used to fall into the summary, which prints happily and
    /// exits 0: <c>conductor plan improt PRD.md</c> reported the plan and imported nothing, and the
    /// only signal was the absence of the stages you asked for. A verb the dispatcher does not know is
    /// a refusal that names the ones it does.</summary>
    internal static int UnknownVerb(string verb)
    {
        foreach (var line in UnknownVerbMessage(verb).Split('\n'))
            AnsiConsole.MarkupLine(Markup.Escape(line));
        return 1;
    }

    /// <summary>The refusal as text, so a test can assert it names every sub-command without capturing
    /// a process-global console (bug #26).</summary>
    internal static string UnknownVerbMessage(string verb) =>
        $"unknown plan sub-command '{verb}'.\n" +
        $"Known sub-commands: {string.Join(", ", KnownVerbs)}. `conductor plan` on its own prints the plan summary.";

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
            AnsiConsole.MarkupLine($"[grey]sub-commands: {string.Join(" | ", KnownVerbs.Select(v => "plan " + v))}[/]");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
