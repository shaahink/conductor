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

    /// <summary>W1.3's claim rule, in ONE place. The claim signal is the WORK GRAPH — what
    /// <c>conductor task --done</c> / MCP <c>task_update</c> wrote during this session. The tracker
    /// diff survives as a FLAGGED transition fallback so an old-habit agent still makes progress,
    /// loudly; a tracker hand-edit is no longer a claim of its own, so the M4.1 veto has nothing
    /// left to veto. K1.1 lifted this out of <see cref="EvaluateSessionAsync"/> so the rollover path
    /// records claims by the same rule rather than by a second, quieter one.</summary>
    private List<string> ResolveClaims(SessionRecord rec, string stageId,
        TrackerSnapshot preTrack, TrackerSnapshot postTrack)
    {
        var trackerFlips = postTrack.Checkpoints
            .Where(c => c.IsDone && !(preTrack.ById(c.Id)?.IsDone ?? false))
            .Select(c => c.Id).ToList();
        var graphClaims = GraphClaimsDuringSession(rec);
        if (graphClaims is null) return trackerFlips; // no store / no session-start marker — legacy signal

        var legacy = trackerFlips.Except(graphClaims, StringComparer.OrdinalIgnoreCase).ToList();
        if (legacy.Count == 0) return graphClaims;

        _ctx.Log($"WARNING: {legacy.Count} checkpoint(s) flipped DONE only in the tracker markdown: [{string.Join(", ", legacy)}] — accepted via the transition fallback; claim with `conductor task --done` or MCP task_update, the tracker is a generated view", "warn");
        _ctx.Store?.WriteLedger(_ctx.State.RunId, rec.Number, stageId, "legacy-claim",
            $"Tracker-only DONE flips accepted via the W1.3 transition fallback: [{string.Join(", ", legacy)}]. The graph heard no claim — report through 'conductor task --done' or MCP task_update.");
        return [.. graphClaims, .. legacy];
    }

    /// <summary>K1.1: a rolled-over session's facts, recorded like any other session's.
    /// <para>The rollover branch in <see cref="SessionRunner"/> returns before the verdict pass, so
    /// nothing ever filled <see cref="SessionRecord.NewCommits"/> (whence <c>commit_count</c>) or
    /// <see cref="SessionRecord.NewlyDone"/>. Measured over both Sarban runs: <c>commit_count</c> was
    /// 0 on 100% of rollovers while git ground truth over each session's own window said 91% of them
    /// had committed. Every board, REPORT.md row, digest and Telegram push under-reported on every
    /// rollover; one client-site run called a session idle that had shipped a pull request.</para>
    /// <para>This records the FACTS and nothing else. It deliberately does not touch
    /// <c>AttemptsThisStage</c>, run no gate battery and advance no workflow step: a rollover still
    /// costs no attempt and still defers the phase gate, which is what a rollover MEANS. The claims
    /// are queued for confirmation because this session will never reach the verdict that would
    /// queue them, and an unqueued claim can never reach DONE ✓ from either side (SF0.2, bug #10).</para></summary>
    public void RecordRolloverFacts(SessionRecord rec, StageConfig stage, TrackerSnapshot preTrack,
        string startHead, CancellationToken ct)
    {
        CollectCommits(rec, startHead);
        NoteOutsideRepoWrites(rec);

        var postTrack = _ctx.Progress.Read(_ctx.Plan, ct);
        rec.NewlyDone = ResolveClaims(rec, stage.Id, preTrack, postTrack);
        foreach (var id in rec.NewlyDone)
            if (!_ctx.State.PendingConfirmation.Contains(id, StringComparer.OrdinalIgnoreCase))
                _ctx.State.PendingConfirmation.Add(id);

        var work = SessionProgress.WorkCommits(rec);
        var bookkeeping = rec.NewCommits.Count + rec.SatelliteCommits.Count - work.Count;
        _ctx.Log($"rollover facts for session #{rec.Number}: commits {work.Count}" +
                 (bookkeeping > 0 ? $" (+{bookkeeping} conductor bookkeeping, not counted)" : "") +
                 $" · newly DONE [{string.Join(",", rec.NewlyDone)}] — recorded without a gate or an attempt");
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
