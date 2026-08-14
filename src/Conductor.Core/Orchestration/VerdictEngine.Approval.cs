using Conductor.Core.Budget;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class VerdictEngine
{
    /// <summary>
    /// The single entry point every approval ingress funnels into — the TUI's R key, `conductor
    /// approve`, Telegram's /approve and the HTTP control POST. What it does is decided by WHY the run
    /// parked (<see cref="OwnerApproval.Decide"/>), never by which ingress asked.
    /// </summary>
    /// <param name="amount">KS5.4 — how much to raise the ceiling by, in
    /// <see cref="BudgetCeiling.ParseRaise"/>'s vocabulary. Null on every ingress that cannot carry one
    /// (a keypress, a chat command), which is the ordinary case and has a stated default. Meaningless on
    /// an owner-gate or approval-mode park, where it is REFUSED rather than ignored: a number an
    /// operator typed and the tool silently dropped is worse than an error.</param>
    internal async Task ApproveAwaitingOwnerAsync(string? amount, CancellationToken ct)
    {
        var stageId = _ctx.State.CurrentStage
            ?? _ctx.Plan.Stages.FirstOrDefault(s => !_ctx.State.ConfirmedStages.Contains(s.Id) && !_ctx.State.SkippedStages.Contains(s.Id))?.Id;
        var outcome = OwnerApproval.Decide(_ctx.State.AwaitingOwnerReason);
        if (outcome != ApprovalOutcome.RaiseCeilingAndResume && !string.IsNullOrWhiteSpace(amount))
        {
            Refuse($"this run is parked on {_ctx.State.AwaitingOwnerReason?.ToString() ?? "an owner gate"}, " +
                   "not on a budget — there is no ceiling for an amount to raise. Approve without one.");
            return;
        }

        switch (outcome)
        {
            case ApprovalOutcome.ResumeSession:
                _ctx.SessionApproved = true;
                _ctx.State.AwaitingOwnerReason = null;
                _ctx.State.Status = RunStatus.Idle;
                if (stageId != null) _ctx.Events.Emit(new OwnerApprovalGranted { StageId = stageId });
                _ctx.Save();
                _ctx.Log("owner approved (approval mode) — running the next session");
                break;
            case ApprovalOutcome.RaiseCeilingAndResume:
                RaiseBudgetCeiling(amount, stageId);
                break;
            default:
                if (stageId == null) { _ctx.State.Status = RunStatus.Idle; _ctx.Save(); break; }
                if (!_ctx.State.OwnerApprovedStages.Contains(stageId))
                {
                    _ctx.Events.Emit(new OwnerApprovalGranted { StageId = stageId });
                    _ctx.State.OwnerApprovedStages.Add(stageId);
                    _ctx.Log($"owner approved stage {stageId} — continuing");
                }
                await ConfirmStageAsync(stageId, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// KS5.4 — an owner approving past a budget park RAISES the ceiling, by an amount this line states,
    /// and touches no counter.
    /// <para>What it replaced: the approval used to zero <c>PerRunCostUsd</c>, <c>PerRunTokens</c>,
    /// <c>PerRunOverheadCostUsd</c> and <c>PerRunSideCostUsd</c>, then log "window reset to $0.00".
    /// The field log's 19:03 entry is what that costs — a $3.00 cap, $3.50 spent, one approval, and the
    /// run free to spend another $3.00 before anyone would hear from it again, with no surface anywhere
    /// naming $6.50 (or $7.00, once the second window closed). Every number in that line was true and
    /// the sentence they formed was not: an operator had to subtract two windows to learn what the run
    /// was allowed to spend.</para>
    /// <para>Now the run keeps ONE monotone spend and gets a bigger number to measure it against, so
    /// "spend vs cap" is a single comparison at every instant of the run's life. The default raise is
    /// the operator's own configured cap — one more of the ceiling they set, not a figure this code
    /// invented — and it is stated either way. The grant composes with a later <c>plan reload</c>
    /// because it is stored as a grant and not as an absolute (see
    /// <see cref="BudgetCeiling.EffectiveCostCap"/>).</para>
    /// <para>A raise that would land the ceiling at or under the spend already made is REFUSED, and the
    /// run stays parked: resuming there buys one more session and a second park, and a ceiling under the
    /// spend can only state its headroom as a negative. The refusal names the shortfall, so the operator
    /// types one number and is done.</para>
    /// </summary>
    private void RaiseBudgetCeiling(string? amount, string? stageId)
    {
        var (ok, request, error) = BudgetCeiling.ParseRaise(amount);
        if (!ok) { Refuse($"{error} — nothing was approved and the run is still parked"); return; }

        var planCost = _ctx.Plan.Limits.MaxRunCostUsd;
        var planTokens = _ctx.Plan.Limits.MaxRunTokens;
        var fromCost = _ctx.EffectiveMaxRunCostUsd;
        var fromTokens = _ctx.EffectiveMaxRunTokens;
        var spentUsd = _ctx.BilledWindowUsd;
        var spentTokens = _ctx.RunTokens;

        if (request.Usd is not null && planCost is null)
        {
            Refuse("this plan sets no limits.maxRunCostUsd, so there is no cost ceiling to raise — " +
                   "set one in the plan first, or approve without an amount");
            return;
        }
        if (request.Tokens is not null && planTokens is null)
        {
            Refuse("this plan sets no limits.maxRunTokens, so there is no token ceiling to raise — " +
                   "set one in the plan first, or approve without an amount");
            return;
        }

        // No amount given: raise every half the run has actually reached, by the cap the operator
        // configured for it. A half that is not blocking is left alone — approving past a money park
        // must not quietly double a token ceiling nobody complained about.
        var raiseCost = request.Usd ?? (request.IsEmpty && fromCost is { } fc && spentUsd >= fc ? planCost : null);
        var raiseTokens = request.Tokens ?? (request.IsEmpty && fromTokens is { } ft && spentTokens >= ft ? planTokens : null);

        // A raise that does not clear what the run has ALREADY spent is not an approval — it is a second
        // park one session later, on a ceiling the run was over before it started. It happens whenever
        // the overshoot is bigger than the default raise (the plan's own cap) or the operator types an
        // --amount smaller than it. The old reset semantics could not reach this state, because zeroing
        // the counter always cleared the park; raising the ceiling has to be able to say no.
        // The alternative — resume anyway — costs a whole session and can only state its headroom as a
        // negative, and "$-4.00 left" is not a sentence anybody can act on.
        var shortfalls = new List<string>();
        if (raiseCost is { } wantCost && fromCost is { } baseCost && baseCost + wantCost <= spentUsd)
            shortfalls.Add(
                $"raising the cost ceiling by {BudgetCeiling.Usd(wantCost)} would land it at " +
                $"{BudgetCeiling.Usd(baseCost + wantCost)}, still at or under the " +
                $"{BudgetCeiling.Usd(spentUsd)} this run has already spent — approve with an amount over " +
                $"{BudgetCeiling.Usd(spentUsd - baseCost)} to clear it");
        if (raiseTokens is { } wantTokens && fromTokens is { } baseTokens && baseTokens + wantTokens <= spentTokens)
            shortfalls.Add(
                $"raising the token ceiling by {BudgetCeiling.Tokens(wantTokens)} would land it at " +
                $"{BudgetCeiling.Tokens(baseTokens + wantTokens)}, still at or under the " +
                $"{BudgetCeiling.Tokens(spentTokens)} already counted — approve with an amount over " +
                $"{BudgetCeiling.Tokens(spentTokens - baseTokens)} to clear it");
        if (shortfalls.Count > 0)
        {
            Refuse(string.Join("; ", shortfalls) +
                   " — nothing was raised and the run is still parked");
            return;
        }

        var parts = new List<string>();
        if (raiseCost is { } dc && fromCost is { } beforeCost)
        {
            _ctx.State.BudgetGrantUsd += dc;
            var after = beforeCost + dc;
            parts.Add($"cost ceiling {BudgetCeiling.Usd(beforeCost)} -> {BudgetCeiling.Usd(after)} " +
                      $"(+{BudgetCeiling.Usd(dc)}); {BudgetCeiling.Usd(spentUsd)} already spent still counts, " +
                      $"{BudgetCeiling.Usd(after - spentUsd)} left");
        }
        if (raiseTokens is { } dt && fromTokens is { } beforeTokens)
        {
            _ctx.State.BudgetGrantTokens += dt;
            var after = beforeTokens + dt;
            parts.Add($"token ceiling {BudgetCeiling.Tokens(beforeTokens)} -> {BudgetCeiling.Tokens(after)} " +
                      $"(+{BudgetCeiling.Tokens(dt)}); {BudgetCeiling.Tokens(spentTokens)} already counted, " +
                      $"{BudgetCeiling.Tokens(after - spentTokens)} left");
        }

        _ctx.State.BudgetApprovals++;
        _ctx.State.BudgetWindowStartedUtc = DateTime.UtcNow;
        _ctx.State.BudgetRaises.Add(new BudgetRaise
        {
            Approval = _ctx.State.BudgetApprovals,
            WhenUtc = _ctx.State.BudgetWindowStartedUtc.Value,
            FromCostUsd = raiseCost is null ? null : fromCost,
            ToCostUsd = raiseCost is { } a && fromCost is { } b ? b + a : null,
            FromTokens = raiseTokens is null ? null : fromTokens,
            ToTokens = raiseTokens is { } c && fromTokens is { } d ? d + c : null,
            SpentUsd = spentUsd,
            SpentTokens = spentTokens,
        });
        _ctx.State.AwaitingOwnerReason = null;
        _ctx.State.Status = RunStatus.Idle;
        _ctx.State.SetAttention(null);
        if (stageId != null) _ctx.Events.Emit(new OwnerApprovalGranted { StageId = stageId });
        _ctx.Save();

        // Nothing to raise is a real answer, not a no-op: it happens when a reload already cleared the
        // park, and an operator who typed `approve` is owed a sentence saying so.
        var what = parts.Count > 0
            ? string.Join("; ", parts)
            : $"no ceiling is currently exceeded ({BudgetCeiling.Usd(spentUsd)} spent) — nothing raised";
        var line = $"owner approved (budget) — {what} — approval {_ctx.State.BudgetApprovals}, continuing";
        _ctx.Log(line);
        _ctx.Sink.Toast(new ToastMessage(what, LogSeverity.Success));
    }

    /// <summary>An approval this engine declines to act on. The run stays exactly where it is — the
    /// point of refusing rather than ignoring is that the operator finds out immediately, from the same
    /// two surfaces the approval itself would have spoken through.</summary>
    private void Refuse(string why)
    {
        _ctx.Log($"approve refused — {why}");
        _ctx.Sink.Toast(new ToastMessage($"approve refused — {why}", LogSeverity.Error));
    }
}
