using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.Http;
using System.Net;
using Conductor.Core.Events;
using Conductor.Core.Face;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Http;

/// <summary>
/// The <c>GET /state</c> projection: fold the event log, layer the live RunState the fold cannot see,
/// and stamp the identity fields (model, provider) the Face reads. Split out of
/// ControlPlaneServer.Endpoints.cs when U3.3's provider field pushed that file past its 500-line
/// ceiling — this is a responsibility of its own, and it is the one that keeps growing: every "the
/// Face is showing something stale/false" fix so far has landed here.
/// </summary>
public sealed partial class ControlPlaneServer
{
    private IReadOnlyList<ConductorEvent> ReadEvents()
    {
        return _store.ReadAllEvents(_state.RunId);
    }

    /// <summary>SC2.4: the incremental read behind <c>GET /events</c>. The folds (<c>/state</c>,
    /// <c>/tasks</c>) genuinely need every event; a TAIL does not, and paying for the full log once a
    /// second per client was the difference between an idle engine and a busy one on a long run.</summary>
    private IReadOnlyList<ConductorEvent> ReadEventsAfter(long afterSeq)
    {
        return _store.ReadEventsAfter(_state.RunId, afterSeq);
    }

    private async Task WriteStateAsync(HttpListenerContext ctx)
    {
        var events = ReadEvents();
        var runState = RunStateProjection.Fold(events);
        // W1.4: the sidebar/chips fold the same WORK GRAPH the Kanban serves — G11's
        // "sidebar full / Kanban empty" split is structurally impossible now.
        var track = GraphTrackerSnapshot();
        var snap = SnapshotBuilder.Build(_plan, runState, track);
        // _state is the live RunState the dispatcher mutates — the set-rollover override lives only
        // there (P5: run-state, not an event), so the fold above can never see it.
        var dto = ControlPlaneMapper.FromSnapshot(snap, runState.RunId, _plan.Repo, _plan.PlanDir,
            _state.MaxSessionTokensThisRun, _plan.Tracker, _plan.StateDir);
        dto = WithLiveSessionMetrics(dto, events, runState);
        // SC2.3: the budget block reads the LIVE RunState, not the fold — PerRunCostUsd (the window
        // the cap is actually measured against) and the approval bookkeeping are run-state, so the
        // fold cannot see them, and every surface that tried to derive them got it wrong.
        dto = WithBudget(dto, _plan.Limits, _state);
        // K4.4: and the token rail beside the money one. After WithLiveSessionMetrics, which is what
        // decides AgentActive and the elapsed clock the burn rate divides by.
        dto = WithTokenHeadroom(dto, _plan.Limits, runState, events);

        // The folded projection never carries run-loop status (it is runtime state, not an event):
        // SnapshotBuilder saw a perpetual Idle, so the Face's top bar read IDLE — and its kind slot
        // "s1 Idle" — through an entire live session (2026-07-16 dogfood). Stamp status, attention,
        // kind, attempt, and model from the live RunState + the latest SessionStarted event instead.
        var lastStart = events.OfType<SessionStarted>().LastOrDefault();
        var stageCfg = _plan.Stages.FirstOrDefault(s => s.Id == _state.CurrentStage);
        dto = dto with
        {
            Status = _state.Status.ToString(),
            AttentionReason = _state.AttentionReason,
            AttentionSinceUtc = _state.AttentionSinceUtc,
            // SC5.1: the declared wait is run-state for the same reason the status is — the fold
            // cannot see it. Measured, not assumed: a live rig served `status: Waiting` with an empty
            // blockedUntilUtc until these two lines existed, which is the exact half-truth this file
            // exists to stop.
            BlockedUntilUtc = _state.BlockedUntilUtc,
            BlockedReason = _state.BlockedReason,
            SessionKind = lastStart?.Kind ?? "-",
            Attempt = lastStart?.Attempt ?? dto.Attempt,
            MaxAttempts = lastStart?.MaxAttempts ?? dto.MaxAttempts,
            Model = lastStart?.Model ?? stageCfg?.Agent?.Model ?? _plan.Agent.Model ?? "",
            // Resolved through the engine's own factory off the stage's EFFECTIVE agent config, so the
            // Face is told what is actually running rather than what the plan happened to spell out.
            // No stage match (before the first stage is entered, or a stage a live reload removed)
            // falls back to the plan's own agent rather than reporting nothing.
            Provider = AgentProviderFactory.ResolveName(
                stageCfg is null ? _plan.Agent : _plan.ResolveAgent(stageCfg)),
            // SF3.3: the repo's git state. Cached (GitSnapshotCache.Ttl), because this endpoint is
            // polled once a second by every attached Face and git awareness must not cost two
            // process spawns per second per viewer.
            Git = GitDto.From(GitSnapshotCache.Get(_plan.Repo)),
            // FU-OWNER-10: which build is serving this run, and which Face would it launch. The
            // engine's half is free (assembly attributes); the Face's is a cached byte scan of the
            // binary FaceLauncher resolves, and says "unstamped" rather than inventing a sha.
            EngineVersion = BuildInfo.Current.Version,
            EngineCommit = BuildInfo.Current.Dirty
                ? BuildInfo.Current.CommitSha + ".dirty"
                : BuildInfo.Current.CommitSha,
            FaceBuild = FaceBuildStamp.Current(),
        };
        await WriteJsonAsync(ctx, dto, ControlPlaneJsonContext.Default.StateDto).ConfigureAwait(false);
    }

