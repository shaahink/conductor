using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Orchestration;

#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
public sealed partial class RunLoop
{
    // ---------------------------------------------------------------- stage selection / readiness

    private StageConfig CurrentStageConfig()
        => _ctx.Plan.Stages.FirstOrDefault(s => s.Id == _ctx.State.CurrentStage) ?? _ctx.Plan.Stages[^1];

    private StageConfig? SelectStage(TrackerSnapshot track)
    {
        bool IsReady(StageConfig s)
        {
            if (StageComplete(s.Id, track) || _ctx.State.SkippedStages.Contains(s.Id))
                return false;
            return s.DependsOn is not { Count: > 0 }
                || s.DependsOn.All(d => DepSatisfied(d, track));
        }
        return _ctx.Plan.Stages.FirstOrDefault(IsReady);
    }

    private bool AllEffectivelyDone(TrackerSnapshot track)
        => _ctx.Plan.Stages.All(s => StageComplete(s.Id, track) || _ctx.State.SkippedStages.Contains(s.Id));

    private bool StageComplete(string id, TrackerSnapshot track)
        => _ctx.Plan.PerPhaseGates ? _ctx.State.ConfirmedStages.Contains(id) : track.StageDone(id);

    private bool DepSatisfied(string id, TrackerSnapshot track)
        => StageComplete(id, track) || _ctx.State.SkippedStages.Contains(id);

    private int MaxAttempts(StageConfig stage) => Math.Max(1, stage.Sessions * _ctx.Plan.Limits.StageSlackFactor);

    private bool HandoffWantsHuman(TrackerSnapshot track)
        => _ctx.Plan.Conventions.MentionsHuman(track.HandoffBlock);

    // ── per-stage overrides (M3.2) ──

    private void ApplyStageOverrides(StageConfig stage)
    {
        _ctx.State.SkipGatesThisStage = stage.Overrides?.SkipGates == true;
        _ctx.State.SkipCommitThisStage = stage.Overrides?.SkipCommit == true;
        _ctx.State.SkipVerificationThisStage = stage.Overrides?.SkipVerification == true;
        if (stage.Overrides is { } o)
        {
            var flags = new List<string>();
            if (o.SkipGates == true) flags.Add("skip-gates");
            if (o.SkipCommit == true) flags.Add("skip-commit");
            if (o.SkipVerification == true) flags.Add("skip-verification");
            if (flags.Count > 0) _ctx.Log($"stage overrides: {string.Join(", ", flags)}");
        }
    }

    // ---------------------------------------------------------------- budget

