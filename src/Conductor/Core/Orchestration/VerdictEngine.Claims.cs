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

    /// <summary>SF0.2 (bug #10): a claim is a claim whoever was in the chair.
    /// <para><see cref="EvaluateSessionAsync"/> reads the work graph on the DELIVERY path only — the
    /// Audit and Verify branches return long before it. So a checkpoint claimed while one of those
    /// sessions held the run (the owner running <c>conductor task --done</c> from another shell
    /// mid-verify; an audit session fixing and claiming as it went) was counted in NO session's
    /// <c>NewlyDone</c>: history, the report, the timeline and StatusAgent all showed it belonging to
    /// nobody, <c>PendingConfirmation</c> never received it so it could never reach DONE ✓, and the
    /// engine-side commit + evidence stamp never ran. It could not be picked up later either — the
    /// next delivery session's PRE-set is folded from the event log at ITS start, which by then
    /// already contains the claim, so the claim was invisible from both sides.</para>
    /// <para>The graph is the only signal consulted here. The W1.3 tracker-diff fallback stays a
    /// delivery-path concession: a verify or audit session regenerating the tracker is bookkeeping,
    /// not a claim, and reading a flip out of it would attribute the PREVIOUS session's work to this
    /// one.</para></summary>
    private void RecordNonDeliveryClaims(SessionRecord rec)
    {
        if (GraphClaimsDuringSession(rec) is not { Count: > 0 } claims) return;
        rec.NewlyDone = claims;
        foreach (var id in claims)
        {
            if (!_ctx.State.PendingConfirmation.Contains(id, StringComparer.OrdinalIgnoreCase))
                _ctx.State.PendingConfirmation.Add(id);
        }
        _ctx.Log($"claim during {rec.Kind.ToString().ToLowerInvariant()} session #{rec.Number}: " +
                 $"[{string.Join(", ", claims)}] — counted for this session and queued for confirmation");
    }

    /// <summary>All of a stage's checkpoint rows in the graph read DONE (and it has some) — so a
    /// stage whose last item was claimed only via the graph is complete NOW, not one tracker
    /// regeneration later. SC5.3: a SKIPPED row is settled too, or one `task --skipped` would leave
    /// the stage permanently incomplete.</summary>
    private bool GraphStageDone(string stageId)
    {
        if (_ctx.Store is not { } db) return false;
        var rows = db.GetCheckpoints(_ctx.State.RunId)
            .Where(r => r.StageId.Equals(stageId, StringComparison.OrdinalIgnoreCase)).ToList();
        return rows.Count > 0 && rows.All(r =>
            r.Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) ||
            r.Status.StartsWith("SKIPPED", StringComparison.OrdinalIgnoreCase));
    }
}
