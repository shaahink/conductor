using System.Globalization;

namespace Conductor.Core.Events;

/// <summary>
/// B5.2 — replay / time-travel over the append-only event log. Where <see cref="Timeline"/> answers
/// "what transitions happened, and how long did each take", <see cref="Replay"/> answers "what did the
/// run <em>look like</em> at each point" — it reconstructs, for every transition, the cumulative run
/// state as of that moment (current stage, sessions started/finished, gates passed/failed, checkpoints
/// confirmed, cost, tokens). Scrolling back through the steps is a rewind: each step shows only what
/// the run knew up to and including that event — a later confirmation never leaks into an earlier
/// frame. This is the "time-travel" viewer behind <c>conductor replay</c> and the TUI <c>F8</c> modal.
/// </summary>
/// <remarks>
/// Pure fold over the single event log (B5 trap: never a parallel bookkeeping store that can drift).
/// The transition text is produced by the already-tested <see cref="Timeline.Build"/>, so replay and
/// the REPORT.md timeline render identical lines from one source; replay only layers the reconstructed
/// as-of state on top. Cumulative cost/tokens accrue from <see cref="SessionFinished"/> — the same
/// source <see cref="Conductor.Models.RunState.TotalCostUsd"/> sums — so the final step's totals equal
/// the folded <see cref="RunStateProjection"/>'s (no drift). <see cref="TokenDelta"/> is not a
/// transition, so it produces no step (consistent with the timeline).
/// </remarks>
public static class Replay
{
    /// <summary>The reconstructed run state as of a single point in the log — the "world" a
    /// time-traveller sees when parked on that step. All counters are cumulative up to and including
    /// the step's event; nothing from a later event is visible.</summary>
    public sealed record ReplayState(
        string? Stage,
        int SessionsStarted,
        int SessionsFinished,
        int GatesPassed,
        int GatesFailed,
        int CheckpointsConfirmed,
        int StagesConfirmed,
        decimal CostUsd,
        long TokensInput,
        long TokensOutput);

    /// <summary>One replay frame: a timeline transition paired with the run state as of that moment.</summary>
    public sealed record ReplayStep(Timeline.TimelineEntry Entry, ReplayState StateAsOf);

    /// <summary>The empty world before any event is applied.</summary>
    private static readonly ReplayState Zero = new(null, 0, 0, 0, 0, 0, 0, 0m, 0, 0);

    /// <summary>Fold the event stream into ordered replay steps. Each transition (the same set
    /// <see cref="Timeline"/> renders) carries the cumulative state as of that point in the log.</summary>
    public static IReadOnlyList<ReplayStep> Build(IEnumerable<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.OrderBy(e => e.Seq).ToList();
        // Reuse the tested transition projection so replay and the report timeline share one renderer.
        var entriesBySeq = Timeline.Build(ordered).ToDictionary(e => e.Seq);

        var steps = new List<ReplayStep>(entriesBySeq.Count);
        var acc = Zero;

        foreach (var e in ordered)
        {
            // Apply the event to the running world first, so the step captures the state *after* the
            // transition it represents (e.g. the checkpoint-confirmed step already counts that one).
            acc = Apply(acc, e);
            if (entriesBySeq.TryGetValue(e.Seq, out var entry))
                steps.Add(new ReplayStep(entry, acc));
        }

        return steps;
    }

    private static ReplayState Apply(ReplayState s, ConductorEvent e) => e switch
    {
        StageEntered x => s with { Stage = x.StageId },
        SessionStarted => s with { SessionsStarted = s.SessionsStarted + 1 },
        SessionFinished x => s with
        {
            SessionsFinished = s.SessionsFinished + 1,
            CostUsd = s.CostUsd + (x.CostUsd ?? 0m),
            TokensInput = s.TokensInput + (x.TokensInput ?? 0),
            TokensOutput = s.TokensOutput + (x.TokensOutput ?? 0),
        },
        GateFinished x when !x.Skipped => x.Passed
            ? s with { GatesPassed = s.GatesPassed + 1 }
            : s with { GatesFailed = s.GatesFailed + 1 },
        CheckpointConfirmed => s with { CheckpointsConfirmed = s.CheckpointsConfirmed + 1 },
        StageConfirmed => s with { StagesConfirmed = s.StagesConfirmed + 1 },
        _ => s, // RunStarted/RunFinished/Attention/Owner/TokenDelta carry no cumulative counter here
    };

    /// <summary>Render one replay step as two lines: the shared timeline transition, then an indented
    /// "as-of" state strip. Used by both the CLI viewer and the TUI modal so the rewind reads the same
    /// everywhere.</summary>
    public static IEnumerable<string> FormatStep(ReplayStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        yield return Timeline.Format(step.Entry);
        yield return "     ↳ " + FormatState(step.StateAsOf);
    }

    /// <summary>The compact "world as of this step" strip: stage, session progress, gate tallies,
    /// confirmed checkpoints, and accrued cost/tokens up to this point.</summary>
    public static string FormatState(ReplayState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var cost = s.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture);
        var tokens = s.TokensInput + s.TokensOutput > 0
            ? $" · {s.TokensInput.ToString("n0", CultureInfo.InvariantCulture)}/{s.TokensOutput.ToString("n0", CultureInfo.InvariantCulture)} tok"
            : "";
        return $"stage {s.Stage ?? "-"} · sessions {s.SessionsFinished}/{s.SessionsStarted} · " +
               $"gates {s.GatesPassed}✓ {s.GatesFailed}✗ · {s.CheckpointsConfirmed} cp · ${cost}{tokens}";
    }
}
