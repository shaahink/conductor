using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Orchestration;

public sealed partial class RunLoop
{
    // ---------------------------------------------------------------- prompt construction
    // (Round 6: the dry-run branch's private prompt switch is gone — SessionComposer.Compose is the
    // one place a session's prompt is put together, and the dry run calls it like the dispatch does.)

    /// <summary>SC3.3: an unresolvable placeholder is a config defect, not a crash. It used to travel
    /// all the way out of the process — the refusal reached stderr, nothing reached conductor.log, and
    /// <c>status</c> went on calling the dead run idle. Park instead: the control plane stays up, the
    /// reason is on every surface, and the operator fixes the template or the plan and resumes into
    /// the same run. The session number is handed back too — nothing was spawned to spend it.</summary>
    private void ParkOnPromptRefusal(StageConfig stage, PromptCompositionException ex)
    {
        if (_ctx.State.History.LastOrDefault()?.Number != _ctx.State.SessionCounter)
            _ctx.State.SessionCounter--;
        _verdicts.NeedsHuman($"prompt for stage {stage.Id} could not be composed — {ex.Message} " +
            "Fix the template or the plan, then `conductor resume` (a plan edit also needs `conductor plan reload`).");
    }

    private static PromptBuilder BuildPromptBuilder(PlanConfig plan)
    {
        var registry = new PersonaRegistry(plan);
        var lessons = new LessonsManager(plan.StateDir);
        return new PromptBuilder(plan, registry, lessons);
    }

    // ---------------------------------------------------------------- activity tracking

    private void TrackActivity(AgentEvent ev, int _)
    {
        if (ev.Kind is not ("tool" or "text" or "result" or "thinking")) return;
        _ctx.Activity.Add((ev.Kind, ev.Text, ev.Utc));
        if (_ctx.Activity.Count > 60) _ctx.Activity.RemoveRange(0, 20);
    }

    private string BuildActivitySection(SessionRecord rec, AgentSession agent)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"_Session #{rec.Number} ({rec.Kind}) · running {(DateTime.UtcNow - agent.StartedUtc).TotalMinutes:0}m · " +
                      $"last output {(DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds:0}s ago" +
                      (agent.CostUsd is { } c ? $" · ${c:0.0000}" : "") + "_");
        sb.AppendLine();
        var think = _ctx.Activity.Where(a => a.Kind == "thinking").TakeLast(3).ToList();
        if (think.Count > 0)
        {
            sb.AppendLine("**Thinking:**");
            foreach (var t in think) sb.AppendLine($"> {Trunc(t.Text.Replace("\n", " "), 300)}");
            sb.AppendLine();
        }
        var acts = _ctx.Activity.Where(a => a.Kind != "thinking").TakeLast(10).ToList();
        if (acts.Count > 0)
        {
            sb.AppendLine("**Recent actions:**");
            foreach (var a in acts)
            {
                var glyph = a.Kind switch { "tool" => "\u00bb", "result" => "\u25c6", _ => "\u00b7" };
                sb.AppendLine($"- `{a.Utc.ToLocalTime():HH:mm:ss}` {glyph} {Trunc(a.Text.Replace("\n", " "), 160)}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private void RefreshReport(SessionRecord rec, StageConfig stage, AgentSession agent, TrackerSnapshot track)
    {
        try
        {
            var cp = track.ForStage(stage.Id).FirstOrDefault(c => c.IsOpen)?.Id ?? stage.Id;
            _ctx.Log($"report refresh @ {cp} (cost ${agent.CostUsd:0.00})");
            Reporter.WriteReport(_ctx.Plan, _ctx.State, track, _ctx.LastGates, _ctx.Log, BuildActivitySection(rec, agent), store: _ctx.Store,
                onNewOwnerItems: _ctx.NotifyNewOwnerQueueItems);
        }
        catch (Exception ex) { _ctx.Log($"report refresh failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- static helpers

    // K5.1: the second copy of ExtractSessionResult lived here, uncalled — a private duplicate of
    // SessionRunner's, with the same 700-char blind cut. One parse, one place: SessionResult.
    // CH1.3: LastRawTail was the same story and went the same way — a private duplicate of
    // SessionRunner.Refusals' copy that nothing on RunLoop called, kept alive only by the
    // file-wide MA0045 suppression it was the sole justification for. Both are gone.

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;
}
