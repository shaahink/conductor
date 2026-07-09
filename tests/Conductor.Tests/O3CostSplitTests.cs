using Conductor.Core;
using Conductor.Models;
using Conductor.Ui;

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

    // ---- DashboardRenderer.CostLine ----

    [Fact]
    public void CostLine_ShowsAgentOverheadSplit()
    {
        var line = DashboardRenderer.CostLine(new DashboardSnapshot
        {
            TotalCostUsd = 0.10m,
            OverheadCostUsd = 0.02m,
        });
        Assert.Contains("$0.1200", line);
        Assert.Contains("agent $0.1000", line);
        Assert.Contains("gates $0.0200", line);
    }

    [Fact]
    public void CostLine_HidesOverheadWhenZero()
    {
        var line = DashboardRenderer.CostLine(new DashboardSnapshot
        {
            TotalCostUsd = 0.05m,
            OverheadCostUsd = 0m,
        });
        Assert.Contains("agent $0.0500", line);
        Assert.DoesNotContain("gates", line);
    }

    [Fact]
    public void CostLine_IncludesLiveSessionCost()
    {
        var line = DashboardRenderer.CostLine(new DashboardSnapshot
        {
            TotalCostUsd = 0.10m,
            SessionCostUsd = 0.03m,
            OverheadCostUsd = 0.01m,
            SessionOverheadCostUsd = 0.001m,
        });
        Assert.Contains("$0.1410", line);         // 0.10 + 0.03 + 0.01 + 0.001
        Assert.Contains("agent $0.1300", line);    // 0.10 + 0.03
        Assert.Contains("gates $0.0110", line);    // 0.01 + 0.001
        Assert.Contains("session $0.0300", line);
    }

    [Fact]
    public void CostLine_OmitsSessionWhenZero()
    {
        var line = DashboardRenderer.CostLine(new DashboardSnapshot
        {
            TotalCostUsd = 0.10m,
            SessionCostUsd = 0m,
            OverheadCostUsd = 0.02m,
        });
        Assert.Contains("agent $0.1000", line);
        Assert.Contains("gates $0.0200", line);
        Assert.DoesNotContain("(session", line);
    }

    [Fact]
    public void CostLine_UntrackedSessionsFlagged()
    {
        var line = DashboardRenderer.CostLine(new DashboardSnapshot
        {
            TotalCostUsd = 0.05m,
            OverheadCostUsd = 0.01m,
            UntrackedSessions = 2,
        });
        Assert.Contains("2 sessions unreported", line);
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
