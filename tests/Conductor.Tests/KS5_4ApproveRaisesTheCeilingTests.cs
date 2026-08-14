using System.Text;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Http;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS5.4 — an owner approving past a budget park RAISES the ceiling, by an amount that is stated, and
/// zeroes nothing.
///
/// <para>The incident being closed is the field log's 2026-07-29 19:03 entry, which survives verbatim
/// in <c>.conductor/evidence/SC2/SC2.3-live-spend.md</c> section 4:
/// <c>owner approved (budget) - window reset to $0.00 after $3.50 / 79.8k; lifetime spend is still
/// $3.50 over 2 session(s) - approval 1, continuing</c>. Every number in that line is true. The
/// sentence they form is not: a $3.00 cap has just permitted $7.00, and no surface anywhere — not the
/// log, not <c>/state</c>, not the report — names the ceiling now in force, because there wasn't one.
/// The run had a fresh $3.00 to spend and the only way to know it was to work out that the counter had
/// been deleted.</para>
///
/// <para>So the counters stay, and the ceiling moves: <c>limits.maxRunCostUsd</c> plus every grant an
/// owner has approved on top of it. Spend-vs-cap is then one monotone comparison for the life of the
/// run, which is the property every assertion in this file is really about. The default grant is the
/// operator's own configured cap — one more of the ceiling they set — because the one thing this verb
/// must never do is pick a number nobody typed.</para>
/// </summary>
public sealed class KS5_4ApproveRaisesTheCeilingTests : IDisposable
{
    private const string RunId = "run-ks54-0001";

    private readonly string _tmp;
    private readonly List<IDisposable> _open = [];

    public KS5_4ApproveRaisesTheCeilingTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks54-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        foreach (var d in _open) { try { d.Dispose(); } catch (ObjectDisposedException) { } }
        try { TestTemp.DeleteTree(_tmp); } catch (IOException) { }
    }

    // ────────────────────────────── the ceiling arithmetic ──────────────────────────────

    /// <summary>The grant composes with the plan rather than replacing it, which is what lets a later
    /// `plan reload` raising the configured cap take effect WITHOUT throwing away an approval that
    /// happened before it. Storing the absolute ceiling instead would have made the two edits fight.</summary>
    [Fact]
    public void TheCeilingInForceIsThePlansCapPlusEveryApprovedRaise()
    {
        Assert.Equal(3.00m, BudgetCeiling.EffectiveCostCap(3.00m, 0m));
        Assert.Equal(6.00m, BudgetCeiling.EffectiveCostCap(3.00m, 3.00m));
        Assert.Equal(11.00m, BudgetCeiling.EffectiveCostCap(8.00m, 3.00m)); // the plan was raised later
        Assert.Equal(200_000L, BudgetCeiling.EffectiveTokenCap(100_000L, 100_000L));
    }

    /// <summary>No configured cap means no ceiling at all — a grant cannot invent one, because there is
    /// nothing for it to be a grant OF. "No cap" and "a very large cap" are different facts (K4.4's
    /// rule for CostRemaining) and this is where they would first be confused.</summary>
    [Fact]
    public void AGrantCannotInventACeilingOnAnUncappedRun()
    {
        Assert.Null(BudgetCeiling.EffectiveCostCap(null, 25.00m));
        Assert.Null(BudgetCeiling.EffectiveTokenCap(null, 999_999L));
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData("5", 5.0, null)]
    [InlineData("$4.50", 4.5, null)]
    [InlineData("usd=3.25", 3.25, null)]
    [InlineData("tokens=500000", null, 500000L)]
    [InlineData("usd=2;tokens=1000", 2.0, 1000L)]
    [InlineData("usd=2, tokens=1000", 2.0, 1000L)]
    public void AnApprovalsAmountIsParsedIntoTheHalvesItRaises(string value, double? usd, long? tokens)
    {
        var (ok, request, error) = BudgetCeiling.ParseRaise(value);
        Assert.True(ok, error);
        Assert.Equal(usd is { } u ? (decimal)u : null, request.Usd);
        Assert.Equal(tokens, request.Tokens);
    }

    /// <summary>An amount this verb cannot read is REFUSED with a reason. Rounding it, ignoring it, or
    /// falling back to the default would each turn a typo into money.</summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("usd=-2")]
    [InlineData("usd=0")]
    [InlineData("tokens=nope")]
    [InlineData("sessions=4")]
    public void AnUnreadableAmountIsRefusedWithAReasonRatherThanGuessed(string value)
    {
        var (ok, _, error) = BudgetCeiling.ParseRaise(value);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ────────────────────────────── the approval itself ──────────────────────────────

    /// <summary>The 19:03 replay, in the small: a $3.00 cap, $3.50 spent, one approval. What must NOT
    /// happen is the deletion — the four counters the old outcome zeroed are all still standing — and
    /// what must happen is a ceiling with a name, stated in the line the operator reads.</summary>
    [Fact]
    public async Task ApproveOnABudgetParkRaisesTheCeilingAndZeroesNothing()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.00m, sideUsd: 0.50m, tokens: 79_800);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        // Nothing was un-spent. This is the whole checkpoint.
        Assert.Equal(3.00m, rig.State.PerRunCostUsd);
        Assert.Equal(0.50m, rig.State.PerRunSideCostUsd);
        Assert.Equal(79_800, rig.State.PerRunTokens);
        Assert.Equal(3.50m, rig.State.BilledWindowCostUsd);
        Assert.Equal(3.50m, rig.Ctx.BilledWindowUsd);

        // The ceiling moved instead, by the operator's own configured cap, and the run is governed by it.
        Assert.Equal(3.00m, rig.State.BudgetGrantUsd);
        Assert.Equal(6.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(1, rig.State.BudgetApprovals);
        Assert.Equal(RunStatus.Idle, rig.State.Status);
        Assert.Null(rig.State.AwaitingOwnerReason);

        var raise = Assert.Single(rig.State.BudgetRaises);
        Assert.Equal(1, raise.Approval);
        Assert.Equal(3.00m, raise.FromCostUsd);
        Assert.Equal(6.00m, raise.ToCostUsd);
        Assert.Equal(3.50m, raise.SpentUsd);
        Assert.Null(raise.FromTokens);       // the token half was not the one that parked it
        Assert.Equal(raise.WhenUtc, rig.State.BudgetWindowStartedUtc);

        // The line the field log could not print, because there was no ceiling to name.
        var line = ApprovalLine(rig);
        Assert.Contains("cost ceiling $3.00 -> $6.00 (+$3.00)", line, StringComparison.Ordinal);
        Assert.Contains("$3.50 already spent still counts", line, StringComparison.Ordinal);
        Assert.Contains("$2.50 left", line, StringComparison.Ordinal);
        Assert.DoesNotContain("reset", line, StringComparison.OrdinalIgnoreCase);

        // …and the toast says the same thing, so the TUI operator is not the one reader left guessing.
        var toast = Assert.Single(rig.Sink.Toasts);
        Assert.Contains("$6.00", toast.Text, StringComparison.Ordinal);
        Assert.Equal(LogSeverity.Success, toast.Severity);
    }

    /// <summary>The run cannot silently spend another full cap: the ceiling after the approval is
    /// $6.00, not "$3.00 again from here", and the difference is exactly the spend the old reset
    /// forgave. Asserted as the comparison the loop makes, against the context the loop reads.</summary>
    [Fact]
    public async Task TheRaisedCeilingIsMeasuredFromTheCapNotFromTheSpendAlreadyMade()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 1_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        // The old behaviour: window zeroed, so the run had $3.00 of headroom from $3.50 — $6.50 total.
        // The new one: $2.50 of headroom, and the run parks again at $6.00 having spent $6.00.
        Assert.Equal(6.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.True(rig.Ctx.BilledWindowUsd < rig.Ctx.EffectiveMaxRunCostUsd);

        rig.Ctx.RunCostUsd = 6.00m;
        rig.Ctx.PersistBudget();
        Assert.True(rig.Ctx.BilledWindowUsd >= rig.Ctx.EffectiveMaxRunCostUsd,
            "a run that has spent the raised ceiling must be over it, not two thirds of the way through a second window");
    }

    /// <summary>An amount raises by exactly that much, and says so. Nothing else about the approval
    /// changes — the point of the flag is the number, not a different code path.</summary>
    [Fact]
    public async Task ApproveWithAnAmountRaisesByExactlyThatMuch()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 1_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=5", CancellationToken.None);

        Assert.Equal(5.00m, rig.State.BudgetGrantUsd);
        Assert.Equal(8.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(3.50m, rig.State.BilledWindowCostUsd);
        Assert.Contains("cost ceiling $3.00 -> $8.00 (+$5.00)",
            ApprovalLine(rig),
            StringComparison.Ordinal);
    }

    /// <summary>The token half of the park gets the same treatment, or the two halves of one park
    /// diverge: an owner clears the money ceiling and the very next boundary parks them again on a
    /// token ceiling nobody mentioned.</summary>
    [Fact]
    public async Task TheTokenHalfOfAParkIsRaisedTheSameWay()
    {
        var rig = Rig(costCap: null, tokenCap: 100_000);
        SpendPast(rig, agentUsd: 0m, sideUsd: 0m, tokens: 120_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        Assert.Equal(100_000L, rig.State.BudgetGrantTokens);
        Assert.Equal(200_000L, rig.Ctx.EffectiveMaxRunTokens);
        Assert.Equal(120_000, rig.State.PerRunTokens);   // not zeroed either
        var raise = Assert.Single(rig.State.BudgetRaises);
        Assert.Equal(100_000L, raise.FromTokens);
        Assert.Equal(200_000L, raise.ToTokens);
        Assert.Null(raise.FromCostUsd);
        Assert.Contains("token ceiling 100k -> 200k (+100k)",
            ApprovalLine(rig),
            StringComparison.Ordinal);
    }

    /// <summary>Both ceilings reached, one approval: both are raised, so the operator does not approve
    /// twice for one park.</summary>
    [Fact]
    public async Task ApproveRaisesEveryCeilingTheRunHasActuallyReached()
    {
        var rig = Rig(costCap: 3.00m, tokenCap: 100_000);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 120_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        Assert.Equal(6.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(200_000L, rig.Ctx.EffectiveMaxRunTokens);
    }

    /// <summary>A ceiling the run has NOT reached is left alone: approving past a money park must not
    /// quietly double a token ceiling nobody complained about.</summary>
    [Fact]
    public async Task ACeilingTheRunIsNowhereNearIsNotRaisedByApprovingTheOtherOne()
    {
        var rig = Rig(costCap: 3.00m, tokenCap: 100_000);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 900);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        Assert.Equal(6.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(100_000L, rig.Ctx.EffectiveMaxRunTokens);   // untouched
        Assert.Equal(0L, rig.State.BudgetGrantTokens);
    }

    // ────────────────────────────── what approve refuses ──────────────────────────────

    /// <summary>All three approval outcomes share one entry point, and an amount is meaningless for two
    /// of them. It is REFUSED rather than ignored: a number an operator typed and the tool silently
    /// dropped is worse than an error, because the operator goes on believing they raised something.</summary>
    [Theory]
    [InlineData(AwaitingOwnerReason.OwnerGate)]
    [InlineData(AwaitingOwnerReason.ApprovalMode)]
    public async Task AnAmountIsRefusedOnAParkThatHasNoCeiling(AwaitingOwnerReason reason)
    {
        var rig = Rig(costCap: 3.00m);
        rig.State.CurrentStage = "S1";
        rig.State.Status = RunStatus.AwaitingOwner;
        rig.State.AwaitingOwnerReason = reason;

        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=5", CancellationToken.None);

        // Refused means refused: nothing advanced, nothing resumed, nothing was granted.
        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Equal(reason, rig.State.AwaitingOwnerReason);
        Assert.Empty(rig.State.OwnerApprovedStages);
        Assert.Empty(rig.State.ConfirmedStages);
        Assert.False(rig.Ctx.SessionApproved);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        Assert.Contains(Log(rig), l => l.Contains("approve refused", StringComparison.Ordinal));
    }

    /// <summary>Approving WITHOUT an amount is untouched on those parks — the flag is the only new
    /// thing, and the ordinary keypress path must behave exactly as it did.</summary>
    [Fact]
    public async Task ApprovalModeStillRunsTheNextSessionWhenNoAmountIsGiven()
    {
        var rig = Rig(costCap: 3.00m);
        rig.State.CurrentStage = "S1";
        rig.State.Status = RunStatus.AwaitingOwner;
        rig.State.AwaitingOwnerReason = AwaitingOwnerReason.ApprovalMode;

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        Assert.True(rig.Ctx.SessionApproved);
        Assert.Equal(RunStatus.Idle, rig.State.Status);
        Assert.Null(rig.State.AwaitingOwnerReason);
    }

    /// <summary>A typo leaves the run parked and the ceiling where it was. Approving "$" of nothing is
    /// the one failure mode that must not resolve itself into a default.</summary>
    [Fact]
    public async Task AnUnreadableAmountLeavesTheRunParkedAndTheCeilingUnmoved()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 1_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=banana", CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Equal(AwaitingOwnerReason.Budget, rig.State.AwaitingOwnerReason);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        Assert.Equal(0, rig.State.BudgetApprovals);
        Assert.Empty(rig.State.BudgetRaises);
        Assert.Contains(Log(rig), l => l.Contains("approve refused", StringComparison.Ordinal));
    }

    /// <summary>Raising a ceiling the plan never set is refused too, and says which key to set — the
    /// alternative is a grant that silently does nothing because there is no cap to add it to.</summary>
    [Fact]
    public async Task AnAmountIsRefusedWhenThePlanSetsNoCapForThatHalf()
    {
        var rig = Rig(costCap: null, tokenCap: 100_000);
        SpendPast(rig, agentUsd: 0m, sideUsd: 0m, tokens: 120_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=5", CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        Assert.Contains(Log(rig), l => l.Contains("limits.maxRunCostUsd", StringComparison.Ordinal));
    }

    // ────────────────────────────── restart + the wire ──────────────────────────────

    /// <summary>The raise is run state, so it survives the process that granted it — and so does the
    /// spend, which is the half that used to be deleted. A resumed engine restores both through the
    /// same PersistBudget/RestoreBudget path the counters have always used.</summary>
    [Fact]
    public async Task TheRaisedCeilingAndTheSpendBothSurviveAnEngineRestart()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 79_800);
        ParkOnBudget(rig);
        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        var json = JsonSerializer.Serialize(rig.State, PlanConfig.JsonOpts);
        var restored = JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)!;

        var next = Rig(costCap: 3.00m, state: restored);
        next.Ctx.RestoreBudget();

        Assert.Equal(3.50m, next.Ctx.BilledWindowUsd);
        Assert.Equal(79_800, next.Ctx.RunTokens);
        Assert.Equal(6.00m, next.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(1, next.State.BudgetApprovals);
        var raise = Assert.Single(next.State.BudgetRaises);
        Assert.Equal(6.00m, raise.ToCostUsd);
        Assert.Equal(3.50m, raise.SpentUsd);
    }

    /// <summary>SC2.3's block, re-read under the new semantics: costCap/costSpent/costRemaining are one
    /// monotone comparison against the ceiling in force, and windowCostUsd goes on answering the other
    /// question — what has it spent since the owner last said yes. The invariant SC2.3 shipped
    /// (window never exceeds lifetime) still holds, which is what makes the two readable side by side.</summary>
    [Fact]
    public async Task TheStateBudgetBlockStaysConsistentAcrossARaise()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 1_000);
        ParkOnBudget(rig);
        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        // …and it spends $1.20 more under the raised ceiling.
        rig.Ctx.RunCostUsd = 4.70m;
        rig.Ctx.PersistBudget();
        rig.State.History.Add(new SessionRecord
        {
            Number = 1, StartedUtc = DateTime.UtcNow.AddMinutes(-5), EndedUtc = DateTime.UtcNow, CostUsd = 4.70m,
        });

        var dto = ControlPlaneServer.WithBudget(Dto(rig), rig.Plan.Limits, rig.State);

        Assert.Equal(6.00m, dto.CostCap);          // the ceiling in force, not the plan's $3.00
        Assert.Equal(4.70m, dto.CostSpent);        // monotone: the approval un-spent nothing
        Assert.Equal(1.30m, dto.CostRemaining);    // one subtraction, and it is the engine's
        Assert.Equal(1.20m, dto.WindowCostUsd);    // spent since the approval
        Assert.Equal(4.70m, dto.LifetimeCostUsd);
        Assert.Equal(1, dto.BudgetApprovals);
        Assert.True(dto.WindowCostUsd <= dto.LifetimeCostUsd,
            $"window ${dto.WindowCostUsd} must never exceed lifetime ${dto.LifetimeCostUsd}");
        Assert.Equal(rig.State.BudgetRaises[^1].WhenUtc, dto.BudgetWindowStartedUtc);
    }

    /// <summary>Before any approval the block answers exactly what SC2.3 promised, unchanged: window
    /// and lifetime are the same number and the cap is the plan's own.</summary>
    [Fact]
    public void WithNoApprovalTheBlockIsUnchangedFromSC23()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 1.25m, sideUsd: 0m, tokens: 1_000);
        rig.State.History.Add(new SessionRecord
        {
            Number = 1, StartedUtc = DateTime.UtcNow.AddMinutes(-5), EndedUtc = DateTime.UtcNow, CostUsd = 1.25m,
        });

        var dto = ControlPlaneServer.WithBudget(Dto(rig), rig.Plan.Limits, rig.State);

        Assert.Equal(3.00m, dto.CostCap);
        Assert.Equal(1.25m, dto.CostSpent);
        Assert.Equal(1.25m, dto.WindowCostUsd);
        Assert.Equal(dto.LifetimeCostUsd, dto.WindowCostUsd);
        Assert.Equal(0, dto.BudgetApprovals);
        Assert.Null(dto.BudgetWindowStartedUtc);
    }

    /// <summary>The run report is the artifact a takeover reads, and it quotes the ceiling IN FORCE —
    /// annotated, so nobody has to reconcile it against a plan file that says something smaller. The
    /// word "cap" is deliberately unchanged: SC2.4 pins it, and the fix here is the number and the
    /// clause beside it, not a rename.</summary>
    [Fact]
    public async Task TheRunReportQuotesTheCeilingInForceAndSaysItWasRaised()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 1_000);

        var before = RunSummary.SpendLine(rig.Plan, rig.State, agentCost: 3.50m, overhead: 0m);
        Assert.Contains("cap $3.00 (", before, StringComparison.Ordinal);
        Assert.DoesNotContain("raised", before, StringComparison.Ordinal);
        Assert.DoesNotContain("approval", before, StringComparison.Ordinal);

        ParkOnBudget(rig);
        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        var after = RunSummary.SpendLine(rig.Plan, rig.State, agentCost: 3.50m, overhead: 0m);
        Assert.Contains("cap $6.00 (raised from $3.00 by 1 approval(s))", after, StringComparison.Ordinal);
        Assert.Contains("since approval #1", after, StringComparison.Ordinal);
    }

    /// <summary>The other half of clause 5, at the unit the reload path actually calls: a run parked on
    /// its ceiling is un-parked when a reloaded plan puts its spend back inside — and a run parked for
    /// any OTHER reason is left exactly where it is. A reload is not a resume.</summary>
    [Fact]
    public void ARaisedPlanCapUnParksABudgetParkAndNothingElse()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 1_000);
        ParkOnBudget(rig);

        // Still over: the reload changed nothing that matters and the park stands.
        RunLoop.ResumeIfBudgetParkCleared(rig.Ctx);
        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);

        rig.Plan.Limits.MaxRunCostUsd = 10.00m;
        RunLoop.ResumeIfBudgetParkCleared(rig.Ctx);
        Assert.Equal(RunStatus.Idle, rig.State.Status);
        Assert.Null(rig.State.AwaitingOwnerReason);
        // The un-park is a reload, not an approval: no grant, no approval counted, no raise recorded.
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        Assert.Equal(0, rig.State.BudgetApprovals);
        Assert.Empty(rig.State.BudgetRaises);

        // An operator pause under the same generous ceiling is NOT a budget park and stays put.
        rig.State.Status = RunStatus.Paused;
        rig.State.AwaitingOwnerReason = null;
        RunLoop.ResumeIfBudgetParkCleared(rig.Ctx);
        Assert.Equal(RunStatus.Paused, rig.State.Status);

        // Nor is an owner gate, even though it parks in the same status.
        rig.State.Status = RunStatus.AwaitingOwner;
        rig.State.AwaitingOwnerReason = AwaitingOwnerReason.OwnerGate;
        RunLoop.ResumeIfBudgetParkCleared(rig.Ctx);
        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Equal(AwaitingOwnerReason.OwnerGate, rig.State.AwaitingOwnerReason);
    }

    // ────────────────────────────── the amount's route in ──────────────────────────────

    /// <summary>`approve` and `resume` are deliberately the SAME action — the engine decides what to do
    /// from why the run parked. Only one of them may carry an amount, and this is the seam where that
    /// is enforced: the dispatcher hands the value straight to the approval.</summary>
    [Fact]
    public async Task TheDispatcherCarriesTheAmountFromTheControlIngressToTheApproval()
    {
        string? seen = "not called";
        var state = new RunState { RunId = RunId, Status = RunStatus.AwaitingOwner };
        var dispatcher = new ControlDispatcher(
            new PlanConfig { Name = "p", Repo = ".", Tracker = "T.md" }, state, new PlainSink(),
            NullEventSink.Instance, log: _ => { }, save: () => { }, deleteControlFile: () => { },
            skipStage: (_, _) => { }, approveAwaitingOwner: (amount, _) => { seen = amount; return Task.CompletedTask; });

        await dispatcher.DispatchAsync(
            ControlFile.Parse("""{"command":"approve","value":"usd=5"}"""), inSession: false, CancellationToken.None);
        Assert.Equal("usd=5", seen);

        // …and a plain resume of the same park carries nothing, whatever its body happens to contain.
        await dispatcher.DispatchAsync(
            ControlFile.Parse("""{"command":"resume","value":"usd=5"}"""), inSession: false, CancellationToken.None);
        Assert.Null(seen);
    }

    // ────────────────── the ceiling every gate on the way to a session reads ──────────────────

    /// <summary>
    /// The approval has to reach EVERY comparison that can stop a session, not just the cap check.
    /// <para><see cref="PreflightHealth"/> carries a budget arm of its own, and the run loop was still
    /// handing it the PLAN's cap and the agent-only counter. The old reset semantics hid that: zeroing
    /// <c>PerRunCostUsd</c> cleared this comparison as a side effect. Keeping the counter and raising the
    /// ceiling does not — so with a <c>limits.dnsHealthCheck</c> block on the plan (and
    /// <c>Enabled</c> defaults to true), an approved run un-parked, failed this probe, was parked again
    /// on a preflight backoff that doubles up to an hour, and never spawned another session. The
    /// approval was inert, and no test in this file went near it.</para>
    /// <para>Asserted through <see cref="RunLoop.PreflightAsync"/>, which is the run loop's own call —
    /// both of its call sites (the pre-session probe and the parked recheck) go through it.</para>
    /// </summary>
    [Fact]
    public async Task TheApprovedRunPassesThePreSessionProbeThatWouldOtherwiseParkItForever()
    {
        var rig = Rig(costCap: 3.00m);
        // Nothing but the budget arm: no hosts to resolve, no endpoints, no git, no disk floor — so the
        // only thing this probe can have an opinion about is the money.
        rig.Plan.Limits.DnsHealthCheck = new DnsHealthCheckConfig
        {
            Enabled = true, Hosts = [], ApiEndpoints = [], EnableGitCheck = false, MinFreeDiskMb = 0,
        };
        SpendPast(rig, agentUsd: 3.00m, sideUsd: 0.50m, tokens: 79_800);
        ParkOnBudget(rig);

        // Parked: the probe agrees with the park, and names the ceiling in force.
        var parked = await RunLoop.PreflightAsync(rig.Ctx);
        var budget = Assert.Single(parked, r => string.Equals(r.Name, "budget", StringComparison.Ordinal));
        Assert.False(budget.Passed);
        Assert.Contains("$3.50", budget.Message, StringComparison.Ordinal);
        Assert.Contains("$3.00", budget.Message, StringComparison.Ordinal);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        // Approved: $3.50 spent under a $6.00 ceiling. The probe must stand aside, or the run backs off
        // for an hour at a time and the approval buys nothing.
        Assert.Equal(6.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        var after = await RunLoop.PreflightAsync(rig.Ctx);
        Assert.DoesNotContain(after, r => string.Equals(r.Name, "budget", StringComparison.Ordinal));
        Assert.False(PreflightHealth.AnyFailed(after));
    }

    /// <summary>The rule behind the arm above, stated as code: <c>PreflightHealth.RunAllAsync</c> has
    /// exactly one engine caller, so there is nowhere for a second, stale spend-vs-cap comparison to
    /// live. Doctor calls it too and is named here with its reason — it passes no cap at all, because
    /// its budget verdict is <c>DoctorCommand.CheckBudget</c>, which reads the same effective ceiling.</summary>
    [Fact]
    public void ThePreflightProbeHasOneEngineCallerAndItReadsTheCeilingInForce()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PreflightHealth.cs",          // the check itself
            "RunLoop.Budget.cs",           // the one engine seam
            "DoctorCommand.cs",            // offline, and deliberately passes no cap
        };

        var callers = SourceFilesUnderSrc()
            .Where(f => File.ReadAllText(f).Contains("PreflightHealth.RunAllAsync(", StringComparison.Ordinal))
            .Select(f => new FileInfo(f).Name)
            .Where(n => !allowed.Contains(n))
            .ToList();
        Assert.True(callers.Count == 0,
            "a second caller of the preflight probe is a second spend-vs-cap comparison, and it will be " +
            "the one nobody rewires when the ceiling moves: " + string.Join(", ", callers));

        // …and the one seam passes the ceiling in force, not the plan's figure. Read off the call
        // itself, because that argument list is the exact thing this checkpoint got wrong.
        var seam = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor.Core", "Orchestration", "RunLoop.Budget.cs"));
        var at = seam.IndexOf("PreflightHealth.RunAllAsync(", StringComparison.Ordinal);
        Assert.True(at >= 0, "the engine's preflight seam moved out of RunLoop.Budget.cs");
        var call = seam[at..seam.IndexOf(");", at, StringComparison.Ordinal)];
        Assert.Contains("EffectiveMaxRunCostUsd", call, StringComparison.Ordinal);
        Assert.Contains("BilledWindowUsd", call, StringComparison.Ordinal);
    }

    // ────────────── a raise that does not clear the spend is refused, not rounded ──────────────

    /// <summary>
    /// The overshoot can be bigger than the raise. A run that blew past a $3.00 cap to $10.00 — one long
    /// session is enough — gets a default raise of $3.00, which lands the ceiling at $6.00: still under
    /// the spend. Resuming there costs a whole session and parks again immediately, and the only way to
    /// state that ceiling's headroom is as a negative. "$-4.00 left" is not a sentence anybody can act
    /// on, so the approval is refused, the run stays parked, and the refusal names the number to type.
    /// </summary>
    [Fact]
    public async Task ARaiseThatWouldNotClearTheSpendIsRefusedAndTheRunStaysParked()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 10.00m, sideUsd: 0m, tokens: 1_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Equal(AwaitingOwnerReason.Budget, rig.State.AwaitingOwnerReason);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        Assert.Equal(0, rig.State.BudgetApprovals);
        Assert.Empty(rig.State.BudgetRaises);
        Assert.DoesNotContain(Log(rig), l => l.Contains("owner approved (budget)", StringComparison.Ordinal));

        var refusal = Assert.Single(Log(rig), l => l.Contains("approve refused", StringComparison.Ordinal));
        Assert.Contains("$10.00 this run has already spent", refusal, StringComparison.Ordinal);
        Assert.Contains("an amount over $7.00", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("$-", refusal, StringComparison.Ordinal);
    }

    /// <summary>The same rule when the operator names the amount themselves: an <c>--amount</c> under the
    /// overshoot is a raise that parks again, and it is refused with the shortfall rather than accepted
    /// into a ceiling below the spend.</summary>
    [Fact]
    public async Task AnAmountSmallerThanTheOvershootIsRefusedWithTheShortfall()
    {
        var rig = Rig(costCap: 3.00m);
        SpendPast(rig, agentUsd: 10.00m, sideUsd: 0m, tokens: 1_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=2", CancellationToken.None);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);

        // …and the number the refusal named does clear it.
        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=7.50", CancellationToken.None);
        Assert.Equal(7.50m, rig.State.BudgetGrantUsd);
        Assert.Equal(10.50m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(RunStatus.Idle, rig.State.Status);
        Assert.Contains("$0.50 left", ApprovalLine(rig), StringComparison.Ordinal);
    }

    /// <summary>The token half refuses on the same rule, so the two halves of one park cannot end up
    /// with different ideas about what an approval is allowed to leave behind.</summary>
    [Fact]
    public async Task ATokenRaiseThatWouldNotClearWhatIsCountedIsRefusedToo()
    {
        var rig = Rig(costCap: null, tokenCap: 100_000);
        SpendPast(rig, agentUsd: 0m, sideUsd: 0m, tokens: 500_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);

        Assert.Equal(0L, rig.State.BudgetGrantTokens);
        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Contains(Log(rig), l => l.Contains("an amount over 400k", StringComparison.Ordinal));
    }

    /// <summary>
    /// Round 2's blocking finding, pinned: a run over BOTH ceilings, and an `--amount` that names only
    /// the money half. Raising that half and un-parking would resume a run still over its token
    /// ceiling, buy one full session, and park it again — the exact harm the refusal exists to
    /// prevent, and the divergence clause 7 forbids. So NOTHING is granted, the run stays parked, and
    /// the refusal names the half the amount did not touch. The un-park test is the same both-halves
    /// predicate the reload's un-park asks (<see cref="RunLoop.ResumeIfBudgetParkCleared"/>).
    /// </summary>
    [Fact]
    public async Task AnAmountThatLeavesTheOtherHalfOverIsRefusedNotResumedIntoASecondPark()
    {
        var rig = Rig(costCap: 3.00m, tokenCap: 100_000);
        SpendPast(rig, agentUsd: 3.50m, sideUsd: 0m, tokens: 120_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=5", CancellationToken.None);

        // Refused whole: not even the half the amount DID clear was granted — a half-approval would
        // leave the park meaning two different things to its two halves.
        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Equal(AwaitingOwnerReason.Budget, rig.State.AwaitingOwnerReason);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        Assert.Equal(0L, rig.State.BudgetGrantTokens);
        Assert.Equal(0, rig.State.BudgetApprovals);
        Assert.Empty(rig.State.BudgetRaises);

        var refusal = Assert.Single(Log(rig), l => l.Contains("approve refused", StringComparison.Ordinal));
        Assert.Contains("token ceiling stays reached (120k >= 100k)", refusal, StringComparison.Ordinal);
        Assert.Contains("tokens=", refusal, StringComparison.Ordinal);

        // …and the two commands the refusal offers both clear it. Here: name both halves at once.
        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=5;tokens=50000", CancellationToken.None);
        Assert.Equal(RunStatus.Idle, rig.State.Status);
        Assert.Null(rig.State.AwaitingOwnerReason);
        Assert.Equal(8.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(150_000L, rig.Ctx.EffectiveMaxRunTokens);
        var raise = Assert.Single(rig.State.BudgetRaises);
        Assert.Equal(8.00m, raise.ToCostUsd);
        Assert.Equal(150_000L, raise.ToTokens);
    }

    /// <summary>The same shape from the other side: parked on tokens ALONE, approved with dollars. The
    /// money half is not even blocking, so the raise buys nothing and the run would resume straight
    /// into the ceiling that parked it. Refused, naming the half that actually needs the number —
    /// then the no-amount default clears exactly that half and touches no other.</summary>
    [Fact]
    public async Task ADollarAmountOnATokenParkIsRefusedAndTheDefaultThenRaisesTheRightHalf()
    {
        var rig = Rig(costCap: 3.00m, tokenCap: 100_000);
        SpendPast(rig, agentUsd: 1.00m, sideUsd: 0m, tokens: 120_000);
        ParkOnBudget(rig);

        await rig.Verdicts.ApproveAwaitingOwnerAsync("usd=5", CancellationToken.None);

        Assert.Equal(RunStatus.AwaitingOwner, rig.State.Status);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);
        var refusal = Assert.Single(Log(rig), l => l.Contains("approve refused", StringComparison.Ordinal));
        Assert.Contains("token ceiling stays reached", refusal, StringComparison.Ordinal);

        await rig.Verdicts.ApproveAwaitingOwnerAsync(null, CancellationToken.None);
        Assert.Equal(RunStatus.Idle, rig.State.Status);
        Assert.Equal(0m, rig.State.BudgetGrantUsd);            // the money half was never blocking
        Assert.Equal(100_000L, rig.State.BudgetGrantTokens);   // the token half got its own cap's worth
        Assert.Equal(3.00m, rig.Ctx.EffectiveMaxRunCostUsd);
        Assert.Equal(200_000L, rig.Ctx.EffectiveMaxRunTokens);
    }

    // ────────────────────────────── one predicate, three doors ──────────────────────────────

    /// <summary>The comparison itself, at its one home: inclusive (spending exactly the ceiling IS
    /// reaching it — the loop's rule), and a half with no cap can never be over.</summary>
    [Fact]
    public void TheStandingPredicateIsInclusiveAndACaplessHalfIsNeverOver()
    {
        Assert.True(BudgetCeiling.Standing(3.00m, 3.00m, null, 0L).OverCost);
        Assert.False(BudgetCeiling.Standing(3.00m, 2.99m, null, 0L).OverCost);
        Assert.True(BudgetCeiling.Standing(null, 0m, 100_000L, 100_000L).OverTokens);
        Assert.False(BudgetCeiling.Standing(null, 999m, null, 999_999L).AnyOver);
        Assert.True(BudgetCeiling.Standing(3.00m, 3.50m, 100_000L, 120_000L) is { OverCost: true, OverTokens: true });
    }

    /// <summary>The park line names EVERY half the run is over — round 2 caught the single-half line
    /// sending an operator to approve a dollar amount against a park that was also about tokens.</summary>
    [Fact]
    public void TheParkAnnouncementNamesEveryHalfTheRunIsOver()
    {
        Assert.Equal("budget cap: $3.50 >= $3.00", BudgetCeiling.Overage(3.00m, 3.50m, null, 0L));
        Assert.Equal("token cap: 120k >= 100k", BudgetCeiling.Overage(null, 0m, 100_000L, 120_000L));
        Assert.Equal("budget cap: $3.50 >= $3.00; token cap: 120k >= 100k",
            BudgetCeiling.Overage(3.00m, 3.50m, 100_000L, 120_000L));
    }

    /// <summary>The routing, stated as code: the cap check, the reload's un-park and the approval all
    /// read <c>BudgetStanding</c>, and none of them keeps a private copy of the spend-vs-cap
    /// comparison. A copy agrees on the day it is written and diverges on the next edit — round 2's
    /// blocking finding was exactly such a divergence, three months early.</summary>
    [Fact]
    public void TheSpendVsCapComparisonHasOneHomeAndEveryDoorReadsIt()
    {
        var doors = new[]
        {
            Path.Combine("src", "Conductor.Core", "Orchestration", "RunLoop.Budget.cs"),
            Path.Combine("src", "Conductor.Core", "Orchestration", "VerdictEngine.Approval.cs"),
        };
        foreach (var door in doors)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), door));
            Assert.Contains("BudgetStanding", text, StringComparison.Ordinal);
            foreach (var counter in new[] { "BilledWindowUsd >=", "RunTokens >=", "spentUsd >=", "spentTokens >=" })
                Assert.DoesNotContain(counter, text, StringComparison.Ordinal);
        }
        // The pre-session probe's budget arm reads the same predicate rather than keeping a fourth copy.
        var probe = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor.Core", "PreflightHealth.cs"));
        Assert.Contains("BudgetCeiling.Standing(", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("currentCostUsd >=", probe, StringComparison.Ordinal);
    }

    // ────────────────────────────── doctor reads the same ceiling ──────────────────────────────

    /// <summary>Doctor is an operator surface like any other, and it was still comparing the run's spend
    /// to <c>limits.maxRunCostUsd</c>: with a grant on file it reported "fail — the run will park at
    /// AwaitingOwner" about a run that had been approved past exactly that park and would not park at
    /// all. It reads <see cref="BudgetCeiling.EffectiveCostCap"/> now, like every other surface, and says
    /// where the bigger number came from.</summary>
    [Fact]
    public void DoctorReportsTheCeilingInForceRatherThanThePlansFigure()
    {
        var plan = new PlanConfig { Name = "p", Repo = "." };
        plan.Limits.MaxRunCostUsd = 3.00m;

        var stale = Conductor.Commands.DoctorCommand.CheckBudget(plan, currentCostUsd: 3.50m, hasRun: true, budgetGrantUsd: 0m, budgetGrantTokens: 0L);
        Assert.Equal("fail", stale.State);

        var granted = Conductor.Commands.DoctorCommand.CheckBudget(plan, currentCostUsd: 3.50m, hasRun: true, budgetGrantUsd: 3.00m, budgetGrantTokens: 0L);
        Assert.Equal("ok", granted.State);
        Assert.Contains("$6.00", granted.Message, StringComparison.Ordinal);
        Assert.Contains("raised from $3.00", granted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("will park", granted.Message, StringComparison.Ordinal);

        // A grant cannot invent a ceiling on a plan that set none — the same rule as everywhere else.
        var uncapped = new PlanConfig { Name = "p", Repo = "." };
        Assert.Equal("warn", Conductor.Commands.DoctorCommand.CheckBudget(uncapped, 99m, hasRun: true, budgetGrantUsd: 25m, budgetGrantTokens: 0L).State);

        // The token half of the same rule (round 2): the no-cost-cap branch quotes the token ceiling,
        // and it must be the ceiling in force, annotated — not the plan's raw figure.
        var tokenOnly = new PlanConfig { Name = "p", Repo = "." };
        tokenOnly.Limits.MaxRunTokens = 100_000;
        var tokenGranted = Conductor.Commands.DoctorCommand.CheckBudget(
            tokenOnly, currentCostUsd: 0m, hasRun: true, budgetGrantUsd: 0m, budgetGrantTokens: 100_000L);
        Assert.Equal("ok", tokenGranted.State);
        Assert.Contains("token cap 200k", tokenGranted.Message, StringComparison.Ordinal);
        Assert.Contains("raised from 100k by owner approval", tokenGranted.Message, StringComparison.Ordinal);
    }

    // ────────────────────────────── the rig ──────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFilesUnderSrc() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));


    private sealed record ApprovalRig(
        string Repo, PlanConfig Plan, RunState State, RunContext Ctx, VerdictEngine Verdicts, RecordingSink Sink);

    private sealed class RecordingSink : IProgressSink
    {
        public readonly List<ToastMessage> Toasts = [];
        public void Log(string line) { }
        public void AgentEvent(AgentEvent ev) { }
        public void Snapshot(DashboardSnapshot snap) { }
        public ControlCommand? PollControl() => null;
        public void Toast(ToastMessage toast) => Toasts.Add(toast);
    }

    private static IReadOnlyList<string> Log(ApprovalRig rig)
    {
        var path = Path.Combine(rig.Repo, StateHome.ScratchDirName, "conductor.log");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    /// <summary>The one line the approval writes — asserted as a single line on purpose: an approval
    /// that announced itself twice would read as two raises.</summary>
    private static string ApprovalLine(ApprovalRig rig)
        => Assert.Single(Log(rig), l => l.Contains("owner approved (budget)", StringComparison.Ordinal));

    private static StateDto Dto(ApprovalRig rig)
        => ControlPlaneMapper.FromSnapshot(
            SnapshotBuilder.Build(rig.Plan, rig.State, new TrackerSnapshot()), rig.State.RunId, rig.Plan.Repo, rig.Plan.PlanDir);

    /// <summary>Spend, as the run loop accrues it: the agent's own bill, the lanes'/advisor's beside it
    /// (KS5.2), and the tokens. Persisted the way the loop persists it, so the assertions above are
    /// about the same fields a real park would have been comparing.</summary>
    private static void SpendPast(ApprovalRig rig, decimal agentUsd, decimal sideUsd, long tokens)
    {
        rig.Ctx.RunCostUsd = agentUsd;
        rig.Ctx.RunSideCostUsd = sideUsd;
        rig.Ctx.RunTokens = tokens;
        rig.State.TotalSideCostUsd = sideUsd;
        rig.Ctx.PersistBudget();
    }

    private static void ParkOnBudget(ApprovalRig rig)
    {
        rig.State.CurrentStage = "S1";
        rig.State.Status = RunStatus.AwaitingOwner;
        rig.State.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
        rig.State.SetAttention("budget cap reached");
    }

    /// <summary>A verdict engine wired for the approval path and nothing else, on KS5.3's rig shape.
    /// No store: the approval writes no rows, and a null one makes that an assertion rather than a
    /// claim. The repo-local state pointer is written first so nothing here can grow the machine's
    /// catalogue (KS0.1's rule for tests that only meant to read).</summary>
    private ApprovalRig Rig(decimal? costCap, long? tokenCap = null, RunState? state = null)
    {
        var repo = Path.Combine(_tmp, "repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(repo, StateHome.ScratchDirName));
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n" +
            "|---|---|---|---|---|\n| S1.1 | one | TODO | | |\n", new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(repo, StateHome.ScratchDirName, StateHome.PointerFileName),
            $$"""{"runDb": {{JsonSerializer.Serialize(Path.Combine(repo, "run.db"))}}}""", new UTF8Encoding(false));

        var plan = new PlanConfig
        {
            Name = "ks54",
            Repo = repo.Replace('\\', '/'),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "S1", Title = "one", Sessions = 1 }],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "echo", "{prompt}"], Provider = "opencode" },
        };
        plan.Limits.MaxRunCostUsd = costCap;
        plan.Limits.MaxRunTokens = tokenCap;

        var runState = state ?? new RunState { RunId = RunId, PlanName = plan.Name };
        var sink = new RecordingSink();
        var lessons = new LessonsManager(plan.StateDir);
        var qa = new DefaultQaPolicy();
        var webhooks = new WebhookNotifier(plan, NullLogger<WebhookNotifier>.Instance);
        _open.Add(webhooks);

        var ctx = new RunContext(
            plan, runState, new RunOptions(DryRun: true, Once: true, MaxSessions: 0),
            sink, NullEventSink.Instance, new PromptBuilder(plan, new PersonaRegistry(plan), lessons, qa),
            lessons, new CheckpointPlanner(), ProgressProviderFactory.Create(plan),
            AgentProviderFactory.Create(plan.Agent), store: null,
            processSupervisor: null, controlInbox: null,
            new NoOpTelegramService(), webhooks,
            workflowResolver: null, NullLogger<KS5_4ApproveRaisesTheCeilingTests>.Instance);

        var verdicts = new VerdictEngine(ctx,
            new GateOrchestrator(plan, runState, NullEventSink.Instance, store: null),
            new LaneCoordinator(plan, runState, sink, NullEventSink.Instance, _ => { }),
            new NoOpTelegramService(), webhooks, saveAndReport: () => { }, pushIdleSnapshot: () => { });

        return new ApprovalRig(repo, plan, runState, ctx, verdicts, sink);
    }
}