    /// <summary>M5.4: fold <see cref="TokenDelta"/> for the current session so the ticker's cost/tokens
    /// accrue DURING a session, not only when <c>SessionFinished</c> lands. The 3-arg
    /// <see cref="SnapshotBuilder"/> can't see the event log, so it always reports zero live spend; here
    /// we add the in-flight session's folded deltas on top of the (finished-session) totals it produced.
    /// Once the session finishes its cost is in <see cref="RunState.History"/>, so we stop adding the
    /// live estimate to avoid double-counting.
    /// <para>SC2.3 extends it two ways. In flight, the folded deltas are priced by
    /// <see cref="LiveCostEstimator"/> and the answer is LABELLED, because for a claude-provider run
    /// the deltas carry tokens but no money. Once the session is over, its cost and tokens are read
    /// off the finished record: the fold is a live estimate and has no business outliving the thing
    /// it estimated — reporting it after exit is how <c>sessionCostUsd</c> snapped back to 0.00 the
    /// instant a claude session ended.</para></summary>
    internal static StateDto WithLiveSessionMetrics(StateDto dto, IReadOnlyList<ConductorEvent> events, RunState runState)
    {
        ArgumentNullException.ThrowIfNull(runState);
        if (runState.SessionCounter <= 0) return dto;

        var current = runState.History.LastOrDefault(h => h.Number == runState.SessionCounter);
        var live = LiveMetrics.ForSession(events, runState.SessionCounter);
        var sessionLive = current is { EndedUtc: null };
        var elapsed = sessionLive && current != null
            ? Math.Max(0, (DateTime.UtcNow - current.StartedUtc).TotalSeconds)
            : dto.SessionElapsedSec;

        if (!sessionLive)
        {
            // Session over: the CLI's own recorded numbers, or nothing to say if it recorded none.
            var recorded = current?.CostUsd;
            return dto with
            {
                AgentActive = false,
                SessionElapsedSec = elapsed,
                SessionCostUsd = recorded ?? 0m,
                SessionCostBasis = recorded is null ? LiveCostEstimator.BasisNone : LiveCostEstimator.BasisMeasured,
                SessionTokensInput = current?.TokensInput ?? 0,
                SessionTokensOutput = current?.TokensOutput ?? 0,
                SessionTokensReasoning = current?.TokensReasoning ?? 0,
            };
        }

        var estimate = LiveCostEstimator.ForLiveSession(live, runState.History);
        return dto with
        {
            AgentActive = true,
            SessionElapsedSec = elapsed,
            SessionCostUsd = estimate.CostUsd,
            SessionCostBasis = estimate.Basis,
            SessionTokensInput = live.Input,
            SessionTokensOutput = live.Output,
            SessionTokensReasoning = live.Reasoning,
            TotalCostUsd = dto.TotalCostUsd + estimate.CostUsd,
            TokensInput = dto.TokensInput + live.Input,
            TokensOutput = dto.TokensOutput + live.Output,
            TokensReasoning = dto.TokensReasoning + live.Reasoning,
        };
    }

