using Conductor.Core.Events;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class SessionRunner
{
    // ── snapshot + activity tracking ──

    private void TrackActivity(AgentEvent ev, SessionRecord rec)
    {
        if (ev.Kind is not ("tool" or "text" or "result" or "thinking" or "stderr")) return;
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
