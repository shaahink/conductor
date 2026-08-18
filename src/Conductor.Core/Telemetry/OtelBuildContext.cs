using System.Globalization;
using Conductor.Core.Events;

namespace Conductor.Core.Telemetry;

/// <summary>
/// KS7.3 — the parent lookup for <see cref="OtelTrace"/>: given an event, which stage and which session
/// was open when it happened.
/// </summary>
/// <remarks>
/// A flat event log has no nesting, so the tree a trace view draws has to be recovered. Two rules, and
/// both matter for how the result reads:
/// <list type="bullet">
/// <item>A stage is found by SEQUENCE, not by id. StageEntered for the same stage happens again on every
/// resume, so "the KS7 span" is ambiguous; "the stage entry most recently before this event" is not.</item>
/// <item>A session is found by its <see cref="ConductorEvent.SessionId"/> when the event carries one.
/// Gates run after their session finishes and are stamped with it, which is what puts a gate under the
/// session that caused it rather than loose under the stage.</item>
/// </list>
/// </remarks>
internal sealed class OtelBuildContext(string runId, string trace, List<ConductorEvent> log)
{
    private readonly List<long> _stageSeqs = log.OfType<StageEntered>().Select(e => e.Seq).ToList();

    private readonly Dictionary<string, List<long>> _sessionSeqs = log.OfType<SessionStarted>()
        .GroupBy(s => s.Number.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(s => s.Seq).ToList(), StringComparer.Ordinal);

    public string RunId { get; } = runId;

    public string Trace { get; } = trace;

    public List<ConductorEvent> Log { get; } = log;

    public string RootSpanId { get; } = OtelIds.Span(runId, "run");

    public string StageSpanId(long stageEnteredSeq) =>
        OtelIds.Span(RunId, "stage/" + stageEnteredSeq.ToString(CultureInfo.InvariantCulture));

    public string SessionSpanId(long sessionStartedSeq) =>
        OtelIds.Span(RunId, "session/" + sessionStartedSeq.ToString(CultureInfo.InvariantCulture));

    /// <summary>The span of the stage that was open at <paramref name="seq"/>, or the run root when the
    /// event predates any stage entry (run-level events do).</summary>
    public string StageSpanIdAt(long seq)
    {
        var open = LastBefore(_stageSeqs, seq);
        return open is null ? RootSpanId : StageSpanId(open.Value);
    }

    /// <summary>The span of the session this event belongs to, or null when it names none — a gate run
    /// outside a session, or an event from a build that did not stamp SessionId.</summary>
    public string? SessionSpanIdAt(ConductorEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.SessionId is not { Length: > 0 } sid) return null;
        if (!_sessionSeqs.TryGetValue(sid, out var starts)) return null;

        // An attempt that was retried has several starts under one number; the event belongs to the one
        // it followed. Falling back to the FIRST start rather than to null keeps a late-stamped event
        // (a gate finishing after the session's own SessionFinished) attached to work it really did.
        var open = LastBefore(starts, e.Seq) ?? starts[0];
        return SessionSpanId(open);
    }

    private static long? LastBefore(List<long> ordered, long seq)
    {
        long? found = null;
        foreach (var s in ordered)
        {
            if (s > seq) break;
            found = s;
        }

        return found;
    }
}