    /// <summary>SC2.3: the spend-vs-cap block, computed once by the engine instead of re-derived by
    /// every surface. <paramref name="liveState"/> is the run loop's own RunState — the only place
    /// <see cref="RunState.PerRunCostUsd"/> (spend in the CURRENT budget window) and the approval
    /// bookkeeping live.</summary>
    /// <remarks>The distinction that makes this worth a function: <c>costSpent</c> is what the run has
    /// billed and <c>costCap</c> is the ceiling in force, which is the plan's <c>limits.maxRunCostUsd</c>
    /// plus every raise an owner has approved. Serving one number and letting readers subtract produced
    /// a remaining figure that was wrong by the entire pre-approval spend.
    /// <para>KS5.4 removed the reset that made this hard: an approval raises the ceiling and leaves the
    /// spend alone, so spend-vs-cap is one monotone comparison from the first session to the last.
    /// <c>windowCostUsd</c> keeps SC2.3's other question — what has it spent since the last approval —
    /// and is the only figure here that moves backwards.</para>
    /// <para><paramref name="dto"/> arrives with the in-flight estimate already folded into
    /// <c>TotalCostUsd</c> and <c>SessionCostUsd</c> by <see cref="WithLiveSessionMetrics"/>, so the
    /// window adds the same in-flight figure and the two stay consistent to the cent.</para></remarks>
    internal static StateDto WithBudget(StateDto dto, LimitsConfig? limits, RunState liveState)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(liveState);

        var inFlight = dto.AgentActive ? dto.SessionCostUsd : 0m;
        // KS5.2: BilledWindowCostUsd, not PerRunCostUsd — the same sum CheckBudgetCap parks on, so the
        // spend an operator reads and the spend the run stops at are one number. The lifetime figure
        // carries the run's side spend for the same reason: window may never exceed lifetime.
        var window = liveState.BilledWindowCostUsd + inFlight;
        var lifetime = dto.TotalCostUsd + liveState.TotalSideCostUsd;
        // KS5.4: the EFFECTIVE ceiling — the plan's cap plus everything an owner has approved on top of
        // it. Serving the plan's raw cap here would have put the wire back where the field log found it:
        // a run governed by $6.00 while every surface printed $3.00.
        var cap = BudgetCeiling.EffectiveCostCap(limits?.MaxRunCostUsd, liveState.BudgetGrantUsd);

        var priced = liveState.History.Where(h => h.EndedUtc != null && h.CostUsd is > 0).ToList();
        var mean = priced.Count > 0
            ? decimal.Round(priced.Sum(h => h.CostUsd!.Value) / priced.Count, 4, MidpointRounding.AwayFromZero)
            : 0m;

