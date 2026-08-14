using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Data.Sqlite;

namespace Conductor.Core.Planning;

/// <summary>
/// W5.1: the ONE work snapshot the engine schedules on — declared rows with the work graph's status.
/// </summary>
/// <remarks>
/// W1 said it plainly: plans <i>declare</i> work, the graph <i>is</i> the work, the tracker and every
/// Face surface are generated views. The engine was the last reader still taking status from a
/// declared source. That worked only by accident of the markdown-table path, where the tracker is
/// regenerated from the graph after every session and so agrees with it a moment later.
/// <para>An inline (<c>plan-checkpoints</c>) plan — which is what every W4.1 import produces — has no
/// such write-back: its declared statuses are frozen at <c>TODO</c> for the life of the run. The
/// rehearsal caught exactly what that costs. The assignment policy kept re-picking a card the graph
/// had already recorded as delivered; the prompt's work section then rendered EMPTY (it reads the
/// graph, and a done card is history, not an instruction), so the agent had nothing to deliver, twice
/// in a row, which the circuit breaker correctly read as no progress and parked. Progress reported
/// 0/N throughout, and <c>AllEffectivelyDone</c> could never become true — so a plan imported from a
/// document could not reach <c>RunFinished</c> at all.</para>
/// <para>This is the same projection <c>GET /state</c> and <c>GET /tasks</c> already served (W1.4);
/// it moved here so the engine and the views cannot drift. The declared read still supplies the row
/// set before anything is seeded, and always supplies the handoff block, which is view-only prose the
/// graph does not model.</para>
/// </remarks>
public static class WorkSnapshot
{
    /// <summary>Declared rows overlaid with graph status. Falls back to the declared snapshot whole
    /// when there is no store, nothing is seeded yet, or the store read fails — a run whose graph is
    /// empty is a run at its very start, and the declaration is all there is.</summary>
    public static TrackerSnapshot Read(IRunStore? store, string runId, Func<TrackerSnapshot> readDeclared)
    {
        ArgumentNullException.ThrowIfNull(readDeclared);
        var declared = readDeclared();
        if (store is null) return declared;
        try
        {
            var rows = store.GetCheckpoints(runId);
            return rows.Count == 0 ? declared : Overlay(rows, declared);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return declared;
        }
    }

    /// <summary>KS3.4 round 5 — what the loop will be scheduling on, for a surface that holds NO open
    /// store: <c>preflight</c>'s compose leg and the dry-run loop (whose host deliberately registers
    /// no store, because a dry run must not write). Neither the declaration nor the graph at rest is
    /// that answer, because <c>RunLoop.RunAsync</c> runs <see cref="WorkGraphSync.Sync"/> BEFORE its
    /// first <c>ReadWork()</c> — the loop mutates its scheduling input and only then reads it. Rounds
    /// 1–4 of this checkpoint each removed a private copy of the decision or of an input; round 5
    /// removes the last one by modelling the mutation itself: the graph is folded from the same
    /// <c>run.db</c> the run would open (read-only, creating nothing —
    /// <see cref="SqliteRunStore.OpenReadOnly"/>), the declaration is read through the caller's own
    /// provider, and <see cref="WorkGraphSync.ProjectView"/> answers with the row set the synced
    /// graph will serve: declared rows carrying the graph's status where the graph knows the id,
    /// their declared status where it does not, retired rows out of view, zero-item-stage scaffolds
    /// in it.</summary>
    /// <param name="readDeclared">The DECLARED read, allowed to throw — an unreadable declaration is
    /// the one input on which the live sync deliberately does nothing, so this reader degrades the
    /// same way: to the graph as it lies (or, with nothing in it, to an empty snapshot, which is the
    /// loop's EmptyTracker park).</param>
    public static TrackerSnapshot ReadAtRest(PlanConfig plan, string runId, Func<TrackerSnapshot> readDeclared)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readDeclared);

        // The graph as the run would find it. No run yet (empty runId, no db file) folds nothing —
        // the projection over an empty graph IS the first sync's outcome; an unanswerable file
        // (locked mid-crash, an older schema) degrades the same way, and is stated as such.
        var graph = new TaskGraph();
        if (!string.IsNullOrEmpty(runId) && File.Exists(plan.RunDbPath))
        {
            try
            {
                using var store = SqliteRunStore.OpenReadOnly(plan.RunDbPath);
                graph.Fold(store.ReadAllEvents(runId));
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                           or IOException or UnauthorizedAccessException)
            {
                graph = new TaskGraph();
            }
        }

        TrackerSnapshot declared;
        try { declared = readDeclared(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The sync's own degradation, mirrored: WorkGraphSync.Sync SKIPS on an unreadable
            // declaration (no adds, no retirements, no scaffolds) and the loop schedules on the
            // graph as it stands.
            declared = new TrackerSnapshot();
            var atRest = WorkGraphSync.GraphRows(graph);
            return atRest.Count == 0 ? declared : Overlay(atRest, declared);
        }

        var rows = WorkGraphSync.ProjectView(plan, declared, graph);
        return rows.Count == 0 ? declared : Overlay(rows, declared);
    }

    /// <summary>Store-view rows rendered as the snapshot the scheduler eats. The graph's labels are
    /// canonical — no conventions round-trip, which is why the flags are set explicitly rather than
    /// re-derived from the status text. The handoff block is view-only prose the graph does not
    /// model, so it always rides in from the declared read.</summary>
    private static TrackerSnapshot Overlay(IReadOnlyList<Store.CheckpointRow> rows, TrackerSnapshot declared) => new()
    {
        Checkpoints =
        [
            .. rows.Select(r => new CheckpointRow(r.Id, r.Title, r.Status, r.Commit, r.Evidence)
            {
                StageId = r.StageId,
                IsDone = r.Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase),
                IsBlocked = r.Status.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase),
                IsInProgress = r.Status.StartsWith("IN", StringComparison.OrdinalIgnoreCase),
                IsSkipped = r.Status.StartsWith("SKIPPED", StringComparison.OrdinalIgnoreCase),
            }),
        ],
        HandoffBlock = declared.HandoffBlock,
        RawText = declared.RawText,
    };
}
