using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using Conductor.Core.Budget;
using Conductor.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// Approve whatever the run is waiting for. What that means is decided by the ENGINE, from why the run
/// parked — an owner gate confirms the stage, an approval-mode park runs the next session, a budget
/// park raises the ceiling — so this verb never has to guess which kind of approval it is issuing.
/// <para>KS5.4 made it more than a one-line <see cref="CtlCommand"/> subclass: a budget approval can
/// carry the amount to raise the ceiling BY, and that amount is the one thing an approval can do that
/// spends money. It rides the control file's shared <c>value</c> field under the word <c>approve</c>,
/// which <c>ControlFile.Parse</c> keeps out of a plain <c>resume</c>.</para>
/// </summary>
public sealed class ApproveCommand : Command<ApproveCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--amount <USD>")]
        [Description("Budget park only: raise the run's cost ceiling by this many dollars. Omit to raise it by one more of the plan's own limits.maxRunCostUsd — whichever it is, the run's log and the toast state the new ceiling.")]
        public string? Amount { get; init; }

        [CommandOption("--tokens <N>")]
        [Description("Budget park only: raise the run's token ceiling by this many tokens. Omit to raise it by one more of the plan's own limits.maxRunTokens.")]
        public string? Tokens { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Parsed HERE as well as in the engine, because a typo in an amount should cost the operator a
        // line of stderr rather than a control file the run picks up, refuses, and stays parked on.
        var value = Compose(settings);
        if (BudgetCeiling.ParseRaise(value) is { Ok: false, Error: var error })
        {
            AnsiConsole.MarkupLine($"[red]approve: {Markup.Escape(error ?? "unusable amount")}[/]");
            return 2;
        }

        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new
            {
                command = "approve",
                issuedUtc = DateTime.UtcNow,
                value = value.Length > 0 ? value : null,
            }));

        AnsiConsole.MarkupLine(value.Length > 0
            ? $"[green]approve[/] queued — raising this run's ceiling by {Markup.Escape(Describe(settings))}; the run states the new ceiling in its log"
            : "[green]approve[/] queued — an owner gate advances, a budget park's ceiling rises by one more of the plan's own cap (the run states it)");
        return 0;
    }

    /// <summary>The two flags, in the one string the control file carries. Empty when neither was
    /// given, which is a request in its own right: raise by the operator's own configured cap.</summary>
    private static string Compose(Settings s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Amount)) parts.Add("usd=" + s.Amount.Trim().TrimStart('$'));
        if (!string.IsNullOrWhiteSpace(s.Tokens)) parts.Add("tokens=" + s.Tokens.Trim());
        return string.Join(";", parts);
    }

    private static string Describe(Settings s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Amount)
            && decimal.TryParse(s.Amount.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var usd))
            parts.Add(BudgetCeiling.Usd(usd));
        if (!string.IsNullOrWhiteSpace(s.Tokens)
            && long.TryParse(s.Tokens.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens))
            parts.Add(BudgetCeiling.Tokens(tokens));
        return string.Join(" + ", parts);
    }
}
