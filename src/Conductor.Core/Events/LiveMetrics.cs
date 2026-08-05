namespace Conductor.Core.Events;

/// <summary>
/// B2.6 — folds <see cref="TokenDelta"/> events into per-session live token/cost totals (fixes F-3
/// live-token lag). Consumed by the dashboard (already wired via <c>agent.Tokens*</c>), the snap-shot
/// report, and — once the event log is authoritative — timeline/replay viewers (B5).
/// </summary>
/// <remarks>
/// Pure projection: it depends only on the events, never on disk or wall-clock. A session that has
/// not yet emitted a <see cref="SessionFinished"/> event is "live" and its deltas count toward the
/// live totals the dashboard renders.
/// </remarks>
public static class LiveMetrics
{
    /// <summary>Token/cost totals folded from all <see cref="TokenDelta"/> events within a session.</summary>
    public sealed record SessionTokenTotals(
        long Input,
        long Output,
        long Reasoning,
        long CacheRead,
        decimal CostUsd)
    {
        public long Total => Input + Output + Reasoning;
    }

    /// <summary>Fold <see cref="TokenDelta"/> events for a given session number into live totals.</summary>
    /// <param name="events">The ordered (by <c>Seq</c>) event stream — typically the full log.</param>
    /// <param name="sessionNumber">The session to fold (1-based). Deltas whose <see cref="ConductorEvent.SessionId"/>
    /// doesn't parse to this number are ignored.</param>
    public static SessionTokenTotals ForSession(IEnumerable<ConductorEvent> events, int sessionNumber)
    {
        ArgumentNullException.ThrowIfNull(events);

        var sid = sessionNumber.ToString();
        long input = 0, output = 0, reasoning = 0, cacheRead = 0;
        decimal cost = 0;

        foreach (var evt in events)
        {
            if (evt is TokenDelta td && td.SessionId == sid)
            {
                input += td.Input;
                output += td.Output;
                reasoning += td.Reasoning;
                cacheRead += td.CacheRead;
                cost += td.CostUsd;
            }
        }

        return new SessionTokenTotals(input, output, reasoning, cacheRead, cost);
    }

    /// <summary>
    /// K4.1 — the per-turn context profile of a session, recovered from its persisted
    /// <see cref="TokenDelta"/> events.
    /// </summary>
    /// <remarks>
    /// Each delta is one deduplicated API call, and its <see cref="TokenDelta.Input"/> already carries
    /// cache-creation, so <c>Input + CacheRead</c> is the prompt that call re-sent. Folding the log
    /// rather than asking the session means every run recorded before this checkpoint existed still
    /// yields its context profile — this repo's own history is 4,800 of these events — and that a live
    /// session's window can be read without reaching into the provider.
    /// </remarks>
    public static ContextWindowStats ContextForSession(IEnumerable<ConductorEvent> events, int sessionNumber)
    {
        ArgumentNullException.ThrowIfNull(events);

        var sid = sessionNumber.ToString();
        var meter = new ContextWindowMeter();
        foreach (var evt in events)
            if (evt is TokenDelta td && td.SessionId == sid)
                meter.Observe(td.Input + td.CacheRead);

        return meter.Snapshot();
    }

    /// <summary>Fold token deltas across ALL sessions in the stream (run-wide total).</summary>
    public static SessionTokenTotals RunWide(IEnumerable<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        long input = 0, output = 0, reasoning = 0, cacheRead = 0;
        decimal cost = 0;

        foreach (var evt in events)
        {
            if (evt is TokenDelta td)
            {
                input += td.Input;
                output += td.Output;
                reasoning += td.Reasoning;
                cacheRead += td.CacheRead;
                cost += td.CostUsd;
            }
        }

        return new SessionTokenTotals(input, output, reasoning, cacheRead, cost);
    }
}
