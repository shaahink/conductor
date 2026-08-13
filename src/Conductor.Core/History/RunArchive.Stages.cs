using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.History;

// KS1.2 — the stage rows, folded out of the event log the way Checkpoints() folds the task events.
// The mutable `stages` side table is still WRITTEN (InitializeStage / ConfirmStage, so an older
// engine browsing this database keeps its two honest columns) but nothing in this repo reads it any
// more: its session_count column has had no writer since v1, so it answered 0 for every run that
// ever held a session. KS1_2NoStagesSideTableReaderTests is the bar that keeps the next reader out.
public sealed partial class RunArchive
{
    /// <summary>
    /// The stages of one run, in the order the engine entered them — every field derived from the
    /// log (<see cref="StageEntered"/> / <see cref="StageConfirmed"/> / <see cref="SessionStarted"/>).
    /// <para>Status speaks the status surface's vocabulary (<c>SnapshotBuilder.StageState</c>, in its
    /// precedence order): <c>confirmed</c> when the log holds a <see cref="StageConfirmed"/>,
    /// <c>done</c> when every checkpoint of the stage folded to done without a confirmation,
    /// <c>active</c> for the last stage entered, <c>todo</c> otherwise. Two of the surface's words an
    /// archive cannot say, and does not: <c>skipped</c> lives only in transient run state (the log
    /// carries no skip event — see <c>StateProjectionParity</c>), and <c>gating</c> needs the live
    /// plan's gate policy, so a stage the surface would call gating settles here as <c>done</c>.</para>
    /// <para>A database with no event log at all — a pre-v5 import — lists no stages rather than
    /// throwing, and a torn event is skipped with the checkpoint fold's tolerance.</para>
    /// </summary>
    public IReadOnlyList<ArchivedStage> Stages(string runId)
    {
        var events = EventsOf(runId);
        if (events.Count == 0) return [];

        var order = new List<string>();
        var folds = new Dictionary<string, StageFold>(StringComparer.Ordinal);
        string? current = null;

        StageFold For(string id)
        {
            if (!folds.TryGetValue(id, out var fold))
            {
                fold = new StageFold();
                folds[id] = fold;
                order.Add(id);
            }
            return fold;
        }

        foreach (var evt in events)
        {
            switch (evt)
            {
                case StageEntered e:
                    var entered = For(e.StageId);
                    if (!string.IsNullOrWhiteSpace(e.Title)) entered.Title = e.Title;
                    // Re-entry restamps — the INSERT OR REPLACE semantics the side table lived by.
                    entered.StartedUtc = Stamp(evt);
                    current = e.StageId;
                    break;
                case StageConfirmed e:
                    For(e.StageId).ConfirmedUtc = Stamp(evt);
                    break;
                case SessionStarted e:
                    For(e.StageId).Sessions++;
                    break;
            }
        }

        var graph = new TaskGraph();
        graph.Fold(events);
        var cards = graph.Checkpoints()
            .Where(t => !string.Equals(t.Status, "archived", StringComparison.Ordinal)).ToList();

        var result = new List<ArchivedStage>(order.Count);
        foreach (var id in order)
        {
            var fold = folds[id];
            var mine = cards.Where(c => string.Equals(c.StageId, id, StringComparison.Ordinal)).ToList();
            var status =
                fold.ConfirmedUtc is not null ? "confirmed"
                : mine.Count > 0 && mine.All(c => string.Equals(c.Status, "done", StringComparison.Ordinal)) ? "done"
                : string.Equals(id, current, StringComparison.Ordinal) ? "active"
                : "todo";
            result.Add(new ArchivedStage(
                Id: id,
                // StageEntered.Title is nullable: a nameless stage reads as its id, never a blank cell.
                Title: fold.Title ?? id,
                Status: status,
                Sessions: fold.Sessions,
                StartedUtc: fold.StartedUtc,
                ConfirmedUtc: fold.ConfirmedUtc));
        }
        return result;
    }

    /// <summary>Every event of one run, oldest first — the one reader behind both folds. A database
    /// whose schema predates the event log (pre-v5) answers empty via the <see cref="Has"/> probe
    /// instead of throwing, and a torn payload is skipped, the same tolerance
    /// <c>SqliteRunStore.DeserializeEvents</c> keeps: one bad row must not take the history down.</summary>
    private List<ConductorEvent> EventsOf(string runId)
    {
        if (!Has("events", "payload")) return [];
        var rows = Query("SELECT payload FROM events WHERE run_id = @runId ORDER BY seq", ("@runId", runId));
        var events = new List<ConductorEvent>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                if (row["payload"] is not string json) continue;
                if (JsonSerializer.Deserialize<ConductorEvent>(json, PlanConfig.JsonOpts) is { } evt)
                    events.Add(evt);
            }
            catch (JsonException)
            {
                // A torn event is skipped, not fatal.
            }
        }
        return events;
    }

    private static string? Stamp(ConductorEvent evt)
        => evt.Ts == default ? null : evt.Ts.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Accumulator for one stage while the log folds.</summary>
    private sealed class StageFold
    {
        public string? Title { get; set; }
        public int Sessions { get; set; }
        public string? StartedUtc { get; set; }
        public string? ConfirmedUtc { get; set; }
    }
}
