using Conductor.Http;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SC2.3 — <c>/state</c> shows live spend, and every number on it can say how it is known.
///
/// The defect these lock down, measured on a scratch rig before the fix: on a claude-provider run
/// <c>GET /state</c> reported <c>sessionCostUsd 0</c> and <c>sessionTokensInput 0</c> for the WHOLE
/// length of a session and then jumped at exit, because <see cref="TokenDelta"/> — the only event the
/// live fold reads — was emitted from exactly one place in the engine, <c>OpencodeProvider</c>.
/// <see cref="ClaudeProvider"/> read usage only off the terminal <c>result</c> envelope. And the cap
/// arithmetic (<c>costSpent</c>/<c>costCap</c>/<c>costRemaining</c>) did not exist on the wire at all,
/// so each surface subtracted lifetime spend from a cap that is measured against a resettable window.
/// </summary>
public class SC23LiveSpendTests
{
    private static (AgentStreamState State, List<TokenDelta> Deltas) NewClaudeState()
    {
        var deltas = new List<TokenDelta>();
        var state = new AgentStreamState(
            (_, _) => { },
            (i, o, r, c, cost) => deltas.Add(new TokenDelta { Input = i, Output = o, Reasoning = r, CacheRead = c, CostUsd = cost }));
        return (state, deltas);
    }

    // ────────────────────────────── the provider: tokens go live ──────────────────────────────

    [Fact]
    public void ClaudeAdapterEmitsOneLiveTokenDeltaPerAssistantMessageNotPerContentBlock()
    {
        var provider = new ClaudeProvider();
        var (state, deltas) = NewClaudeState();

        // Two lines for msg_a (claude re-emits one message once per content block, same usage), then
        // a genuinely new call as msg_b. Shape lifted from a real stream-json log.
        const string msgA =
            """{"type":"assistant","message":{"id":"msg_a","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":11374,"cache_read_input_tokens":12528,"output_tokens":1}}}""";
        const string msgB =
            """{"type":"assistant","message":{"id":"msg_b","content":[{"type":"text","text":"more"}],"usage":{"input_tokens":5,"cache_creation_input_tokens":600,"cache_read_input_tokens":40000,"output_tokens":900}}}""";
        provider.ParseLine(msgA, state);
        provider.ParseLine(msgA, state);
        provider.ParseLine(msgA, state);
        provider.ParseLine(msgB, state);

        Assert.Equal(2, deltas.Count);
        Assert.Equal(2 + 11374, deltas[0].Input);
        Assert.Equal(1, deltas[0].Output);
        Assert.Equal(12528, deltas[0].CacheRead);
        Assert.Equal(5 + 600, deltas[1].Input);
        Assert.Equal(900, deltas[1].Output);
        // No money on the wire before the result envelope, so the delta claims none.
        Assert.All(deltas, d => Assert.Equal(0m, d.CostUsd));
    }

    /// <summary>B13.6: the live deltas now also fold onto the session totals, and the result envelope
    /// overwrites them at the end.</summary>
    /// <remarks>This test previously asserted the opposite — that the totals stayed null until the
    /// result envelope — on the reasoning that <c>TokensTotal</c> gates <c>limits.maxSessionTokens</c>
    /// and double-counting here would break the cap. The first half was right and the conclusion was
    /// backwards: the cap reads those totals, so leaving them null did not protect the cap, it disabled
    /// it. A run with a 6M ceiling reached 17M and first noticed as the session was already ending,
    /// because that is when the envelope finally set the numbers. There is no double-count to fear:
    /// <c>ReadUsage</c> ASSIGNS the envelope's totals rather than adding to them, so the authoritative
    /// figure still wins, and the message-id dedupe keeps the running figure honest until it lands.</remarks>
    [Fact]
    public void ClaudeAdapterLiveDeltasAlsoTrackTheSessionTotals_AndTheEnvelopeStillWins()
    {
        var provider = new ClaudeProvider();
        var (state, deltas) = NewClaudeState();

        provider.ParseLine(
            """{"type":"assistant","message":{"id":"msg_a","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":11374,"cache_read_input_tokens":12528,"output_tokens":1}}}""",
            state);

        // The live channel fired…
        Assert.Single(deltas);
        // …and the session totals moved with it, so a rail asking what this session has spent gets an
        // answer while it can still act on one.
        Assert.Equal(2 + 11374, state.TokensInput);
        Assert.Equal(1, state.TokensOutput);
        Assert.Equal(12528, state.TokensCacheRead);

        // The envelope is still the authority: it assigns, so the session's recorded total is the
        // CLI's own number and not a sum this parser accumulated.
        provider.ParseLine(
            """{"type":"result","subtype":"success","usage":{"input_tokens":7,"cache_creation_input_tokens":20000,"cache_read_input_tokens":99999,"output_tokens":42}}""",
            state);

        Assert.Equal(7 + 20000, state.TokensInput);
        Assert.Equal(42, state.TokensOutput);
        Assert.Equal(99999, state.TokensCacheRead);
    }