        return dto with
        {
            CostSpent = window,
            CostCap = cap,
            // No cap means no remaining — NOT an unbounded one. A surface must be able to tell
            // "this plan set no ceiling" apart from "there is plenty left".
            CostRemaining = cap is { } c ? c - window : null,
            MeanSessionCost = mean,
            CheckpointsRemaining = Math.Max(0, dto.TotalCount - dto.DoneCount),
            // KS5.4: spend SINCE THE LAST RAISE. costSpent/costCap/costRemaining are now one monotone
            // comparison for the life of the run — an approval widens the ceiling instead of zeroing the
            // spend — so this is the field that keeps answering SC2.3's question, "what has it spent
            // since I last approved". With no approval on file it is the whole run, exactly as before.
            WindowCostUsd = liveState.SpendSinceLastRaiseUsd + inFlight,
            LifetimeCostUsd = lifetime,
            BudgetWindowStartedUtc = liveState.BudgetWindowStartedUtc,
            BudgetApprovals = liveState.BudgetApprovals,
        };
    }

    /// <summary>A rate needs enough clock to be a rate. Below this, the first delta divided by a
    /// second or two projects a burn of tens of millions a minute and a nudge "in 4 seconds" — a
    /// number that is not wrong so much as meaningless, and it lands in the gauge that is supposed to
    /// be the honest one.</summary>
    private const double MinRateSeconds = 20;

    /// <summary>K4.4: live token headroom — the token half of what SC2.3 did for money. The session's
    /// spend against the ceiling that will actually end it, the distance to the cooperative nudge, a
    /// burn rate and a projection, computed once by the engine so a remote surface renders rather than
    /// derives.</summary>
    /// <remarks>Two things here are deliberate and were the whole reason the block exists.
    /// <para>First, <c>Tokens</c> is folded INCLUDING cache-read, because that is what
    /// <c>SessionRunner.LiveTokens</c> compares against the ceiling. The wire's existing
    /// <c>sessionTokens*</c> triple excludes it, and on this project cache reads are 98% of every
    /// token spent — a Face adding up the three visible fields would have drawn a nearly empty gauge
    /// for a session the engine was about to kill.</para>
    /// <para>Second, the cap and the nudge come from <see cref="SoftBreak"/>, the rail's own
    /// arithmetic, rather than from a fourth copy of the 0.8 fallback. Headroom measured against a
    /// cap that is not the enforced one is worse than no headroom at all.</para></remarks>
    internal static StateDto WithTokenHeadroom(
        StateDto dto, LimitsConfig? limits, RunState folded, IReadOnlyList<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(folded);
        ArgumentNullException.ThrowIfNull(events);
        if (folded.SessionCounter <= 0) return dto;

        var live = LiveMetrics.ForSession(events, folded.SessionCounter);
        var tokens = live.Input + live.Output + live.Reasoning + live.CacheRead;
        // The override the wire already reports, so the gauge and the field can never disagree.
        var cap = SoftBreak.EffectiveCap(dto.MaxSessionTokensThisRun, limits?.MaxSessionTokens);
        var nudge = SoftBreak.Threshold(cap, limits?.SoftBreakRatio);
        var toNudge = nudge is { } n ? n - tokens : (long?)null;
        var toCap = cap is { } c ? c - tokens : (long?)null;

        double? burn = dto.AgentActive && dto.SessionElapsedSec >= MinRateSeconds && tokens > 0
            ? tokens / (dto.SessionElapsedSec / 60.0)
            : null;

        return dto with
        {
            TokenHeadroom = new TokenHeadroomDto(
                Tokens: tokens,
                Cap: cap,
                NudgeAt: nudge,
                ToNudge: toNudge,
                ToCap: toCap,
                UsedRatio: cap is { } capped and > 0 ? (double)tokens / capped : null,
                BurnPerMinute: burn,
                MinutesToNudge: Eta(toNudge, burn),
                MinutesToCap: Eta(toCap, burn),
                Live: dto.AgentActive),
        };
    }

    /// <summary>Minutes to close a distance at a rate. Null when there is no rate, or when the
    /// distance is already behind — an ETA to a threshold that has been crossed is a countdown that
    /// renders "0m" forever, which reads as "about to happen" rather than "already happened".</summary>
    private static double? Eta(long? distance, double? perMinute) =>
        distance is { } d and > 0 && perMinute is { } rate and > 0 ? d / rate : null;

    /// <summary>SF4.1 — <c>GET /owner/queue</c>. Reads the LIVE <see cref="RunState"/>, not the event
    /// fold: the park, the owner approvals, the blocked-until wait and the skipped stages are run
    /// state, and a queue derived from the fold would keep offering an approval the owner had already
    /// given. The tracker comes from the work graph, same as <c>/state</c>, so the handoff block
    /// (where <c>HUMAN:</c> lines live) and the board agree with every other surface.</summary>
    private async Task WriteOwnerQueueAsync(HttpListenerContext ctx)
    {
        var now = DateTime.UtcNow;
        var items = OwnerQueue.Collect(_plan, _state, GraphTrackerSnapshot(), now);
        await WriteJsonAsync(ctx, OwnerQueueDto.From(items, now),
            ControlPlaneJsonContext.Default.OwnerQueueDto).ConfigureAwait(false);
    }

    private TrackerSnapshot ReadTrackerSafe()
    {
        try { return ProgressProviderFactory.Create(_plan).Read(_plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    /// <summary>W1.4: the /state projection's checkpoint rows come from the WORK GRAPH — the same
    /// fold <c>GET /tasks</c> serves — with explicit status flags (no conventions round-trip: the
    /// graph's labels are canonical). Before anything is seeded, the declared tracker is all there
    /// is; it also still carries the handoff block, which is view-only prose.</summary>
    /// <remarks>W5.1 moved the fold itself into <see cref="Planning.WorkSnapshot"/>, which the ENGINE
    /// now schedules on too — the projection had two implementations, and only one of them was the
    /// truth the loop acted on.</remarks>
    private TrackerSnapshot GraphTrackerSnapshot()
        => Conductor.Core.Planning.WorkSnapshot.Read(_store, _state.RunId, ReadTrackerSafe);
}
