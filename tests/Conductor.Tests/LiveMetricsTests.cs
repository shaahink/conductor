using Conductor.Core.Events;

namespace Conductor.Tests;

/// <summary>B2.6 — proves the <see cref="LiveMetrics"/> projection correctly folds
/// <see cref="TokenDelta"/> events into per-session and run-wide token/cost totals.</summary>
public class LiveMetricsTests
{
    private static TokenDelta Delta(string sid, long inp = 0, long outp = 0, long r = 0, long c = 0, decimal cost = 0)
        => new() { RunId = "r", SessionId = sid, Input = inp, Output = outp, Reasoning = r, CacheRead = c, CostUsd = cost, Seq = 1, Ts = DateTimeOffset.UtcNow };

    [Fact]
    public void ForSessionSumsOnlyDeltasWithMatchingSessionId()
    {
        ConductorEvent[] events =
        [
            Delta("1", inp: 100, outp: 50),
            Delta("2", inp: 200, outp: 75, r: 30),    // different session
            Delta("1", inp: 60, outp: 30, r: 10, c: 500, cost: 0.01m),
            // non-TokenDelta events should be ignored
            new RunStarted { RunId = "r", Plan = "Bat", Repo = "r", Seq = 4, Ts = DateTimeOffset.UtcNow },
        ];

        var t = LiveMetrics.ForSession(events, sessionNumber: 1);
        Assert.Equal(160, t.Input);       // 100 + 60
        Assert.Equal(80, t.Output);       // 50 + 30
        Assert.Equal(10, t.Reasoning);
        Assert.Equal(500, t.CacheRead);
        Assert.Equal(0.01m, t.CostUsd);
    }

    [Fact]
    public void RunWideSumsAllTokenDeltas()
    {
        ConductorEvent[] events =
        [
            Delta("1", inp: 100, outp: 50, cost: 0.01m),
            Delta("2", inp: 200, outp: 75, cost: 0.02m),
            Delta("2", inp: 40, outp: 10),
        ];

        var t = LiveMetrics.RunWide(events);
        Assert.Equal(340, t.Input);       // 100 + 200 + 40
        Assert.Equal(135, t.Output);
        Assert.Equal(0.03m, t.CostUsd);
    }

    [Fact]
    public void EmptyStreamReturnsZeroes()
    {
        var t = LiveMetrics.ForSession(Array.Empty<ConductorEvent>(), 1);
        Assert.Equal(0, t.Input);
        Assert.Equal(0m, t.CostUsd);
    }

    [Fact]
    public void NonExistentSessionReturnsZeroes()
    {
        ConductorEvent[] events = [Delta("1", inp: 100)];
        var t = LiveMetrics.ForSession(events, 99);
        Assert.Equal(0, t.Input);
    }
}
