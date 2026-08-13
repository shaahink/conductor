using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class O3CostSplitTests
{
    // ---- GateResult.EstimatedCostUsd ----

    [Fact]
    public void GateResult_EstimatedCostUsd_ReturnsDurationTimesRate()
    {
        var g = new GateResult("build", true, false, false, 0, TimeSpan.FromSeconds(30), "");
        var cost = g.EstimatedCostUsd(0.0001m);
        Assert.Equal(0.003m, cost, 6); // 30 * 0.0001
    }

    [Fact]
    public void GateResult_EstimatedCostUsd_SkippedGateReturnsZero()
    {
        var g = new GateResult("lint", false, true, true, -1, TimeSpan.FromSeconds(60), "skipped");
        var cost = g.EstimatedCostUsd(0.0001m);
        Assert.Equal(0m, cost);
    }

    [Fact]
    public void GateResult_EstimatedCostUsd_ZeroDurationReturnsZero()
    {
        var g = new GateResult("fast", true, false, false, 0, TimeSpan.Zero, "");
        var cost = g.EstimatedCostUsd(0.0001m);
        Assert.Equal(0m, cost);
    }

    // ---- RunState overhead totals ----

    [Fact]
    public void RunState_TotalOverheadCostUsd_SumsHistory()
    {
        var state = new RunState();
        state.History.Add(new SessionRecord { OverheadCostUsd = 0.001m });
        state.History.Add(new SessionRecord { OverheadCostUsd = 0.002m });
        state.History.Add(new SessionRecord { OverheadCostUsd = null });
        Assert.Equal(0.003m, state.TotalOverheadCostUsd);
    }

    [Fact]
    public void RunState_TotalOverheadCostUsd_EmptyHistoryReturnsZero()
    {
        var state = new RunState();
        Assert.Equal(0m, state.TotalOverheadCostUsd);
    }

    // ---- PlanConfig defaults ----

    [Fact]
    public void LimitsConfig_OverheadCostPerSecond_DefaultsTo0001()
    {
        var limits = new LimitsConfig();
        Assert.Equal(0.0001m, limits.OverheadCostPerSecond);
    }

    // ---- SessionRecord serialisation ----

    [Fact]
    public void SessionRecord_OverheadCostUsd_SerializesRoundTrip()
    {
        var rec = new SessionRecord
        {
            Number = 10,
            Stage = "O3",
            OverheadCostUsd = 0.003m,
            CostUsd = 0.15m,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(rec, PlanConfig.JsonOpts);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<SessionRecord>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(0.003m, loaded.OverheadCostUsd);
        Assert.Equal(0.15m, loaded.CostUsd);
    }

    // ---- RunState serialisation ----

    [Fact]
    public void RunState_PerRunOverheadCostUsd_SerializesRoundTrip()
    {
        var state = new RunState
        {
            PlanName = "test",
            PerRunOverheadCostUsd = 0.005m,
            PerRunCostUsd = 1.50m,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state, PlanConfig.JsonOpts);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(0.005m, loaded.PerRunOverheadCostUsd);
        Assert.Equal(1.50m, loaded.PerRunCostUsd);
    }

    /// <summary>KS5.2: the side ledger survives a restart the way the agent's window does. A run killed
    /// mid-lane must not forget what the lane cost — that is the same crash-survival contract
    /// <c>PerRunCostUsd</c> has carried since C3, and the cap now compares the sum of the two.</summary>
    [Fact]
    public void RunState_PerRunSideCostUsd_SerializesRoundTrip()
    {
        var state = new RunState
        {
            PlanName = "test",
            PerRunCostUsd = 1.50m,
            PerRunSideCostUsd = 0.25m,
            TotalSideCostUsd = 0.75m,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(state, PlanConfig.JsonOpts);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)!;

        Assert.Equal(0.25m, loaded.PerRunSideCostUsd);
        Assert.Equal(0.75m, loaded.TotalSideCostUsd);
        Assert.Equal(1.75m, loaded.BilledWindowCostUsd);
    }

    /// <summary>KS5.2: the cap total is billed money only. Gate overhead is an ESTIMATE from a
    /// plan-set rate and stays out of it; the advisor's and the lanes' billed rows are in it. Before
    /// this, the advisor's spend went into the overhead bucket, which the cap has never read — so the
    /// only non-agent spend the engine recorded was also the only spend it could not park on.</summary>
    [Fact]
    public void TheCapTotalCountsBilledRowsAndNotTheGateEstimate()
    {
        var state = new RunState
        {
            PlanName = "test",
            PerRunCostUsd = 2.00m,
            PerRunSideCostUsd = 0.50m,
            PerRunOverheadCostUsd = 9.99m,
        };

        Assert.Equal(2.50m, state.BilledWindowCostUsd);
    }

    // ---- SnapshotBuilder ----

    [Fact]
    public void SnapshotBuilder_PopulatesOverheadCost()
    {
        var plan = new PlanConfig { Name = "test", Repo = ".", Stages = new() };
        var state = new RunState();
        state.History.Add(new SessionRecord { OverheadCostUsd = 0.001m });
        state.History.Add(new SessionRecord { OverheadCostUsd = 0.002m });
        var track = new TrackerSnapshot();

        var snap = SnapshotBuilder.Build(plan, state, track);

        Assert.Equal(0.003m, snap.OverheadCostUsd);
        Assert.Equal(0m, snap.SessionOverheadCostUsd); // no live session
    }
}
