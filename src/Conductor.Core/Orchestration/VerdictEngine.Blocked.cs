using Conductor.Core.Events;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Core.Orchestration;

/// <summary>
/// SC5.1 — "blocked until T" as a session outcome. Field notes 2026-07-29 (sk-platform #1): stage S4
/// burned $51.98, 23% of the whole run, on sessions whose entire content was re-reading a rate-limit
/// clock the FIRST session had already written down exactly — and then a human sat and resumed the
/// run one minute after the window opened. The engine had no way to express <em>wait</em>. It does now.
/// </summary>
public sealed partial class VerdictEngine
{
    /// <summary>The wait this session asked for, if any: the last <see cref="BlockedUntilRequested"/>
    /// emitted after its <see cref="SessionStarted"/> marker. Same session-scoping as the claim
    /// signal (<see cref="GraphClaimsDuringSession"/>) — a request from an earlier session is that
    /// session's, already honoured and already over.</summary>
    private BlockedUntilRequested? BlockedUntilDuringSession(SessionRecord rec)
    {
        if (_ctx.Store is not { } db) return null;
        db.FlushEvents();
        var events = db.ReadAllEvents(_ctx.State.RunId);
        var startSeq = events.OfType<SessionStarted>().FirstOrDefault(s => s.Number == rec.Number)?.Seq;
        if (startSeq is null) return null;
        return events.OfType<BlockedUntilRequested>().LastOrDefault(e => e.Seq > startSeq.Value);
    }

    /// <summary>Accept the wait: park the run loop on it, burn no attempt, queue no fix. Returns false
    /// when the request is not (or no longer) honourable, in which case the caller runs the ordinary
    /// verdict — a wait that has already expired is not a reason to skip judging the session.</summary>
    private bool HonourBlockedUntil(SessionRecord rec, StageConfig stage, BlockedUntilRequested req, string startHead)
    {
        var now = DateTimeOffset.UtcNow;
        CollectCommits(rec, startHead);

        // The session may have taken longer to exit than the window took to open. Sleeping on a past
        // instant would spend the boundary for nothing, so fall through and judge the session.
        if (req.UntilUtc <= now)
        {
            _ctx.Log($"session #{rec.Number} asked to wait until {req.UntilUtc:HH:mm:ss}Z — that window is already open, judging the session normally instead of sleeping");
            return false;
        }

        rec.Outcome = SessionOutcome.BlockedUntil;

        // Nothing is invisible: a session that claimed work AND blocked keeps its claims on the record
        // and in the graph. They are judged by the battery that follows the session which wakes up.
        rec.NewlyDone = GraphClaimsDuringSession(rec) ?? [];
        if (rec.NewlyDone.Count > 0)
            _ctx.Log($"note: session #{rec.Number} claimed [{string.Join(", ", rec.NewlyDone)}] before blocking — those claims stand and are verified after the wait", "warn");

        // The guard that keeps this from becoming its own money pit: each block costs one session, so
        // an estimate that never converges must stop being slept on and start being a human's problem.
        var consecutive = 1;
        for (var i = _ctx.State.History.Count - 2; i >= 0; i--)
        {
            if (_ctx.State.History[i].Outcome != SessionOutcome.BlockedUntil) break;
            consecutive++;
        }
        if (consecutive >= BlockedUntilRequest.MaxConsecutiveBlocks)
        {
            NeedsHuman($"{consecutive} consecutive sessions ended blocked without making progress — the unblock estimate is not converging. Latest: {BlockedUntilRequest.Describe(req.UntilUtc, req.Reason, now)}");
            return true;
        }

        _ctx.State.BlockedUntilUtc = req.UntilUtc.UtcDateTime;
        _ctx.State.BlockedReason = req.Reason;
        _ctx.State.BlockedSinceUtc = now.UtcDateTime;
        _ctx.State.Status = RunStatus.Waiting;

        // Deliberately NOT touched: AttemptsThisStage (the promise this checkpoint makes) and
        // PendingFix/PendingVerify/PendingResume (a fix that was already queued is still owed after
        // the wait — the block defers work, it does not cancel it).
        _ctx.Log($"session #{rec.Number} BlockedUntil — {BlockedUntilRequest.Describe(req.UntilUtc, req.Reason, now)}; sleeping at the session boundary, no attempt burned (attempts stay {_ctx.State.AttemptsThisStage}/{MaxAttempts(stage)})",
            "blockeduntil");
        Notify($"Conductor {_ctx.Plan.Name}: {BlockedUntilRequest.Describe(req.UntilUtc, req.Reason, now)}");
        _saveAndReport();
        return true;
    }

    /// <summary>The park event, emitted by the run loop AFTER the session's
    /// <see cref="SessionFinished"/> so it is the last thing in the log — which is what makes
    /// <c>conductor status</c> answer "waiting until T" rather than "idle, last session finished".</summary>
    public void EmitBlockedUntilPark(SessionRecord rec)
    {
        if (_ctx.State.BlockedUntilUtc is not { } until) return;
        _ctx.Events.Emit(new RunBlockedUntil
        {
            UntilUtc = new DateTimeOffset(until, TimeSpan.Zero),
            Reason = _ctx.State.BlockedReason ?? "",
            StageId = rec.Stage,
            FromSession = rec.Number,
        });
    }
}
