using Conductor.Http;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// K4.4 — live token headroom on the wire.
///
/// <para>The defect these lock down is not a missing feature, it is a WRONG one that any surface
/// would have built from the fields already on the wire. The soft-break rail compares
/// <c>SessionRunner.LiveTokens</c> — input + output + reasoning + <b>cache-read</b> — against the
/// session ceiling. The wire's <c>sessionTokensInput/Output/Reasoning</c> triple excludes cache-read,
/// and on this project cache reads are 98% of every token ever spent. A Face summing the three
/// visible fields would have drawn a gauge sitting near zero for a session the engine was seconds
/// from killing.</para>
///
/// <para>The second rule under test is the honesty one, inherited from SC2.3's
/// <c>CostRemaining</c>: no cap means null, never a comfortable 100% of nothing.</para>
/// </summary>
public class K4_4TokenHeadroomTests
{
    private static PlanConfig Plan(long? maxSessionTokens = null, double? softBreakRatio = null) => new()
    {
        Name = "K44",
        Repo = ".",
        Stages = [new StageConfig { Id = "T0", Title = "t" }],
        Limits = new LimitsConfig { MaxSessionTokens = maxSessionTokens, SoftBreakRatio = softBreakRatio },
    };

    private static TrackerSnapshot Track() =>
        new() { Checkpoints = [new CheckpointRow("T0.1", "cp", "TODO", "-", "-")] };

    /// <summary>A run whose session #1 is still going, started <paramref name="elapsedSec"/> ago.</summary>
    private static RunState Running(double elapsedSec = 120) => new()
    {
        RunId = "r",
        SessionCounter = 1,
        History =
        [
            new SessionRecord { Number = 1, StartedUtc = DateTime.UtcNow.AddSeconds(-elapsedSec), EndedUtc = null },
        ],
    };

    private static ConductorEvent[] Spent(long input, long output, long reasoning, long cacheRead) =>
    [
        new TokenDelta
        {
            SessionId = "1", Input = input, Output = output, Reasoning = reasoning, CacheRead = cacheRead,
            Seq = 1, Ts = DateTimeOffset.UtcNow,
        },
    ];

    /// <summary>The real chain <c>GET /state</c> runs: the mapper, then live metrics (which decides
    /// AgentActive and the elapsed clock), then the headroom block.</summary>
    private static TokenHeadroomDto Headroom(PlanConfig plan, RunState state, ConductorEvent[] events,
        long? thisRunOverride = null)
    {
        var dto = ControlPlaneMapper.FromSnapshot(
            SnapshotBuilder.Build(plan, state, Track()), state.RunId, plan.Repo, plan.PlanDir,
            thisRunOverride);
        dto = ControlPlaneServer.WithLiveSessionMetrics(dto, events, state);
        dto = ControlPlaneServer.WithTokenHeadroom(dto, plan.Limits, state, events);
        Assert.NotNull(dto.TokenHeadroom);
        return dto.TokenHeadroom!;
    }

    [Fact]
    public void HeadroomCountsCacheReadBecauseTheRailDoes()
    {
        // 200k of visible tokens and 9.8M of cache reads: the ratio this project actually runs at.
        var events = Spent(input: 120_000, output: 60_000, reasoning: 20_000, cacheRead: 9_800_000);

        var dto = ControlPlaneMapper.FromSnapshot(
            SnapshotBuilder.Build(Plan(12_000_000), Running(), Track()), "r", ".", "");
        dto = ControlPlaneServer.WithLiveSessionMetrics(dto, events, Running());
        dto = ControlPlaneServer.WithTokenHeadroom(dto, Plan(12_000_000).Limits, Running(), events);

        // What a surface would have derived from the fields it could already see...
        Assert.Equal(200_000, dto.SessionTokensInput + dto.SessionTokensOutput + dto.SessionTokensReasoning);
        // ...versus what the rail is actually about to act on.
        Assert.Equal(10_000_000, dto.TokenHeadroom!.Tokens);
        Assert.Equal(12_000_000, dto.TokenHeadroom.Cap);
        Assert.Equal(2_000_000, dto.TokenHeadroom.ToCap);
        // Past the 9.6M nudge already — 49x further along than the visible fields suggest.
        Assert.Equal(9_600_000, dto.TokenHeadroom.NudgeAt);
        Assert.Equal(-400_000, dto.TokenHeadroom.ToNudge);
    }

