using Conductor.Core.Budget;
using Conductor.Models;

namespace Conductor.Commands;

/// <summary>The money check, split out of DoctorCommand.cs by the KS5.4 round-3 pass — the main file
/// was at the 500-line ceiling and this is doctor's one self-contained verdict: what ceiling is this
/// run governed by, and where does its spend stand against it.</summary>
public sealed partial class DoctorCommand
{
    /// <param name="budgetGrantUsd">KS5.4 — dollars an owner has already approved on top of
    /// <c>limits.maxRunCostUsd</c> for this run (<see cref="RunState.BudgetGrantUsd"/>). Doctor reports on
    /// the ceiling the run is GOVERNED by, which is the plan's cap plus that grant
    /// (<see cref="BudgetCeiling.EffectiveCostCap"/> — the same function the cap check, <c>/state</c> and
    /// the run report read). Left on the plan's figure, doctor called a run "fail — the run will park at
    /// AwaitingOwner" about a run that had been approved past exactly that park and would not park at
    /// all. Explicit rather than defaulted: a caller that forgets it re-introduces that.</param>
    /// <param name="budgetGrantTokens">The token half of the same grant
    /// (<see cref="RunState.BudgetGrantTokens"/>): the no-cost-cap branch quotes the token ceiling, and
    /// quoting the plan's raw figure there was this defect wearing the other half's numbers. Explicit
    /// for the same reason.</param>
    internal static Check CheckBudget(
        PlanConfig plan, decimal currentCostUsd, bool hasRun, decimal budgetGrantUsd, long budgetGrantTokens)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // W3.3: unbounded is a choice, and a defensible one — but silence made it a default nobody
        // picked. The U-series run had no cap and spent $139.68 before it died.
        if (BudgetCeiling.EffectiveCostCap(plan.Limits.MaxRunCostUsd, budgetGrantUsd) is not { } cap)
        {
            if (BudgetCeiling.EffectiveTokenCap(plan.Limits.MaxRunTokens, budgetGrantTokens) is { } tokenCap)
            {
                var tokensRaised = budgetGrantTokens > 0 && plan.Limits.MaxRunTokens is { } configuredTokens
                    ? $" (raised from {BudgetCeiling.Tokens(configuredTokens)} by owner approval)" : "";
                return new Check("budget", "ok", $"no cost cap, token cap {BudgetCeiling.Tokens(tokenCap)}{tokensRaised}");
            }
            return new Check("budget", "warn", "no spend cap — set limits.maxRunCostUsd (or maxRunTokens) unless unbounded is deliberate");
        }

        // Where the ceiling came from, when it is not the number in the plan file — otherwise an operator
        // reading "cap $6.00" against a plan that says 3.00 goes looking for the difference.
        var raised = budgetGrantUsd > 0m && plan.Limits.MaxRunCostUsd is { } configured
            ? $" (raised from ${configured:0.00} by owner approval)" : "";
        if (!hasRun)
            return new Check("budget", "ok", $"cap ${cap:0.00}{raised}, no run yet");
        // "Will it park" is the loop's own question, so it is asked through the loop's one predicate.
        if (BudgetCeiling.Standing(cap, currentCostUsd, null, 0L).OverCost)
            return new Check("budget", "fail", $"${currentCostUsd:0.00} ≥ cap ${cap:0.00}{raised} — raise limits.maxRunCostUsd or the run will park at AwaitingOwner");

        var pct = cap > 0 ? (double)(currentCostUsd / cap) * 100 : 0;
        return pct >= 80
            ? new Check("budget", "warn", $"${currentCostUsd:0.00} / ${cap:0.00}{raised} ({pct:0}%) — approaching the cap")
            : new Check("budget", "ok", $"${currentCostUsd:0.00} / ${cap:0.00}{raised} ({pct:0}%)");
    }
}
