using Conductor.Core.Budget;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>The spend rail of the run loop: what the ceiling is at this instant, and what happens when
/// the run reaches it. Split out of RunLoop.Plumbing.cs (KS5.4) so the plan swap, the cap comparison
/// and the un-park that a raised cap triggers are one responsibility in one file — and so neither of
/// the two 490-line partials had to grow to hold it.</summary>
public sealed partial class RunLoop
{
    /// <summary>
    /// KS5.4 — the session boundary, in the one order that makes the cap honest: swap the plan first,
    /// then compare against it.
    /// <para>The swap may only happen at the top of the loop (G3.2 — anywhere else an agent may be
    /// running against the old stage graph), and the cap check used to sit at the BOTTOM, after the
    /// session. So a `plan reload` raising <c>limits.maxRunCostUsd</c>, queued while the session that
    /// would trip the old cap was still running, arrived one turn too late: the run parked on a ceiling
    /// the operator had already raised, and then had to be approved past a park that should never have
    /// happened. The bottom-of-loop check now stands aside when a reload is pending
    /// (<see cref="Commands.ControlDispatcher.ReloadPending"/>) and this is where the comparison lands
    /// instead — after the swap, against the plan actually in force.</para>
    /// </summary>
    /// <returns>true when the loop should start its next turn immediately: either the run just parked
    /// on its ceiling, or it is already parked and nothing has changed.</returns>
    private bool ReloadThenCheckCap()
    {
        if (!Dispatcher.ConsumeReloadPending() && !PlanFileChangedOnDisk()) return false;
        ApplyPlanReload();
        return !_ctx.Options.DryRun && CheckBudgetCap();
    }

    /// <summary>
    /// Has this run reached the ceiling it is governed by? The ceiling is the plan's cap PLUS every
    /// dollar (or token) an owner has approved on top of it — <see cref="RunContext.EffectiveMaxRunCostUsd"/>
    /// — so an approval widens this comparison instead of resetting the number on the left of it, and
    /// the comparison stays monotone for the life of the run.
    /// </summary>
    private bool CheckBudgetCap()
    {
        var costCap = _ctx.EffectiveMaxRunCostUsd;
        var tokenCap = _ctx.EffectiveMaxRunTokens;
        var overCost = costCap is { } cc && _ctx.BilledWindowUsd >= cc;
        var overTokens = tokenCap is { } tc && _ctx.RunTokens >= tc;
        if (!overCost && !overTokens) return false;

        // Already parked on exactly this: the reload path re-checks after every swap, and a park that
        // announces itself twice reads as two parks. Still "true" — the loop must idle, not spend.
        if (_ctx.State.Status == RunStatus.AwaitingOwner && _ctx.State.AwaitingOwnerReason == AwaitingOwnerReason.Budget)
            return true;

        _ctx.Events.Emit(new OwnerApprovalRequested { StageId = _ctx.State.CurrentStage ?? "?" });
        _ctx.State.Status = RunStatus.AwaitingOwner;
        _ctx.State.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
        // KS5.2: BilledWindowUsd, not RunCostUsd — a run whose spend was all lanes and advisors could
        // never reach its own ceiling. See RunContext.BilledWindowUsd for what is in the total and why.
        _ctx.Log(overCost
            ? $"budget cap: {BudgetCeiling.Usd(_ctx.BilledWindowUsd)} >= {BudgetCeiling.Usd(costCap!.Value)}{RaisedNote()} — awaiting owner approval to continue"
            : $"token cap: {BudgetCeiling.Tokens(_ctx.RunTokens)} >= {BudgetCeiling.Tokens(tokenCap!.Value)}{RaisedNote()} — awaiting owner approval to continue");
        _saveAndReport();
        return true;
    }

    /// <summary>Says, in the park line itself, that the ceiling being quoted is not the one in the plan
    /// file. Without it an operator reading "$6.00 (limit)" against a plan that says 3.00 has to go
    /// looking for the difference.</summary>
    private string RaisedNote()
        => _ctx.State.BudgetApprovals > 0
            ? $" (limit, raised over {_ctx.State.BudgetApprovals} approval(s))"
            : " (limit)";

    /// <summary>
    /// KS5.4 — a reloaded plan whose ceiling now clears this run's spend un-parks a budget park, the way
    /// G3.3 un-parks a session-cap park. The operator's Settings edit IS the approval in that case, and
    /// making them type `approve` afterwards to acknowledge a limit they have just personally raised is
    /// ceremony.
    /// <para>Deliberately narrow: only a park whose reason is <see cref="AwaitingOwnerReason.Budget"/>,
    /// and only when BOTH halves of the ceiling are now clear. An operator pause, a NeedsHuman park and
    /// an owner gate are all left exactly where they are — a reload is not a resume.</para>
    /// <para>Static over the context for the same reason <see cref="ApplyStartPause"/> is: the whole
    /// method is a decision about state, and taking it out of the loop's instance is what lets a test
    /// state each of those exclusions directly instead of driving a live run to reach them.</para>
    /// </summary>
    /// <returns>true when this call un-parked the run.</returns>
    internal static bool ResumeIfBudgetParkCleared(RunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.State.Status != RunStatus.AwaitingOwner
            || ctx.State.AwaitingOwnerReason != AwaitingOwnerReason.Budget) return false;

        var costCap = ctx.EffectiveMaxRunCostUsd;
        var tokenCap = ctx.EffectiveMaxRunTokens;
        if (costCap is { } cc && ctx.BilledWindowUsd >= cc) return false;
        if (tokenCap is { } tc && ctx.RunTokens >= tc) return false;

        ctx.State.AwaitingOwnerReason = null;
        ctx.State.Status = RunStatus.Idle;
        ctx.State.SetAttention(null);
        var ceiling = costCap is { } c ? BudgetCeiling.Usd(c) : "no cost cap";
        ctx.Log($"spend ceiling raised by the reloaded plan to {ceiling} — " +
                $"{BudgetCeiling.Usd(ctx.BilledWindowUsd)} spent is inside it again, resuming");
        return true;
    }

    /// <summary>W3.3: an unbounded run is a policy choice, not a default anyone opted into. The
    /// U-series run had no cap and spent $139.68 before dying, so the run says so out loud at start
    /// (and `doctor` warns). Caps stay the owner's to set — nothing is invented here.</summary>
    private void WarnOnUnboundedSpend()
    {
        if (_ctx.Options.DryRun) return;
        if (_ctx.Plan.Limits.MaxRunCostUsd.HasValue || _ctx.Plan.Limits.MaxRunTokens.HasValue) return;
        _ctx.Log("⚠ no spend cap: limits.maxRunCostUsd and limits.maxRunTokens are both unset — " +
                 "this run can spend without bound (set one in the plan, or in the Face's Plan tab)");
    }
}
