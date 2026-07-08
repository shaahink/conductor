namespace Conductor.Core.Events;

/// <summary>
/// B5.1 — the Timeline projection. Folds the append-only event log into an ordered list of state
/// transitions, each stamped with its wall-clock time and (for spans) a computed duration —
/// "Jaeger for AI agents". A session's duration is the elapsed time between its
/// <see cref="SessionStarted"/> and <see cref="SessionFinished"/>; a stage's between
/// <see cref="StageEntered"/> and <see cref="StageConfirmed"/>; a gate carries the
/// <see cref="GateFinished.DurationMs"/> the engine already measured; the run's is measured from the
/// first <see cref="RunStarted"/> to <see cref="RunFinished"/>. Point events (checkpoint confirmed,
/// stage entered, needs-human, owner-approval) carry no duration.
/// </summary>
/// <remarks>
/// Pure fold — depends only on the events, never on disk or the wall-clock — so it is deterministic
/// and unit-testable. Per the B5 trap it is a fold over the single event log, never a parallel
/// bookkeeping store that can drift (BATON-BRIEF §3.2). <see cref="TokenDelta"/> is deliberately
/// excluded: token/cost accrual is the <see cref="LiveMetrics"/> projection's concern, not a state
/// transition. Both the REPORT.md section and the TUI timeline modal render via <see cref="Format"/>,
/// so the human-readable line has one source of truth.
/// </remarks>
public static class Timeline
{
    /// <summary>The transition category, used for the row glyph and for filtering.</summary>
    public enum EntryKind { Run, Stage, Session, Gate, Checkpoint, Attention, Owner }

    /// <summary>One transition on the timeline. <paramref name="Duration"/> is set only for spans
    /// (session/stage/gate/run); point events leave it null.</summary>
    public sealed record TimelineEntry(
        long Seq,
        DateTimeOffset Ts,
        EntryKind Kind,
        string Label,
        TimeSpan? Duration);

    /// <summary>Fold the event stream into an ordered timeline. Deltas are ignored; every other
    /// meaningful transition becomes one entry, ordered by <see cref="ConductorEvent.Seq"/>.</summary>
    public static IReadOnlyList<TimelineEntry> Build(IEnumerable<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.OrderBy(e => e.Seq).ToList();
        var entries = new List<TimelineEntry>(ordered.Count);

        // Opening timestamps, so a closing event can compute its span's duration by matching back to
        // its start (session by number, stage by id, run by the earliest RunStarted).
        var sessionStart = new Dictionary<int, DateTimeOffset>();
        var stageStart = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        DateTimeOffset? runStart = null;

        foreach (var e in ordered)
        {
            switch (e)
            {
                case RunStarted r:
                    runStart ??= r.Ts;
                    entries.Add(new TimelineEntry(r.Seq, r.Ts, EntryKind.Run,
                        (r.Resumed ? "run resumed · " : "run started · ") + r.Plan, null));
                    break;

                case StageEntered s:
                    stageStart[s.StageId] = s.Ts;
                    entries.Add(new TimelineEntry(s.Seq, s.Ts, EntryKind.Stage,
                        $"stage {s.StageId} entered" + (string.IsNullOrEmpty(s.Title) ? "" : $" — {s.Title}"), null));
                    break;

                case SessionStarted s:
                    sessionStart[s.Number] = s.Ts;
                    entries.Add(new TimelineEntry(s.Seq, s.Ts, EntryKind.Session,
                        $"session #{s.Number} {s.StageId} {s.Kind} started (attempt {s.Attempt}/{s.MaxAttempts})" +
                        (s.Persona != null ? $" · persona {s.Persona}" : ""), null));
                    break;

                case SessionFinished s:
                {
                    var dur = sessionStart.TryGetValue(s.Number, out var st) ? s.Ts - st : (TimeSpan?)null;
                    var done = s.NewlyDone is { Count: > 0 } ? " · done " + string.Join(",", s.NewlyDone) : "";
                    var commits = s.NewCommits is { Count: > 0 } ? $" · {s.NewCommits.Count} commit(s)" : "";
                    entries.Add(new TimelineEntry(s.Seq, s.Ts, EntryKind.Session,
                        $"session #{s.Number} {s.StageId} → {s.Outcome}{done}{commits}", dur));
                    break;
                }

                case GateFinished g:
                    entries.Add(new TimelineEntry(g.Seq, g.Ts, EntryKind.Gate,
                        $"gate {g.Name} {(g.Skipped ? "skip" : g.Passed ? "pass" : "FAIL")}" +
                        (string.IsNullOrEmpty(g.Scope) ? "" : $" [{g.Scope}]"),
                        TimeSpan.FromMilliseconds(g.DurationMs)));
                    break;

                case CheckpointConfirmed c:
                    entries.Add(new TimelineEntry(c.Seq, c.Ts, EntryKind.Checkpoint,
                        $"checkpoint {c.CheckpointId} confirmed", null));
                    break;

                case StageConfirmed s:
                {
                    var dur = stageStart.TryGetValue(s.StageId, out var st) ? s.Ts - st : (TimeSpan?)null;
                    entries.Add(new TimelineEntry(s.Seq, s.Ts, EntryKind.Stage,
                        $"stage {s.StageId} confirmed{(s.Audited ? " (audited)" : "")}", dur));
                    break;
                }

                case AttentionRequested a:
                    entries.Add(new TimelineEntry(a.Seq, a.Ts, EntryKind.Attention, $"needs human — {a.Reason}", null));
                    break;

                case OwnerApprovalRequested o:
                    entries.Add(new TimelineEntry(o.Seq, o.Ts, EntryKind.Owner, $"owner approval requested — {o.StageId}", null));
                    break;

                case OwnerApprovalGranted o:
                    entries.Add(new TimelineEntry(o.Seq, o.Ts, EntryKind.Owner, $"owner approval granted — {o.StageId}", null));
                    break;

                case RunFinished r:
                {
                    var dur = runStart is { } rs ? r.Ts - rs : (TimeSpan?)null;
                    entries.Add(new TimelineEntry(r.Seq, r.Ts, EntryKind.Run,
                        $"run finished — {r.Status} · {r.CheckpointsDone}/{r.CheckpointsTotal} checkpoints", dur));
                    break;
                }

                // TokenDelta intentionally omitted — LiveMetrics owns token/cost accrual, not the
                // transition timeline (keeps the timeline a pure list of state transitions).
            }
        }

        return entries;
    }

    /// <summary>One human-readable timeline line, shared by the REPORT.md section and the TUI modal so
    /// both render identical text. Time is shown in the operator's local zone; a span appends its
    /// elapsed duration.</summary>
    public static string Format(TimelineEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var ts = e.Ts.ToLocalTime().ToString("MM-dd HH:mm:ss");
        var dur = e.Duration is { } d ? $"  ({FormatDuration(d)})" : "";
        return $"{ts}  {Glyph(e.Kind)} {e.Label}{dur}";
    }

    /// <summary>Compact H/M/S duration formatting shared across the timeline renderers.</summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s"
        : $"{t.TotalSeconds:0.0}s";

    private static string Glyph(EntryKind k) => k switch
    {
        EntryKind.Run => "◆",
        EntryKind.Stage => "▸",
        EntryKind.Session => "•",
        EntryKind.Gate => "▪",
        EntryKind.Checkpoint => "✓",
        EntryKind.Attention => "■",
        EntryKind.Owner => "§",
        _ => "·",
    };
}
