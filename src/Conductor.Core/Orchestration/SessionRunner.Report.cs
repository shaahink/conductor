using System.Text;

using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>The live report refresh, delegated from RunLoop — rendering the in-flight session's
/// activity into REPORT.md. Split out of <c>SessionRunner.cs</c> under the architecture ratchet
/// (SC5.3): driving a session and rendering a report about one are two jobs, and the driver file
/// had reached the 500-line ceiling.</summary>
public sealed partial class SessionRunner
{
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
                var glyph = a.Kind switch { "tool" => "»", "result" => "◆", _ => "·" };
                sb.AppendLine($"- `{a.Utc.ToLocalTime():HH:mm:ss}` {glyph} {Trunc(a.Text.Replace("\n", " "), 160)}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