    private bool CheckBudgetCap()
    {
        if (_ctx.Plan.Limits.MaxRunCostUsd is { } costCap && _ctx.RunCostUsd >= costCap)
        {
            _ctx.Events.Emit(new OwnerApprovalRequested { StageId = _ctx.State.CurrentStage ?? "?" });
            _ctx.State.Status = RunStatus.AwaitingOwner;
            _ctx.State.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
            _ctx.Log($"budget cap: ${_ctx.RunCostUsd:0.00} >= ${costCap:0.00} (limit) — awaiting owner approval to continue");
            _saveAndReport();
            return true;
        }
        if (_ctx.Plan.Limits.MaxRunTokens is { } tokenCap && _ctx.RunTokens >= tokenCap)
        {
            _ctx.Events.Emit(new OwnerApprovalRequested { StageId = _ctx.State.CurrentStage ?? "?" });
            _ctx.State.Status = RunStatus.AwaitingOwner;
            _ctx.State.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
            _ctx.Log($"token cap: {_ctx.RunTokens / 1000.0:0.#}k >= {tokenCap / 1000.0:0.#}k (limit) — awaiting owner approval to continue");
            _saveAndReport();
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- process lock

    private bool AcquireLock()
    {
        try
        {
            if (File.Exists(_ctx.LockPath))
            {
                var pidText = File.ReadAllText(_ctx.LockPath).Trim();
                if (int.TryParse(pidText, out var pid))
                {
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
                        if (!p.HasExited)
                        {
                            _ctx.Sink.Log($"another conductor (pid {pid}) is already running this plan — exiting");
                            return false;
                        }
                    }
                    catch (ArgumentException) { /* stale lock — process gone */ }
                }
            }
            File.WriteAllText(_ctx.LockPath, Environment.ProcessId.ToString());
            return true;
        }
        catch (Exception ex)
        {
            _ctx.Sink.Log($"could not acquire lock: {ex.Message}");
            return false;
        }
    }

    private void ReleaseLock()
    {
        try { if (File.Exists(_ctx.LockPath)) File.Delete(_ctx.LockPath); }
        catch (IOException) { /* reclaimed next start via pid-liveness */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    // ---------------------------------------------------------------- save, report, snapshot

    private void SaveAndReport()
    {
        _ctx.Save();
        TrackerSnapshot track;
        try { track = _ctx.Progress.Read(_ctx.Plan, CancellationToken.None); }
        catch (Exception) { track = new TrackerSnapshot(); }
        Reporter.WriteAndPublish(_ctx.Plan, _ctx.State, track, _ctx.LastGates, _ctx.Log, store: _ctx.Store);
        PushIdleSnapshot();
    }

    private void PushIdleSnapshot()
    {
        TrackerSnapshot track;
        try { track = _ctx.Progress.Read(_ctx.Plan, CancellationToken.None); }
        catch (Exception) { track = new TrackerSnapshot(); }
        _ctx.Sink.Snapshot(BaseSnapshot(track));
    }

    private void PushSessionSnapshot(AgentSession agent, SessionRecord rec, StageConfig stage, int attempt, int maxAttempts, TrackerSnapshot track)
        => _ctx.Sink.Snapshot(BaseSnapshot(track) with
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

    private DashboardSnapshot BaseSnapshot(TrackerSnapshot track)
        => SnapshotBuilder.Build(_ctx.Plan, _ctx.State, track,
            _ctx.LastGates != null ? GateRunner.Summary(_ctx.LastGates) : "", _ctx.BackoffUntil);

    // ---------------------------------------------------------------- session events

    private void EmitSessionFinished(SessionRecord rec)
    {
        var sid = rec.Number.ToString();
        _ctx.Events.Emit(new SessionFinished
        {
            SessionId = sid,
            Number = rec.Number,
            StageId = rec.Stage,
            Outcome = rec.Outcome?.ToString() ?? "Unknown",
            NewCommits = rec.NewCommits,
            NewlyDone = rec.NewlyDone,
            CostUsd = rec.CostUsd,
            TokensInput = rec.TokensInput,
            TokensOutput = rec.TokensOutput,
            TokensReasoning = rec.TokensReasoning,
            TokensCacheRead = rec.TokensCacheRead,
        });
        if (rec.Outcome == SessionOutcome.Advanced)
            foreach (var id in rec.NewlyDone)
                _ctx.Events.Emit(new CheckpointConfirmed { SessionId = sid, CheckpointId = id, StageId = rec.Stage });

        if (_ctx.Store is { } db)
        {
            db.RecordSession(_ctx.State.RunId, rec.Stage, rec.Number, rec.Kind.ToString(),
                rec.StartedUtc, rec.EndedUtc, rec.Outcome?.ToString(), rec.ClaudeSessionId,
                rec.ResumeCount, rec.Attempt, rec.GateSummary, rec.ResultSummary,
                rec.NewCommits.Count, rec.NewlyDone.Count > 0 ? string.Join(",", rec.NewlyDone) : null);
            if (rec.CostUsd is { } costUsd)
                db.RecordCost(_ctx.State.RunId, rec.Number, "agent",
                    rec.TokensInput ?? 0, rec.TokensOutput ?? 0, rec.TokensReasoning ?? 0,
                    rec.TokensCacheRead ?? 0, costUsd,
                    (long)((rec.EndedUtc - rec.StartedUtc)?.TotalMilliseconds ?? 0));
            if (rec.OverheadCostUsd is { } ovCostUsd)
                db.RecordCost(_ctx.State.RunId, rec.Number, "gate",
                    0, 0, 0, 0, ovCostUsd, 0);

            if (rec.NewlyDone.Count > 0)
            {
                var commit = rec.NewCommits.Count > 0 ? rec.NewCommits[^1].Split(' ')[0] : "-";
                var evidence = rec.GateSummary ?? "completed";
                foreach (var cpId in rec.NewlyDone)
                    db.UpdateCheckpoint(_ctx.State.RunId, cpId, "DONE", commit, evidence);
            }

            var track = ReadTrackerSafe();
            if (!string.IsNullOrWhiteSpace(track.HandoffBlock))
                db.WriteHandover(_ctx.State.RunId, rec.Number, rec.Stage, track.HandoffBlock);

            RegenerateTracker(track);
        }

        WriteSessionHistory(rec);

        _ = _ctx.Telegram.PushSessionEndAsync(rec.Number, rec.Stage, rec.Outcome?.ToString() ?? "Unknown",
            rec.GateSummary, rec.ResultSummary, rec.CostUsd, _ctx.State.PendingFix?.VerifierScore);
    }

    // ---------------------------------------------------------------- notifications

    private void Notify(string message)
    {
        _ = _ctx.Telegram.PushAsync(message);
        _ctx.Webhooks.FireAsync(message);

        var n = _ctx.Plan.Notify;
        if (n == null || string.IsNullOrWhiteSpace(n.Command)) return;
        try
        {
            var args = n.Args.Select(a => a.Replace("{message}", message));
            ProcessRunner.Run(n.Command, args, _ctx.Plan.Repo, TimeSpan.FromMinutes(1));
        }
        catch (Exception ex) { _ctx.Log($"notify failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- F1.2 tracker-as-view helpers

    private void SeedCheckpointsFromTracker()
    {
        if (_ctx.Store is not { } db) return;
        var track = ReadTrackerSafe();
        if (track.Checkpoints.Count == 0) return;

        var cps = track.Checkpoints.Select(c => (c.Id, c.StageId, c.Title,
            c.IsDone ? "DONE" : c.IsInProgress ? "IN PROGRESS" : c.IsBlocked ? "BLOCKED" : "TODO",
            c.Commit, c.Evidence));
        db.SeedCheckpoints(_ctx.State.RunId, cps);
        _ctx.Log($"seeded {track.Checkpoints.Count} checkpoints from tracker into run.db");
    }

    private TrackerSnapshot ReadTrackerSafe()
    {
        try { return _ctx.Progress.Read(_ctx.Plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    private void RegenerateTracker(TrackerSnapshot currentTrack)
    {
        if (_ctx.Store is not { } db) return;
        try
        {
            TrackerGenerator.Write(_ctx.Plan, db, _ctx.State.RunId, currentTrack.HandoffBlock);
        }
        catch (Exception ex)
        {
            _ctx.Log($"tracker regeneration failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- M2.4 session history

    private void WriteSessionHistory(SessionRecord rec)
    {
        try
        {
            var sessionsDir = Path.Combine(_ctx.Plan.StateDir, "sessions");
            var sessionDir = Path.Combine(sessionsDir, rec.Number.ToString("000"));
            Directory.CreateDirectory(sessionDir);

            var promptPath = Path.Combine(_ctx.Plan.StateDir, "logs", $"session-{rec.Number:000}.prompt.md");
            if (File.Exists(promptPath))
                File.Copy(promptPath, Path.Combine(sessionDir, "prompt.md"), overwrite: true);

            // cost.json
            var cost = new
            {
                session = rec.Number,
                stage = rec.Stage,
                kind = rec.Kind.ToString(),
                outcome = rec.Outcome?.ToString(),
                costUsd = rec.CostUsd,
                overheadCostUsd = rec.OverheadCostUsd,
                tokensInput = rec.TokensInput,
                tokensOutput = rec.TokensOutput,
                tokensReasoning = rec.TokensReasoning,
                tokensCacheRead = rec.TokensCacheRead,
                startedUtc = rec.StartedUtc,
                endedUtc = rec.EndedUtc,
                wallMs = (long?)(rec.EndedUtc - rec.StartedUtc)?.TotalMilliseconds,
                commits = rec.NewCommits.Count,
            };
            File.WriteAllText(Path.Combine(sessionDir, "cost.json"),
                JsonSerializer.Serialize(cost, new JsonSerializerOptions { WriteIndented = true }));

            // verdict.md
            if (!string.IsNullOrEmpty(rec.ResultSummary))
                File.WriteAllText(Path.Combine(sessionDir, "verdict.md"), rec.ResultSummary);

            // handover.md
            if (_ctx.Store is { } store)
            {
                var handover = store.GetLatestHandover(_ctx.State.RunId, rec.Stage);
                if (!string.IsNullOrEmpty(handover))
                    File.WriteAllText(Path.Combine(sessionDir, "handover.md"), handover);
            }

            // INDEX.md
            var index = new System.Text.StringBuilder();
            index.AppendLine("# Session History");
            index.AppendLine();
            var entries = Directory.GetDirectories(sessionsDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var entryDir = Path.Combine(sessionsDir, entry);
                var costPath = Path.Combine(entryDir, "cost.json");
                var stageId = "";
                var outcome = "";
                if (File.Exists(costPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(costPath));
                        stageId = doc.RootElement.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "";
                        outcome = doc.RootElement.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "";
                    }
                    catch { }
                }
                var files = Directory.GetFiles(entryDir)
                    .Select(f => Path.GetFileName(f))
                    .OrderBy(f => f, StringComparer.Ordinal);
                index.AppendLine($"- [{entry}]({entry}/) — {stageId} — {outcome}");
                foreach (var f in files)
                    index.AppendLine($"  - [{f}]({entry}/{f})");
            }
            File.WriteAllText(Path.Combine(sessionsDir, "INDEX.md"), index.ToString());
        }
        catch (Exception ex)
        {
            _ctx.Log($"session history write failed: {ex.Message}");
        }
    }
}