    [Fact]
    public void ClaudeAdapterSkipsAnAssistantEnvelopeWithNoMessageId()
    {
        var provider = new ClaudeProvider();
        var (state, deltas) = NewClaudeState();

        // With no id there is no way to tell a fresh API call from a re-emission of one already
        // counted. A live ticker reading 4x high is no better than one reading zero — so: nothing.
        provider.ParseLine(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":2,"output_tokens":1}}}""",
            state);

        Assert.Empty(deltas);
    }

    [Fact]
    public void ClaudeAdapterStillReadsTheAuthoritativeTotalOffTheResultEnvelope()
    {
        var provider = new ClaudeProvider();
        var (state, _) = NewClaudeState();

        provider.ParseLine(
            """{"type":"assistant","message":{"id":"msg_a","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":11374,"cache_read_input_tokens":12528,"output_tokens":1}}}""",
            state);
        provider.ParseLine(
            """{"type":"result","subtype":"success","result":"done","total_cost_usd":2.6979,"usage":{"input_tokens":641,"cache_creation_input_tokens":96444,"cache_read_input_tokens":2327709,"output_tokens":22543}}""",
            state);

        Assert.Equal(2.6979m, state.CostUsd);
        Assert.Equal(641 + 96444, state.TokensInput);
        Assert.Equal(22543, state.TokensOutput);
    }

    // ────────────────────────────── the estimator: labelled, never invented ──────────────────

