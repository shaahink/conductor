using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Tests;

public sealed class C3EventsAndRecoveryTests
{
    // ---------------------------------------------------------------- Rollback event

    [Fact]
    public void RollbackExecuted_RoundTripsThroughJson()
    {
        var evt = new RollbackExecuted
        {
            RunId = "r1",
            SessionId = null,
            Seq = 42,
            Ts = DateTimeOffset.UtcNow,
            StageId = "C3",
            FromSha = "abc123",
            ToSha = "def456",
            Forced = true,
        };

        var json = JsonSerializer.Serialize(evt, EventJsonContext.Default.ConductorEvent);
        var back = JsonSerializer.Deserialize(json, EventJsonContext.Default.ConductorEvent);

        Assert.NotNull(back);
        Assert.IsType<RollbackExecuted>(back);
        var cast = (RollbackExecuted)back!;
        Assert.Equal("C3", cast.StageId);
        Assert.Equal("abc123", cast.FromSha);
        Assert.Equal("def456", cast.ToSha);
        Assert.True(cast.Forced);
    }

    [Fact]
    public void RollbackExecuted_NonForcedRoundTrip()
    {
        var evt = new RollbackExecuted
        {
            RunId = "r1",
            Seq = 1,
            Ts = DateTimeOffset.UtcNow,
            StageId = "B3",
            FromSha = "111",
            ToSha = "222",
            Forced = false,
        };

        var json = JsonSerializer.Serialize<ConductorEvent>(evt, EventJsonContext.Default.ConductorEvent);
        Assert.Contains("\"type\":\"rollbackExecuted\"", json, StringComparison.Ordinal);
        Assert.Contains("\"forced\":false", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize(json, EventJsonContext.Default.ConductorEvent);
        Assert.IsType<RollbackExecuted>(back);
        Assert.False(((RollbackExecuted)back!).Forced);
    }

    // ---------------------------------------------------------------- Budget persistence

    [Fact]
    public void RunState_PerRunBudgetSurvivesSaveLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-budget-{Guid.NewGuid():N}.json");
        try
        {
            var s = new RunState
            {
                PlanName = "Test",
                PerRunCostUsd = 3.50m,
                PerRunTokens = 1_200_000,
                Status = RunStatus.Idle,
            };
            s.Save(path);

            var loaded = RunState.LoadOrNew(path, s.PlanName);
            Assert.Equal(3.50m, loaded.PerRunCostUsd);
            Assert.Equal(1_200_000, loaded.PerRunTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RunState_BudgetZeroByDefault()
    {
        var s = new RunState { PlanName = "New" };
        Assert.Equal(0m, s.PerRunCostUsd);
        Assert.Equal(0L, s.PerRunTokens);
    }

    [Fact]
    public void RunState_BudgetDoesNotAffectTotalSummary()
    {
        var s = new RunState
        {
            PlanName = "Test",
            PerRunCostUsd = 5.0m,
            PerRunTokens = 500_000,
            History =
            {
                new SessionRecord { Number = 1, Stage = "S1", Kind = SessionKind.Deliver, CostUsd = 2.0m },
            },
        };
        Assert.Equal(2.0m, s.TotalCostUsd); // only history, not per-run
        Assert.Equal(5.0m, s.PerRunCostUsd); // separate field
    }

    // ---------------------------------------------------------------- Orphaned resume

    [Fact]
    public void FindInterruptedSession_ReturnsNullWhenAllMatched()
    {
        var events = new List<ConductorEvent>
        {
            new SessionStarted { Seq = 1, Number = 1, StageId = "S1", Kind = "Deliver", AgentSessionId = "sid-1" },
            new SessionFinished { Seq = 2, Number = 1, StageId = "S1", Outcome = "Advanced" },
        };

        var result = RunStateProjection.FindInterruptedSession(events);
        Assert.Null(result);
    }

    [Fact]
    public void FindInterruptedSession_DetectsUnmatchedStart()
    {
        var events = new List<ConductorEvent>
        {
            new SessionStarted { Seq = 1, Number = 7, StageId = "C3", Kind = "Deliver", AgentSessionId = "sid-7" },
        };

        var result = RunStateProjection.FindInterruptedSession(events);
        Assert.NotNull(result);
        Assert.Equal(7, result.Number);
        Assert.Equal("C3", result.StageId);
        Assert.Equal("sid-7", result.AgentSessionId);
    }

    [Fact]
    public void FindInterruptedSession_EmptyAgentSessionId_IsEmptyString()
    {
        var events = new List<ConductorEvent>
        {
            new SessionStarted { Seq = 1, Number = 1, StageId = "S1", Kind = "Deliver", AgentSessionId = null },
        };

        var result = RunStateProjection.FindInterruptedSession(events);
        Assert.NotNull(result);
        Assert.Equal("", result.AgentSessionId); // null gets defaulted to ""
    }

    [Fact]
    public void FindInterruptedSession_ReturnsHighestUnmatched()
    {
        var events = new List<ConductorEvent>
        {
            new SessionStarted { Seq = 1, Number = 1, StageId = "S1", Kind = "Deliver", AgentSessionId = "s-1" },
            new SessionFinished { Seq = 2, Number = 1, StageId = "S1", Outcome = "Advanced" },
            new SessionStarted { Seq = 3, Number = 2, StageId = "S1", Kind = "Deliver", AgentSessionId = "s-2" },
            new SessionFinished { Seq = 4, Number = 2, StageId = "S1", Outcome = "Advanced" },
            new SessionStarted { Seq = 5, Number = 3, StageId = "S2", Kind = "Fix", AgentSessionId = "s-3" },
            // No SessionFinished for #3 → interrupted
        };

        var result = RunStateProjection.FindInterruptedSession(events);
        Assert.NotNull(result);
        Assert.Equal(3, result.Number);
        Assert.Equal("S2", result.StageId);
        Assert.Equal("s-3", result.AgentSessionId);
    }

    // ---------------------------------------------------------------- LiveMetrics folding

    [Fact]
    public void LiveMetrics_ForSession_FoldsCorrectSessionOnly()
    {
        var events = new List<ConductorEvent>
        {
            new TokenDelta { Seq = 1, SessionId = "1", Input = 100, Output = 50, CostUsd = 0.01m },
            new TokenDelta { Seq = 2, SessionId = "2", Input = 200, Output = 100, CostUsd = 0.02m },
            new TokenDelta { Seq = 3, SessionId = "1", Input = 50, Output = 25, CostUsd = 0.005m },
        };

        var s1 = LiveMetrics.ForSession(events, 1);
        Assert.Equal(150, s1.Input);
        Assert.Equal(75, s1.Output);
        Assert.Equal(0.015m, s1.CostUsd);

        var s2 = LiveMetrics.ForSession(events, 2);
        Assert.Equal(200, s2.Input);
        Assert.Equal(100, s2.Output);
        Assert.Equal(0.02m, s2.CostUsd);
    }

    [Fact]
    public void LiveMetrics_RunWide_FoldsAllSessions()
    {
        var events = new List<ConductorEvent>
        {
            new TokenDelta { Seq = 1, SessionId = "1", Input = 100, Output = 50, CostUsd = 0.01m },
            new TokenDelta { Seq = 2, SessionId = "2", Input = 200, Output = 100, CostUsd = 0.02m },
        };

        var all = LiveMetrics.RunWide(events);
        Assert.Equal(300, all.Input);
        Assert.Equal(150, all.Output);
        Assert.Equal(0.03m, all.CostUsd);
    }

    [Fact]
    public void LiveMetrics_SessionTokenTotals_TotalProp()
    {
        var totals = new LiveMetrics.SessionTokenTotals(100, 50, 25, 500, 0.02m);
        Assert.Equal(175, totals.Total);
    }

    // ---------------------------------------------------------------- Mid-session control: rejection doesn't crash

    [Fact]
    public void ControlFile_Parse_UnknownVerbReturnsNullAction()
    {
        var json = """{"command":"nonexistent"}""";
        var result = ControlFile.Parse(json);
        Assert.Null(result.Action);
    }
}
