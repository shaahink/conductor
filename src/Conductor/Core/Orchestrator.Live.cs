using System.Text;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Core;

#pragma warning disable MA0045 // Session helper methods use sync file I/O by design — fast local writes, not hot-path
public sealed partial class Orchestrator
{
    // ---------------------------------------------------------------- prompt construction

    private string BuildPrompt(SessionKind kind, StageConfig stage, int sessionNumber, int attempt, int maxAttempts)
    {
        var isReview = stage.Kind.Equals("review", StringComparison.OrdinalIgnoreCase);
        var reviewPath = isReview ? Path.Combine(plan.StateDir, "reviews", $"{stage.Id}.md") : "";
        return kind switch
        {
            SessionKind.Resume => _prompts.Resume(stage, sessionNumber, attempt, maxAttempts, state.PendingResume!),
            SessionKind.Audit => _prompts.Audit(stage, sessionNumber, state.PendingAudit!, state.CurrentStageStartHead ?? "HEAD~1"),
            SessionKind.Fix => _prompts.Fix(stage, sessionNumber, attempt, maxAttempts, state.PendingFix!),
            _ => isReview
                ? _prompts.Review(stage, sessionNumber, attempt, maxAttempts, reviewPath)
                : _prompts.Deliver(stage, sessionNumber, attempt, maxAttempts),
        };
    }

    private static PromptBuilder BuildPromptBuilder(PlanConfig plan)
    {
        var registry = new PersonaRegistry(plan);
        var lessons = new LessonsManager(plan.StateDir);
        return new PromptBuilder(plan, registry, lessons);
    }

    private static string ExtractSessionResult(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return "";
        var idx = resultText.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        var s = idx >= 0 ? resultText[idx..] : resultText;
        return Trunc(s.Trim(), 700);
    }

    private string LastRawTail(string rawLogPath)
    {
        try { return GateRunner.TailOf(File.ReadAllText(rawLogPath), 10); }
        catch (IOException) { return ""; }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;

    // ---------------------------------------------------------------- snapshots, log, lock

    private void PushSessionSnapshot(AgentSession agent, SessionRecord rec, StageConfig stage, int attempt, int maxAttempts, TrackerSnapshot track)
        => sink.Snapshot(BaseSnapshot(track) with
        {
            SessionNumber = rec.Number,
            SessionKind = rec.Kind.ToString(),
            Attempt = attempt,
            MaxAttempts = maxAttempts,
            ResumeCount = rec.ResumeCount,
            SessionCostUsd = agent.CostUsd ?? 0m,
            SessionTokensInput = agent.TokensInput ?? 0,
            SessionTokensOutput = agent.TokensOutput ?? 0,
            SessionTokensReasoning = agent.TokensReasoning ?? 0,
            SessionElapsed = DateTime.UtcNow - agent.StartedUtc,
            LastActivityAgoSec = (DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds,
            AgentActive = true,
        });

    private void PushIdleSnapshot()
    {
        TrackerSnapshot track;
        try { track = _progress.Read(plan, CancellationToken.None); }
        catch (Exception) { track = new TrackerSnapshot(); }
        sink.Snapshot(BaseSnapshot(track));
    }

    private DashboardSnapshot BaseSnapshot(TrackerSnapshot track)
        => SnapshotBuilder.Build(plan, state, track,
            Ctx.LastGates != null ? GateRunner.Summary(Ctx.LastGates) : "", _backoffUntil);

    private void TrackActivity(AgentEvent ev, int sessionNumber)
    {
        transcript?.Append(sessionNumber.ToString(), ev.Kind, ev.Text);
        if (ev.Kind is not ("tool" or "text" or "result" or "thinking")) return;
        _activity.Add((ev.Kind, ev.Text, ev.Utc));
        if (_activity.Count > 60) _activity.RemoveRange(0, 20);
    }

    private string BuildActivitySection(SessionRecord rec, AgentSession agent)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"_Session #{rec.Number} ({rec.Kind}) · running {(DateTime.UtcNow - agent.StartedUtc).TotalMinutes:0}m · " +
                      $"last output {(DateTime.UtcNow - agent.LastActivityUtc).TotalSeconds:0}s ago" +
                      (agent.CostUsd is { } c ? $" · ${c:0.0000}" : "") + "_");
        sb.AppendLine();
        var think = _activity.Where(a => a.Kind == "thinking").TakeLast(3).ToList();
        if (think.Count > 0)
        {
            sb.AppendLine("**Thinking:**");
            foreach (var t in think) sb.AppendLine($"> {Trunc(t.Text.Replace("\n", " "), 300)}");
            sb.AppendLine();
        }
        var acts = _activity.Where(a => a.Kind != "thinking").TakeLast(10).ToList();
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
            var cp = track.ForStage(stage.Id).FirstOrDefault(c => !c.IsDone)?.Id ?? stage.Id;
            Log($"report refresh @ {cp} (cost ${agent.CostUsd:0.00})");
            Reporter.WriteReport(plan, state, track, Ctx.LastGates, Log, BuildActivitySection(rec, agent));
        }
        catch (Exception ex) { Log($"report refresh failed: {ex.Message}"); }
    }
}