    [Fact]
    public void NoCapMeansEveryCapDependentFieldIsNullNeverAFullGauge()
    {
        var h = Headroom(Plan(maxSessionTokens: null), Running(), Spent(10_000, 5_000, 0, 985_000));

        Assert.Equal(1_000_000, h.Tokens); // the spend is still a fact and is still reported
        Assert.Null(h.Cap);
        Assert.Null(h.NudgeAt);
        Assert.Null(h.ToNudge);
        Assert.Null(h.ToCap);
        Assert.Null(h.UsedRatio);      // NOT 0 — a bar would render that as 100% headroom
        Assert.Null(h.MinutesToNudge);
        Assert.Null(h.MinutesToCap);
        // The rate needs no cap to be true, so it survives.
        Assert.NotNull(h.BurnPerMinute);
    }

    [Fact]
    public void TheNudgeUsesTheRailsOwnRatioFallbackNotAFourthCopyOfIt()
    {
        // The plan sets a ceiling and no ratio. The rail nudges at SoftBreak.DefaultRatio; a surface
        // reading the unset ratio as "no nudge" would describe a rail that fires as one that does not.
        var h = Headroom(Plan(maxSessionTokens: 10_000_000), Running(), Spent(100_000, 50_000, 0, 1_850_000));

        Assert.Equal((long)(10_000_000 * SoftBreak.DefaultRatio), h.NudgeAt);
        Assert.Equal(8_000_000, h.NudgeAt);
        Assert.Equal(2_000_000, h.Tokens);
        Assert.Equal(6_000_000, h.ToNudge);
        Assert.Equal(0.2, h.UsedRatio);
    }

    [Fact]
    public void AConfiguredRatioMovesTheNudgeAndThePlanIsBelieved()
    {
        var h = Headroom(Plan(maxSessionTokens: 10_000_000, softBreakRatio: 0.7), Running(),
            Spent(0, 0, 0, 1_000_000));

        Assert.Equal(7_000_000, h.NudgeAt);
        Assert.Equal(6_000_000, h.ToNudge);
    }

    [Fact]
    public void ARunOverrideOfZeroTurnsTheCeilingOffEvenThoughThePlanSetsOne()
    {
        // `conductor set rollover 0` means OFF for this run. Reporting the plan's ceiling here would
        // put a gauge on screen for a rail that has been switched off — and the session would then
        // sail past a limit the Face was still counting down to.
        var h = Headroom(Plan(maxSessionTokens: 10_000_000), Running(), Spent(0, 0, 0, 1_000_000),
            thisRunOverride: 0);

        Assert.Null(h.Cap);
        Assert.Null(h.NudgeAt);
        Assert.Equal(1_000_000, h.Tokens);
    }

    [Fact]
    public void ARunOverrideBeatsThePlanCeiling()
    {
        var h = Headroom(Plan(maxSessionTokens: 10_000_000), Running(), Spent(0, 0, 0, 1_000_000),
            thisRunOverride: 4_000_000);

        Assert.Equal(4_000_000, h.Cap);
        Assert.Equal(3_200_000, h.NudgeAt);
    }

    [Fact]
    public void TheBurnRateAndTheProjectionComeFromTheSessionsOwnClock()
    {
        // 2,000,000 tokens in 120 seconds = 1,000,000/min. Nudge at 8M, so 6M away = 6 minutes.
        var h = Headroom(Plan(maxSessionTokens: 10_000_000), Running(elapsedSec: 120),
            Spent(100_000, 50_000, 0, 1_850_000));

        // Ranges, not equalities: the clock is the real one, so 120s is 120.0-and-a-bit seconds.
        Assert.NotNull(h.BurnPerMinute);
        Assert.InRange(h.BurnPerMinute!.Value, 990_000, 1_010_000);
        Assert.InRange(h.MinutesToNudge!.Value, 5.9, 6.1);
        Assert.InRange(h.MinutesToCap!.Value, 7.9, 8.1);
    }

