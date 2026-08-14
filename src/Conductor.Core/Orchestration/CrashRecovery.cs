using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS3.4 round 4 — the startup recovery the loop applies BEFORE its first decision, stated once.
/// <para><see cref="RunLoop"/> has always continued an aborted run and turned a crash's persisted
/// <c>Running</c>/<c>VerifyingGates</c>/<c>Backoff</c> into a queued resume at startup — which means
/// the state <see cref="StageSelection.NextAction"/> decides on is not the state on disk, it is the
/// state after this function. <c>preflight</c>'s compose leg read the raw saved state, so a
/// hard-killed engine — precisely the run an operator preflights before relaunching — drilled as a
/// Deliver session while the loop would compose a Resume: wrong kind, wrong prompt, wrong measured
/// argv. The transitions knowable from the saved state alone live here, pure, and the loop and the
/// drill both apply them; <see cref="RunLoop"/> keeps only the side effects (logging, saving) and the
/// store-backed orphan recovery, which needs a live store.</para>
/// <para>Read-nothing by construction: this type touches only the <see cref="RunState"/> it is
/// handed. The drill applies it to a peeked copy that is never written back.</para>
/// </summary>
public static class CrashRecovery
{
    /// <summary>The reason a crash-queued resume carries — a constant because it is rendered into
    /// the resume prompt, so both surfaces must spell it identically or their measured prompts
    /// differ by exactly these words.</summary>
    public const string CrashReason = "conductor crashed or was killed mid-session";

    /// <summary>The reason a resume queued off the event log carries — the run.db knew about a
    /// session that state.json's history had already recorded. One constant, rendered into the
    /// resume prompt by both the loop and the drill.</summary>
    public const string OrphanReason = "event log shows interrupted session — recovering";

    /// <summary>As <see cref="OrphanReason"/>, for the orphan state.json never heard of at all —
    /// the resume is rebuilt from the store's own <c>SessionStarted</c> row.</summary>
    public const string OrphanFromLogReason = "event log shows interrupted session — recovering from orphaned SessionStarted";

    /// <summary>What <see cref="Apply"/> did, so a caller with side effects (the loop logs and
    /// saves; the drill annotates its leg) can narrate without re-deriving the decision.</summary>
    /// <param name="ContinuedAborted">The saved <see cref="RunStatus.Aborted"/> was lifted —
    /// `conductor run` on an aborted run means "continue".</param>
    /// <param name="LiftedCrashStatus">The saved status was one of the crash trio
    /// (Running/VerifyingGates/Backoff) and is Idle now.</param>
    /// <param name="Interrupted">The unfinished session that was closed and queued for resume, when
    /// there was one.</param>
    public sealed record Outcome(bool ContinuedAborted, bool LiftedCrashStatus, SessionRecord? Interrupted);

    /// <summary>The ONE construction of a pending resume for a session record.
    /// <see cref="VerdictEngine.QueueResume"/> and <see cref="Apply"/> both build through here,
    /// because two constructions of the same resume is how the drill measures a different prompt
    /// than the loop composes.</summary>
    public static PendingResume ResumeFor(SessionRecord rec, string reason, bool countResume = true)
    {
        ArgumentNullException.ThrowIfNull(rec);
        return new PendingResume
        {
            FromSession = rec.Number,
            ClaudeSessionId = rec.ClaudeSessionId,
            Reason = reason,
            ResumeCount = rec.ResumeCount + (countResume ? 1 : 0),
        };
    }

