using Conductor.Core.Events;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class SessionRunner
{
    // ── snapshot + activity tracking ──

    private void TrackActivity(AgentEvent ev, SessionRecord rec)
    {
        // KS7.1: `refusal` is on this list because the list is an ALLOWLIST — a kind the provider
        // emits and this line does not name reaches no transcript and no Face pane, silently. A
        // posture that refuses a tool call and shows the operator nothing is the failure mode the
        // whole checkpoint exists to remove, and it was exactly what the first rig run measured.
        if (ev.Kind is not ("tool" or "text" or "result" or "thinking" or "stderr" or "refusal")) return;
        // The live transcript feed (/transcript/current → the Face agent pane). This was the
        // disconnected wire of the 2026-07-16 dogfood: TranscriptLog existed but nothing wrote it,
        // so the Face could only ever replay a stale file from an earlier build.
        // SC7.1: a tool event carries its STRUCTURE here too, so the stored line holds the real path
        // or command instead of a JSON blob cut at 150 characters.
        _ctx.Transcript.Append(rec.Number.ToString(), ev.Kind, ev.Text, ev.Tool);
        // SC7.2: the same funnel folds the per-session digest, so it survives a kill mid-session.
        if (ev.Tool != null) { rec.Digest.Add(ev.Tool, _ctx.Plan.Repo); NoteOutsideRepoWrite(ev.Tool, rec); }
        if (ev.Kind is "stderr") return; // the activity ring buffer keeps its original vocabulary
        _ctx.Activity.Add((ev.Kind, ev.Text, ev.Utc));
        if (_ctx.Activity.Count > 60) _ctx.Activity.RemoveRange(0, 20);
    }

    /// <summary>KS7.2 — swaps the transcript-derived digest for the hook-derived one, when the hook
    /// delivered anything. Called once, at session end, after the stream has been drained.</summary>
    /// <remarks>
    /// The live fold in <see cref="TrackActivity"/> stays exactly as it was, and on purpose: it is
    /// what feeds the Face's agent pane and the out-of-repo write check WHILE the session runs, and a
    /// digest that only existed after the process exited would take that away from a session that
    /// gets killed. So the transcript path remains the running estimate and the hook file becomes the
    /// record — primary where it exists, silent where it does not.
    /// <para>Absent and empty are the same answer here. A hook-less agent (opencode, <c>--bare</c>, a
    /// provider with no hook surface at all) writes no file; a session that made no tool calls writes
    /// an empty one; in both the transcript-derived digest is the only source there is, and promoting
    /// an empty digest over it would report a session that did nothing.</para>
    /// </remarks>
    private void PromoteHookDigest(SessionRecord rec)
    {
        var path = HookToolLog.PathFor(_ctx.Plan.StateDir, rec.Number);
        if (HookToolLog.BuildDigest(path, _ctx.Plan.Repo) is not { } hookDigest) return;
        var before = rec.Digest.ToolCalls;
        rec.Digest = hookDigest;
        if (before != hookDigest.ToolCalls)
            _ctx.Log($"session #{rec.Number} digest source: hook ({hookDigest.ToolCalls} calls; " +
                     $"the transcript saw {before})");
    }

    /// <summary>How many distinct out-of-repo paths one session records. The verdict reports a COUNT
    /// and names the first few; a session that writes a thousand files under %TEMP% must not put a
    /// thousand strings in state.json to say so.</summary>
    internal const int MaxOutsideRepoWrites = 25;

    /// <summary>SC7.1 (devcontext #11): the write-scope half of structured capture. A tool call that
    /// writes to a resolved path outside the plan's repo — and outside every repo the plan declared as
    /// a satellite — is recorded on the session so the verdict can note it. Reading, grepping and
    /// listing outside the repo are not writes and are not counted.</summary>
    private void NoteOutsideRepoWrite(ToolCall call, SessionRecord rec)
    {
        if (!ToolEventExtractor.IsWrite(call.Name)) return;
        if (call.Field("path") is not { Length: > 0 } path) return;
        if (rec.OutsideRepoWrites.Count >= MaxOutsideRepoWrites) return;
        var satellites = SatelliteRepos.Resolve(_ctx.Plan).Select(s => s.Path).ToList();
        if (!RepoScope.IsOutside(_ctx.Plan.Repo, satellites, path, out var full)) return;
        if (!rec.OutsideRepoWrites.Contains(full, StringComparer.OrdinalIgnoreCase))
            rec.OutsideRepoWrites.Add(full);
    }
}
