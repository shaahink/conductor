using System.Net;
using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core.Http;

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
        var dto = ControlPlaneDto.FromSnapshot(snap, runState.RunId, _plan.Repo, _plan.PlanDir,
            _state.MaxSessionTokensThisRun, _plan.Tracker, _plan.StateDir);
        dto = WithLiveSessionMetrics(dto, events, runState);

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
        };
        await WriteJsonAsync(ctx, dto, ControlPlaneJsonContext.Default.StateDto).ConfigureAwait(false);
    }

    /// <summary>M5.4: fold <see cref="TokenDelta"/> for the current session so the ticker's cost/tokens
    /// accrue DURING a session, not only when <c>SessionFinished</c> lands. The 3-arg
    /// <see cref="SnapshotBuilder"/> can't see the event log, so it always reports zero live spend; here
    /// we add the in-flight session's folded deltas on top of the (finished-session) totals it produced.
    /// Once the session finishes its cost is in <see cref="RunState.History"/>, so we stop adding the
    /// live estimate to avoid double-counting.</summary>
    internal static StateDto WithLiveSessionMetrics(StateDto dto, IReadOnlyList<ConductorEvent> events, RunState runState)
    {
        if (runState.SessionCounter <= 0) return dto;

        var current = runState.History.LastOrDefault(h => h.Number == runState.SessionCounter);
        var live = LiveMetrics.ForSession(events, runState.SessionCounter);
        var sessionLive = current is { EndedUtc: null };
        var elapsed = sessionLive && current != null
            ? Math.Max(0, (DateTime.UtcNow - current.StartedUtc).TotalSeconds)
            : dto.SessionElapsedSec;

        return dto with
        {
            AgentActive = sessionLive,
            SessionElapsedSec = elapsed,
            SessionCostUsd = live.CostUsd,
            SessionTokensInput = live.Input,
            SessionTokensOutput = live.Output,
            SessionTokensReasoning = live.Reasoning,
            TotalCostUsd = sessionLive ? dto.TotalCostUsd + live.CostUsd : dto.TotalCostUsd,
            TokensInput = sessionLive ? dto.TokensInput + live.Input : dto.TokensInput,
            TokensOutput = sessionLive ? dto.TokensOutput + live.Output : dto.TokensOutput,
            TokensReasoning = sessionLive ? dto.TokensReasoning + live.Reasoning : dto.TokensReasoning,
        };
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
        => Planning.WorkSnapshot.Read(_store, _state.RunId, ReadTrackerSafe);
}