    /// <summary>Applies the state-only startup recovery, exactly as the top of
    /// <see cref="RunLoop.RunAsync"/> has always executed it:
    /// <list type="bullet">
    /// <item>Aborted → Idle. An aborted run is stopped, not discarded; running it again continues it.</item>
    /// <item>Running/VerifyingGates/Backoff → Idle, and an unfinished last history record is closed
    /// as Interrupted with a resume queued for its agent session.</item>
    /// </list>
    /// The parked trio (Paused/NeedsHuman/AwaitingOwner) is deliberately untouched — the loop idles
    /// on those, which is <see cref="LaunchStep.ParkedStatus"/>'s branch, and only
    /// <c>conductor resume</c> lifts them.</summary>
    public static Outcome Apply(RunState state, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var continuedAborted = false;
        var lifted = false;
        SessionRecord? interrupted = null;

        if (state.Status == RunStatus.Aborted)
        {
            state.Status = RunStatus.Idle;
            continuedAborted = true;
        }

        if (state.Status is RunStatus.Running or RunStatus.VerifyingGates or RunStatus.Backoff)
        {
            lifted = true;
            var last = state.History.LastOrDefault();
            if (last is { EndedUtc: null })
            {
                last.EndedUtc = nowUtc ?? DateTime.UtcNow;
                last.Outcome = SessionOutcome.Interrupted;
                state.PendingResume = ResumeFor(last, CrashReason);
                interrupted = last;
            }
            state.Status = RunStatus.Idle;
        }

        return new Outcome(continuedAborted, lifted, interrupted);
    }

    /// <summary>What <see cref="ApplyOrphan"/> did. <paramref name="Resumed"/> is the session record
    /// a resume was queued for (from the state's history, or rebuilt from the store's own row);
    /// <paramref name="ParkedOrphanNumber"/> is the session number whose orphaned start row carries
    /// no agent session id — unresumable, so the run parks at NeedsHuman before spawning.</summary>
    public sealed record OrphanOutcome(SessionRecord? Resumed, int? ParkedOrphanNumber)
    {
        public static readonly OrphanOutcome Nothing = new(null, null);
    }

    /// <summary>The SECOND half of startup recovery — the one <see cref="Apply"/> cannot see because
    /// its evidence lives in the store, not in <c>state.json</c>: a <c>SessionStarted</c> row in the
    /// event log with no matching <c>SessionFinished</c>. <see cref="RunLoop"/> has always asked
    /// this question when the state-only half recovered nothing, and either queued a resume off the
    /// orphan row or parked the run at NeedsHuman when the row carries no agent session id. KS3.4
    /// round 5 moved the transitions here so <c>preflight</c>'s compose leg — which already opens
    /// the same <c>run.db</c> read-only — applies them to its peeked copy instead of naming a
    /// Deliver session for a launch that queues a Resume or parks; the loop keeps only the side
    /// effects (logging, saving, its decomposed-checkpoint replay).
    /// <para>Callers guard as the loop always has: only when <see cref="Apply"/> found no
    /// interrupted session and no resume is already pending. The store is only ever read.</para></summary>
    public static OrphanOutcome ApplyOrphan(RunState state, IRunStore store)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(store);

        var interrupted = store.FindInterruptedSession(state.RunId);
        if (interrupted == null) return OrphanOutcome.Nothing;

        var rec = state.History.FirstOrDefault(h => h.Number == interrupted.Number);
        if (rec != null)
        {
            if (rec.EndedUtc == null) rec.EndedUtc = DateTime.UtcNow;
            rec.Outcome = SessionOutcome.Interrupted;
            state.PendingResume = ResumeFor(rec, OrphanReason);
            // The loop has never lifted a standing NeedsHuman here — the park outranks the resume.
            if (state.Status != RunStatus.NeedsHuman) state.Status = RunStatus.Idle;
            return new OrphanOutcome(rec, null);
        }

        if (string.IsNullOrEmpty(interrupted.AgentSessionId))
        {
            state.Status = RunStatus.NeedsHuman;
            state.SetAttention($"Orphaned session #{interrupted.Number} in run.db has no AgentSessionId — manual review needed.");
            return new OrphanOutcome(null, interrupted.Number);
        }

        rec = new SessionRecord
        {
            Number = interrupted.Number,
            Stage = interrupted.StageId,
            Kind = SessionKind.Deliver,
            Attempt = 1,
            StartedUtc = DateTime.UtcNow,
            ClaudeSessionId = interrupted.AgentSessionId,
            Outcome = SessionOutcome.Interrupted,
        };
        state.History.Add(rec);
        state.PendingResume = ResumeFor(rec, OrphanFromLogReason);
        if (state.Status != RunStatus.NeedsHuman) state.Status = RunStatus.Idle;
        return new OrphanOutcome(rec, null);
    }
}