    [Fact]
    public void NoRateUntilThereIsEnoughClockToDivideBy()
    {
        // Four seconds in, the first delta would project tens of millions a minute and a nudge "in
        // seconds". A gauge that exists to be the honest one does not get to say that.
        var h = Headroom(Plan(maxSessionTokens: 10_000_000), Running(elapsedSec: 4),
            Spent(100_000, 50_000, 0, 1_850_000));

        Assert.Null(h.BurnPerMinute);
        Assert.Null(h.MinutesToNudge);
        Assert.Null(h.MinutesToCap);
        // The spend and the distances are still known and still reported.
        Assert.Equal(2_000_000, h.Tokens);
        Assert.Equal(6_000_000, h.ToNudge);
    }

    [Fact]
    public void PastTheNudgeTheDistanceGoesNegativeAndTheCountdownDisappears()
    {
        // 8.5M against an 8M nudge. A countdown here would sit at "0m" and read as "about to happen"
        // rather than "already happened", which is the opposite of what the session must be told.
        var h = Headroom(Plan(maxSessionTokens: 10_000_000), Running(), Spent(0, 0, 0, 8_500_000));

        Assert.Equal(-500_000, h.ToNudge);
        Assert.Null(h.MinutesToNudge);
        // The hard ceiling is still ahead, so its countdown is still live.
        Assert.Equal(1_500_000, h.ToCap);
        Assert.NotNull(h.MinutesToCap);
    }

    [Fact]
    public void AFinishedSessionKeepsItsTotalButLosesTheRateAndTheProjection()
    {
        var state = new RunState
        {
            RunId = "r",
            SessionCounter = 1,
            History =
            [
                new SessionRecord
                {
                    Number = 1,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-10),
                    EndedUtc = DateTime.UtcNow,
                    CostUsd = 1.00m,
                },
            ],
        };

        var h = Headroom(Plan(maxSessionTokens: 10_000_000), state, Spent(0, 0, 0, 2_000_000));

        Assert.False(h.Live);
        Assert.Equal(2_000_000, h.Tokens);  // what it burned is still true
        Assert.Null(h.BurnPerMinute);        // a burn rate for something not burning is not
        Assert.Null(h.MinutesToNudge);
        Assert.Equal(8_000_000, h.NudgeAt);  // the ceiling is a fact about the plan, not the session
    }

    [Fact]
    public void NoBlockAtAllBeforeTheFirstSessionStarts()
    {
        var state = new RunState { RunId = "r", SessionCounter = 0 };
        var plan = Plan(maxSessionTokens: 10_000_000);
        var dto = ControlPlaneMapper.FromSnapshot(SnapshotBuilder.Build(plan, state, Track()), "r", ".", "");

        dto = ControlPlaneServer.WithTokenHeadroom(dto, plan.Limits, state, []);

        // Absent, not a zeroed gauge: "no session has run" is a different fact from "a session at 0".
        Assert.Null(dto.TokenHeadroom);
    }

    [Fact]
    public void TheBlockResolvesTheCeilingTheSameWayTheRailEnforcesIt()
    {
        // The regression this guards: the wire growing its own copy of the cap/nudge arithmetic and
        // drifting from SessionRunner. Both sides call SoftBreak, so the parity is checkable here.
        foreach (var (thisRun, planLimit) in new (long?, long?)[]
                 { (null, 10_000_000), (0, 10_000_000), (4_000_000, 10_000_000), (null, null) })
        {
            var h = Headroom(Plan(maxSessionTokens: planLimit), Running(), Spent(0, 0, 0, 1_000), thisRun);
            Assert.Equal(SoftBreak.EffectiveCap(thisRun, planLimit), h.Cap);
            Assert.Equal(SoftBreak.Threshold(h.Cap, null), h.NudgeAt);
        }
    }
}
