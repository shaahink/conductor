using Conductor.Core.Store;
using Conductor.Models;

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
            if (rows.Count == 0) return declared;
            return new TrackerSnapshot
            {
                Checkpoints =
                [
                    .. rows.Select(r => new CheckpointRow(r.Id, r.Title, r.Status, r.Commit, r.Evidence)
                    {
                        StageId = r.StageId,
                        // The graph's labels are canonical — no conventions round-trip, which is why
                        // these flags are set explicitly rather than re-derived from the status text.
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return declared;
        }
    }
}
