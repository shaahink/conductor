using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Lanes;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
public sealed partial class SessionRunner
{
    // ── soft-break + MCP wiring ──

    private void CheckSoftBreak(AgentSession agent, TrackerSnapshot preTrack)
    {
        if (_ctx.SoftBreakSignalled) return;
        var threshold = ComputeSoftThreshold();
        if (threshold is not { } thresh) return;

        var liveTokens = (agent.TokensInput ?? 0) + (agent.TokensOutput ?? 0)
            + (agent.TokensReasoning ?? 0) + (agent.TokensCacheRead ?? 0);
        if (liveTokens < thresh) return;

        _ctx.SoftBreakSignalled = true;
        var activeCp = preTrack.Checkpoints.FirstOrDefault(c => !c.IsDone)?.Id;
        var maxTokens = _ctx.Plan.Limits.MaxSessionTokens!.Value;
        var signalFile = Path.Combine(_ctx.Plan.StateDir, "soft-break");
        File.WriteAllText(signalFile, $"finish-subtask-and-handoff:{DateTime.UtcNow:o}");

        _ctx.Events.Emit(new SoftBreakRequested
        {
            LiveTokens = liveTokens,
            TokenBudget = maxTokens,
            CurrentCheckpointId = activeCp,
        });
        _ctx.Log($"soft-break: {liveTokens / 1000.0:0.#}k tokens >= {thresh / 1000.0:0.#}k threshold — nudge written, session should hand off cleanly");
        _ctx.Sink.Log($"[soft-break] {liveTokens / 1000.0:0.#}k/{maxTokens / 1000.0:0.#}k tokens — agent has been nudged to hand off");
    }

    private long? ComputeSoftThreshold()
    {
        if (_ctx.Plan.Limits.MaxSessionTokens is not { } max) return null;
        var ratio = _ctx.Plan.Limits.SoftBreakRatio is { } r and > 0 and <= 1.0
            ? r : 0.8;
        return (long)(max * ratio);
    }

    private void CleanSoftBreakSignal()
    {
        var signalFile = Path.Combine(_ctx.Plan.StateDir, "soft-break");
        try { if (File.Exists(signalFile)) File.Delete(signalFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string? WireMcpServer(SessionRecord rec, StageConfig stage)
    {
        try
        {
            var conductorExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(conductorExe) || !File.Exists(conductorExe))
                return null;

            var eventsPath = Path.Combine(_ctx.Plan.StateDir, "events.jsonl");
            var journalPath = Path.Combine(_ctx.Plan.StateDir, "mcp-journal.jsonl");
            var runId = _ctx.State.RunId;
            var stateDir = _ctx.Plan.StateDir;
            var repoPath = _ctx.Plan.Repo;

            var commandArgs = new List<string>
            {
                "mcp-serve",
                "--events", eventsPath,
                "--journal", journalPath,
                "--run-id", runId,
                "--state-dir", stateDir,
                "--repo", repoPath,
            };

            var mcpConfig = new
            {
                mcp = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["conductor-tasks"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["type"] = "local",
                        ["command"] = new[] { conductorExe }.Concat(commandArgs).ToArray(),
                        ["enabled"] = true,
                    }
                }
            };

            var configPath = Path.Combine(_ctx.Plan.StateDir, "mcp-config.json");
            var json = JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            return configPath;
        }
        catch (Exception ex)
        {
            _ctx.Log($"I1: failed to write MCP config: {ex.Message}");
            return null;
        }
    }

    private void CleanupMcpConfig(string? configPath)
    {
        if (configPath == null) return;
        try { if (File.Exists(configPath)) File.Delete(configPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void FoldMcpJournal()
    {
        var journalPath = Path.Combine(_ctx.Plan.StateDir, "mcp-journal.jsonl");
        if (!File.Exists(journalPath)) return;
        try
        {
            var journalEvents = EventLog.ReadAll(journalPath);
            if (journalEvents.Count == 0) return;
            foreach (var evt in journalEvents)
                _ctx.Events.Emit(evt);
            File.Delete(journalPath);
            _ctx.Log($"MCP journal folded: {journalEvents.Count} event(s) merged into event log");
        }
        catch (Exception ex)
        {
            _ctx.Log($"MCP journal fold failed: {ex.Message}");
        }
    }

    private string? BuildRolloverResumeHint(TrackerSnapshot preTrack)
    {
        if (_ctx.Store == null) return null;
        try
        {
            var allEvents = _ctx.Store.ReadAllEvents(_ctx.State.RunId);
            var taskGraph = new TaskGraph();
            taskGraph.Fold(allEvents);
            var activeCp = preTrack.ForStage(_ctx.State.CurrentStage ?? "")
                .FirstOrDefault(c => !c.IsDone);
            if (activeCp == null) return null;
            var next = taskGraph.CurrentTask(activeCp.Id);
            return next != null
                ? $"next sub-task: {next.Title} [{next.Status}]"
                : null;
        }
        catch (Exception ex)
        {
            _ctx.Log($"task-graph resume hint failed: {ex.Message}");
            return null;
        }
    }
}
