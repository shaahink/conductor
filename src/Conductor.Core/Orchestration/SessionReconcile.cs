using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// Bug #39 — a session record the engine never got to close.
///
/// <para>A session's <see cref="SessionRecord"/> is appended to <see cref="RunState.History"/> when
/// the session starts and its outcome is written when it ends. An engine killed mid-session — with
/// its parent shell, by the OS, by a power cut — leaves the record with no outcome and no end. The
/// run resumes cleanly as the next session, so the record is orphaned, not active: it reads as a live
/// session that has been dead for hours, its cost reads $0.00, and no verb could close it because
/// <c>conductor run close</c> only knew about RUN rows.</para>
///
/// <para>Two readings and one write, all the same rule. The engine closes dangling records the moment
/// it resumes (it is a new process, so anything still open was left by a dead one); the status
/// surfaces print an open record as <see cref="Orphaned"/> unless it is the latest one and an engine
/// holds the store; and <c>conductor run close</c> closes them alongside the run row.</para>
/// </summary>
public static class SessionReconcile
{
    /// <summary>The word for an open session record nothing is driving.</summary>
    public const string Orphaned = "orphaned";

    /// <summary>Is this record still waiting for its engine to write an outcome?</summary>
    public static bool IsDangling(SessionRecord rec)
    {
        ArgumentNullException.ThrowIfNull(rec);
        return rec.Outcome is null && rec.EndedUtc is null;
    }

    /// <summary>Close every dangling record as <see cref="SessionOutcome.Interrupted"/>, ended at the
    /// last moment the record itself vouches for. Returns the numbers closed, so the caller can say
    /// what it did rather than that it ran.</summary>
    public static IReadOnlyList<int> CloseDangling(RunState state, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var closed = new List<int>();
        foreach (var rec in state.History)
        {
            if (!IsDangling(rec)) continue;
            rec.Outcome = SessionOutcome.Interrupted;
            rec.EndedUtc = nowUtc;
            closed.Add(rec.Number);
        }
        return closed;
    }

    /// <summary>The outcome word a status surface prints for a record. A closed record is repeated
    /// exactly; an open one is <c>running</c> only while it is the run's latest session AND an engine
    /// is holding the store — otherwise nothing can be running it, and the honest word is
    /// <see cref="Orphaned"/>.</summary>
    public static string OutcomeWord(SessionRecord rec, bool isLatest, bool engineLive)
    {
        ArgumentNullException.ThrowIfNull(rec);
        if (rec.Outcome is { } outcome) return outcome.ToString();
        return isLatest && engineLive ? "running" : Orphaned;
    }
}
