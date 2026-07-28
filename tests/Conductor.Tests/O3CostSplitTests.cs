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
