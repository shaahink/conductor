using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>W1.3 — the claim signal. Done-ness comes from the WORK GRAPH (what
/// `conductor task --done` / MCP task_update wrote during the session), not from diffing the
/// tracker markdown, which is a generated view. These helpers answer the two questions the
/// verdict asks: "what was claimed during THIS session?" and "does the graph say the stage is
/// complete?".</summary>
public sealed partial class VerdictEngine
{
    /// <summary>Checkpoint ids whose graph status became done between this session's start
    /// (its <see cref="SessionStarted"/> seq) and now — the one claim path (`conductor task
    /// --done`, MCP task_update, and the folded MCP journal all emit into it). null = no store,
    /// or the session-start marker is missing — callers fall back to the legacy tracker diff
    /// wholesale.</summary>
    private List<string>? GraphClaimsDuringSession(SessionRecord rec)
    {
        if (_ctx.Store is not { } db) return null;
        db.FlushEvents();
        var events = db.ReadAllEvents(_ctx.State.RunId);
        var startSeq = events.OfType<SessionStarted>().FirstOrDefault(s => s.Number == rec.Number)?.Seq;
        if (startSeq is null) return null;

        var pre = new TaskGraph();
        pre.Fold(events.Where(e => e.Seq <= startSeq.Value));
        var post = new TaskGraph();
        post.Fold(events);

        var preDone = pre.Checkpoints().Where(t => t.Status == "done").Select(t => t.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return post.Checkpoints()
            .Where(t => t.Status == "done" && !preDone.Contains(t.TaskId))
            .Select(t => t.TaskId).ToList();
    }

    /// <summary>All of a stage's checkpoint rows in the graph read DONE (and it has some) — so a
    /// stage whose last item was claimed only via the graph is complete NOW, not one tracker
    /// regeneration later.</summary>
    private bool GraphStageDone(string stageId)
    {
        if (_ctx.Store is not { } db) return false;
        var rows = db.GetCheckpoints(_ctx.State.RunId)
            .Where(r => r.StageId.Equals(stageId, StringComparison.OrdinalIgnoreCase)).ToList();
        return rows.Count > 0 && rows.All(r => r.Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase));
    }
}