    private static SessionRecord Finished(int n, decimal cost, long input, long output, long cacheRead = 0)
        => new()
        {
            Number = n,
            StartedUtc = new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc),
            EndedUtc = new DateTime(2026, 7, 31, 6, 30, 0, DateTimeKind.Utc),
            CostUsd = cost,
            TokensInput = input,
            TokensOutput = output,
            TokensCacheRead = cacheRead,
        };

    [Fact]
    public void EstimatorPricesLiveTokensAtTheRunsOwnObservedRate()
    {
        // One finished session: $2.00 for 200,000 tokens = $0.00001/token.
        var history = new List<SessionRecord> { Finished(1, 2.00m, input: 150_000, output: 50_000) };
        var live = new LiveMetrics.SessionTokenTotals(Input: 30_000, Output: 10_000, Reasoning: 0, CacheRead: 0, CostUsd: 0m);

        var est = LiveCostEstimator.ForLiveSession(live, history);

        Assert.Equal(LiveCostEstimator.BasisRunRate, est.Basis);
        Assert.Equal(0.40m, est.CostUsd); // 40,000 tokens x $0.00001
    }

    [Fact]
    public void EstimatorSaysNoRateYetRatherThanInventingAPrice()
    {
        // Real tokens, but nothing this run has ever been billed for — so there is no rate, and the
        // engine says exactly that instead of reaching for a hard-coded price list.
        var live = new LiveMetrics.SessionTokenTotals(Input: 30_000, Output: 10_000, Reasoning: 0, CacheRead: 0, CostUsd: 0m);

        var est = LiveCostEstimator.ForLiveSession(live, Array.Empty<SessionRecord>());

        Assert.Equal(LiveCostEstimator.BasisNoRate, est.Basis);
        Assert.Equal(0m, est.CostUsd);
    }

    [Fact]
    public void EstimatorPrefersCostTheProviderActuallyStreamed()
    {
        // opencode puts money on the wire per step. That is not an estimate and must not be relabelled
        // as one, nor overwritten by a rate derived from other sessions.
        var history = new List<SessionRecord> { Finished(1, 2.00m, input: 150_000, output: 50_000) };
        var live = new LiveMetrics.SessionTokenTotals(Input: 30_000, Output: 10_000, Reasoning: 0, CacheRead: 0, CostUsd: 0.07m);

        var est = LiveCostEstimator.ForLiveSession(live, history);

        Assert.Equal(LiveCostEstimator.BasisStreamed, est.Basis);
        Assert.Equal(0.07m, est.CostUsd);
    }

    [Fact]
    public void EstimatorIgnoresUnpricedAndUnfinishedSessionsWhenLearningTheRate()
    {
        var history = new List<SessionRecord>
        {
            Finished(1, 2.00m, input: 150_000, output: 50_000),
            new() { Number = 2, StartedUtc = DateTime.UtcNow, EndedUtc = null, CostUsd = null }, // in flight
            new() { Number = 3, StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, CostUsd = null, TokensInput = 999_999 }, // untracked
        };

        Assert.Equal(2.00m / 200_000, LiveCostEstimator.ObservedRatePerToken(history));
    }

    // ────────────────────────────── /state: in flight, then recorded ─────────────────────────

    private static PlanConfig Plan(decimal? cap = null) => new()
    {
        Name = "SC23",
        Repo = ".",
        Stages = [new StageConfig { Id = "T0", Title = "t" }],
        Limits = new LimitsConfig { MaxRunCostUsd = cap },
    };

    private static TrackerSnapshot Track(int done, int total)
    {
        var rows = new List<CheckpointRow>();
        for (var i = 1; i <= total; i++)
            rows.Add(new CheckpointRow($"T0.{i}", $"cp {i}", i <= done ? "DONE" : "TODO", "-", "-"));
        return new TrackerSnapshot { Checkpoints = rows };
    }

    private static StateDto Dto(PlanConfig plan, RunState state, TrackerSnapshot track)
        => ControlPlaneDto.FromSnapshot(SnapshotBuilder.Build(plan, state, track), state.RunId, plan.Repo, plan.PlanDir);

    [Fact]
    public void StateReportsLiveTokensAndALabelledCostWhileTheAgentIsStillRunning()
    {
        var state = new RunState
        {
            RunId = "r",
            SessionCounter = 2,
            History =
            [
                Finished(1, 2.00m, input: 150_000, output: 50_000),
                new SessionRecord { Number = 2, StartedUtc = DateTime.UtcNow.AddMinutes(-5), EndedUtc = null },
            ],
        };
        ConductorEvent[] events =
        [
            new TokenDelta { SessionId = "2", Input = 30_000, Output = 10_000, Seq = 1, Ts = DateTimeOffset.UtcNow },
        ];

        var dto = ControlPlaneServer.WithLiveSessionMetrics(Dto(Plan(), state, Track(0, 2)), events, state);

        Assert.True(dto.AgentActive);
        Assert.Equal(30_000, dto.SessionTokensInput);
        Assert.Equal(10_000, dto.SessionTokensOutput);
        Assert.Equal(0.40m, dto.SessionCostUsd);
        Assert.Equal(LiveCostEstimator.BasisRunRate, dto.SessionCostBasis);
        // …and it is folded into the run total, which the finished-session sum alone would put at 2.00.
        Assert.Equal(2.40m, dto.TotalCostUsd);
    }

    [Fact]
    public void StateReportsTheRecordedTotalOnceTheSessionEndedNotTheDiscardedEstimate()
    {
        // The bug this pins: the live fold was served unconditionally, so the instant a claude session
        // ended, sessionCostUsd snapped from "whatever was folded" back to 0 — the recorded cost was
        // on the record and never reached the wire.
        var state = new RunState
        {
            RunId = "r",
            SessionCounter = 1,
            History = [Finished(1, 1.75m, input: 35_700, output: 4_200, cacheRead: 400_000)],
        };

        var dto = ControlPlaneServer.WithLiveSessionMetrics(Dto(Plan(), state, Track(0, 2)), [], state);

        Assert.False(dto.AgentActive);
        Assert.Equal(1.75m, dto.SessionCostUsd);
        Assert.Equal(LiveCostEstimator.BasisMeasured, dto.SessionCostBasis);
        Assert.Equal(35_700, dto.SessionTokensInput);
        Assert.Equal(4_200, dto.SessionTokensOutput);
    }

    // ────────────────────────────── /state: the budget block ─────────────────────────────────

    [Fact]
    public void BudgetBlockAnswersSpentCapRemainingMeanAndCheckpointsRemaining()
    {
        var state = new RunState
        {
            RunId = "r",
            SessionCounter = 2,
            PerRunCostUsd = 3.50m,
            History = [Finished(1, 1.75m, 100, 100), Finished(2, 1.75m, 100, 100)],
        };
        var plan = Plan(cap: 10m);

        var dto = ControlPlaneServer.WithBudget(Dto(plan, state, Track(1, 4)), plan.Limits, state);

        Assert.Equal(3.50m, dto.CostSpent);
        Assert.Equal(10m, dto.CostCap);
        Assert.Equal(6.50m, dto.CostRemaining);
        Assert.Equal(1.75m, dto.MeanSessionCost);
        Assert.Equal(3, dto.CheckpointsRemaining);
    }

    [Fact]
    public void BudgetBlockReportsNoRemainingAtAllWhenThePlanSetsNoCap()
    {
        var state = new RunState { RunId = "r", PerRunCostUsd = 3.50m };
        var plan = Plan(cap: null);

        var dto = ControlPlaneServer.WithBudget(Dto(plan, state, Track(0, 2)), plan.Limits, state);

        // Null, not decimal.MaxValue and not 0: "this plan set no ceiling" and "there is plenty left"
        // are different facts and must not render the same.
        Assert.Null(dto.CostCap);
        Assert.Null(dto.CostRemaining);
        Assert.Equal(3.50m, dto.CostSpent);
    }

    [Fact]
    public void WindowAndLifetimeAgreeUntilAnApprovalAndDivergeAfterOne()
    {
        // Before any approval the two are the same number, and a surface may compare them freely.
        var before = new RunState
        {
            RunId = "r",
            PerRunCostUsd = 3.50m,
            History = [Finished(1, 1.75m, 100, 100), Finished(2, 1.75m, 100, 100)],
        };
        var plan = Plan(cap: 3m);
        var dtoBefore = ControlPlaneServer.WithBudget(Dto(plan, before, Track(0, 2)), plan.Limits, before);
        Assert.Equal(dtoBefore.LifetimeCostUsd, dtoBefore.WindowCostUsd);
        Assert.Equal(0, dtoBefore.BudgetApprovals);
        Assert.Null(dtoBefore.BudgetWindowStartedUtc);

        // The approval zeroes the window and stamps when the new one opened. The lifetime is untouched:
        // approving a budget raises the ceiling, it does not un-spend the money. Serving only one of
        // these was the whole defect — the takeover subtraction was wrong by the pre-approval spend.
        var opened = new DateTime(2026, 7, 31, 19, 3, 0, DateTimeKind.Utc);
        var after = new RunState
        {
            RunId = "r",
            PerRunCostUsd = 0.25m,
            BudgetWindowStartedUtc = opened,
            BudgetApprovals = 1,
            History = [Finished(1, 1.75m, 100, 100), Finished(2, 1.75m, 100, 100), Finished(3, 0.25m, 100, 100)],
        };
        var dtoAfter = ControlPlaneServer.WithBudget(Dto(plan, after, Track(0, 2)), plan.Limits, after);

        Assert.Equal(0.25m, dtoAfter.WindowCostUsd);
        Assert.Equal(0.25m, dtoAfter.CostSpent);
        Assert.Equal(2.75m, dtoAfter.CostRemaining);   // against the $3 cap, which measures the window
        Assert.Equal(3.75m, dtoAfter.LifetimeCostUsd); // and the run has really spent this much
        Assert.Equal(opened, dtoAfter.BudgetWindowStartedUtc);
        Assert.Equal(1, dtoAfter.BudgetApprovals);
    }

    [Fact]
    public void InFlightSpendCountsAgainstTheCapNotJustAgainstTheLifetimeTotal()
    {
        // A session that has already burned money is not free until it exits: the window figure the
        // cap is compared against has to include it, or a run sails past its ceiling mid-session.
        var state = new RunState
        {
            RunId = "r",
            SessionCounter = 2,
            PerRunCostUsd = 2.00m,
            History =
            [
                Finished(1, 2.00m, input: 150_000, output: 50_000),
                new SessionRecord { Number = 2, StartedUtc = DateTime.UtcNow.AddMinutes(-5), EndedUtc = null },
            ],
        };
        ConductorEvent[] events =
        [
            new TokenDelta { SessionId = "2", Input = 30_000, Output = 10_000, Seq = 1, Ts = DateTimeOffset.UtcNow },
        ];
        var plan = Plan(cap: 3m);

        var dto = ControlPlaneServer.WithLiveSessionMetrics(Dto(plan, state, Track(0, 2)), events, state);
        dto = ControlPlaneServer.WithBudget(dto, plan.Limits, state);

        Assert.Equal(2.40m, dto.CostSpent);      // 2.00 recorded + 0.40 in flight
        Assert.Equal(0.60m, dto.CostRemaining);
        Assert.Equal(2.40m, dto.LifetimeCostUsd);
    }
}
