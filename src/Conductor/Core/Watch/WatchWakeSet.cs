using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Watch;

/// <summary>
/// SF5.1 — the classifier. Pure and stateful: feed it the run's event stream in order and it answers
/// <c>null</c> for everything a supervisor must sleep through, and a <see cref="WatchWake"/> for the
/// six things worth waking for. No disk, no clock, no network — so the whole wake/don't-wake policy
/// is unit-testable event by event, which is the only way "it stays silent through a backoff" is a
/// measurement rather than a claim.
/// </summary>
/// <remarks>
/// Two of the six wakes are patterns rather than single events, and this recomputes them from the
/// wire instead of asking the engine, because the engine does not emit either one as an event:
/// <list type="bullet">
/// <item><b>Circuit breaker.</b> <see cref="FailureCircuitBreaker"/> is the engine's <em>policy</em>
/// (it consults the advisor, which may well grant more attempts); this is the <em>symptom</em> — the
/// same breakable outcome twice in a row on one stage. The symptom is deliberately the wider net: a
/// supervisor wants to see the churn even when the advisor decides to ride it out. Where the wire
/// carries what the engine reads (commits on a Stalled/TimedOut pair) this applies the same test.</item>
/// <item><b>Phase RED twice.</b> A phase battery is emitted as one contiguous burst of
/// <c>gateFinished</c> events with <c>scope=phase</c> (GateOrchestrator.PersistGates loops over the
/// results), so a burst is a battery and a burst holding a failed required gate is a RED battery.</item>
/// </list>
/// </remarks>
public sealed class WatchWakeSet
{
    // The engine's own breakable set (FailureCircuitBreaker) — LimitBackoff, BlockedUntil, RolledOver
    // and KilledByUser are absent from it on purpose: those are the self-resuming outcomes, and waking
    // on them is exactly the polling babysitter's mistake in a cheaper wrapper.
    private static readonly HashSet<SessionOutcome> Breakable =
    [
        SessionOutcome.Stalled,
        SessionOutcome.TimedOut,
        SessionOutcome.GatesRed,
        SessionOutcome.AgentError,
        SessionOutcome.NoProgress,
    ];

    private string? _stage;
    private string? _prevStage;
    private SessionOutcome? _prevOutcome;
    private bool _prevProduced;
    private readonly Dictionary<string, int> _phaseReds = new(StringComparer.Ordinal);
    private bool _inPhaseBurst;
    private bool _burstCountedRed;
    private long _lastPhaseGateSeq = long.MinValue;

    /// <summary>The stage the stream has walked into, or null before the first <c>stageEntered</c>.</summary>
    public string? CurrentStage => _stage;

    /// <summary>How many RED phase batteries this stream has seen for a stage. Exposed so a test can
    /// prove the counter is per-stage and not global.</summary>
    public int PhaseRedsFor(string stageId) => _phaseReds.TryGetValue(stageId, out var n) ? n : 0;

    /// <summary>Classify one event. <c>null</c> means stay silent — the common case, and the point.</summary>
    public WatchWake? Observe(ConductorEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // A phase battery is a contiguous burst of phase-scoped gate events; anything else ends it.
        if (evt is not GateFinished { Scope: "phase" }) { _inPhaseBurst = false; _burstCountedRed = false; }

        switch (evt)
        {
            case StageEntered e:
                _stage = e.StageId;
                return null;

            case AttentionRequested e:
                return new WatchWake(WatchReason.NeedsHuman, e.Reason, _stage, evt.Seq);

            case OwnerApprovalRequested e:
                return new WatchWake(WatchReason.OwnerPark,
                    $"stage {e.StageId} is awaiting the owner", e.StageId, evt.Seq);

            case RunFinished e:
                return new WatchWake(WatchReason.RunEnded,
                    $"run {e.Status} — {e.CheckpointsDone}/{e.CheckpointsTotal} checkpoints over {e.Sessions} session(s)",
                    _stage, evt.Seq);

            case SessionFinished e:
                return ObserveSessionFinished(e);

            case GateFinished e:
                return ObservePhaseGate(e);

            // Everything else is the silent set, named here so adding an event type to the engine is a
            // deliberate decision about supervision rather than an accidental new alarm:
            // runStarted, sessionStarted, stageConfirmed, checkpointConfirmed, tokenDelta, noteAdded,
            // taskAdded, taskStatusChanged, taskDetailEdited, mcpCallFinished, ownerApprovalGranted,
            // softBreakRequested (rollover — self-resumes), blockedUntilRequested + runBlockedUntil
            // (an agent-declared nap the engine wakes itself from), planReloaded, the lane events and
            // rollbackExecuted.
            default:
                return null;
        }
    }

    private WatchWake? ObserveSessionFinished(SessionFinished e)
    {
        var produced = (e.NewCommits?.Count ?? 0) > 0 || (e.SatelliteCommits?.Count ?? 0) > 0;
        var outcome = Enum.TryParse<SessionOutcome>(e.Outcome, ignoreCase: true, out var parsed)
            ? parsed
            : (SessionOutcome?)null;

        var repeat = outcome is { } o
            && Breakable.Contains(o)
            && _prevOutcome == o
            && string.Equals(_prevStage, e.StageId, StringComparison.Ordinal)
            // The engine reads a Stalled/TimedOut pair as identical only when NEITHER produced work;
            // the wire carries the commits, so apply the same test rather than a looser one.
            && !(o is SessionOutcome.Stalled or SessionOutcome.TimedOut && (produced || _prevProduced));

        _prevStage = e.StageId;
        _prevOutcome = outcome;
        _prevProduced = produced;

        return repeat
            ? new WatchWake(WatchReason.CircuitBreaker,
                $"two consecutive {e.Outcome} sessions on stage {e.StageId} (#{e.Number - 1} and #{e.Number}) — attempts are burning on one failure",
                e.StageId, e.Seq)
            : null;
    }

    private WatchWake? ObservePhaseGate(GateFinished e)
    {
        if (!string.Equals(e.Scope, "phase", StringComparison.Ordinal)) return null;

        // A gap in the ordinal means a different battery even if nothing else intervened.
        if (!_inPhaseBurst || e.Seq != _lastPhaseGateSeq + 1) { _inPhaseBurst = true; _burstCountedRed = false; }
        _lastPhaseGateSeq = e.Seq;

        // Optional and skipped gates cannot make a battery RED — GateRunner.AllRequiredPassed is the
        // engine's rule and this is the same one.
        if (e.Passed || e.Skipped || e.Optional || _burstCountedRed) return null;

        _burstCountedRed = true;
        var stage = _stage ?? "?";
        var reds = _phaseReds.TryGetValue(stage, out var n) ? n + 1 : 1;
        _phaseReds[stage] = reds;

        return reds >= 2
            ? new WatchWake(WatchReason.PhaseRedTwice,
                $"phase gate for stage {stage} came back RED {reds} times (latest failure: {e.Name}) — the fix loop is not converging",
                stage, e.Seq)
            : null;
    }
}
