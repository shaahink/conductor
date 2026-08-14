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

    // ────────────────────────────── the rig ──────────────────────────────

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
