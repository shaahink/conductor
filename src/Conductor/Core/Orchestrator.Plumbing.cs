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
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

// Suppressions carried forward from Orchestrator.cs — these methods use sync I/O by design for
// fast local writes, not hot-path work.
#pragma warning disable MA0045

public sealed partial class Orchestrator
{
    // ---------------------------------------------------------------- stage selection / readiness

    private StageConfig CurrentStageConfig()
        => plan.Stages.FirstOrDefault(s => s.Id == state.CurrentStage) ?? plan.Stages[^1];

    private StageConfig? SelectStage(TrackerSnapshot track)
    {
        // B10.1: readiness = stage itself not complete/skipped AND all dependsOn satisfied.
        // Among ready stages, plan.Stages order determines priority (preserves sequential intent).
        bool IsReady(StageConfig s)
        {
            if (StageComplete(s.Id, track) || state.SkippedStages.Contains(s.Id))
                return false;
            return s.DependsOn is not { Count: > 0 }
                || s.DependsOn.All(d => DepSatisfied(d, track));
        }
        return plan.Stages.FirstOrDefault(IsReady);
    }

    private bool AllEffectivelyDone(TrackerSnapshot track)
        => plan.Stages.All(s => StageComplete(s.Id, track) || state.SkippedStages.Contains(s.Id));

    /// <summary>Under perPhase, a stage is "complete" only once its full battery (and audit) confirmed it —
    /// so a stage whose tracker rows read DONE but whose phase-gate is red is never advanced past.</summary>
    private bool StageComplete(string id, TrackerSnapshot track)
        => plan.PerPhaseGates ? state.ConfirmedStages.Contains(id) : track.StageDone(id);

    /// <summary>A dependency satisfied if the target stage is confirmed/done OR has been skipped
    /// (you can't run a skipped stage — treating it as effectively done unblocks dependents, B10.1).</summary>
    private bool DepSatisfied(string id, TrackerSnapshot track)
        => StageComplete(id, track) || state.SkippedStages.Contains(id);

    private int MaxAttempts(StageConfig stage) => Math.Max(1, stage.Sessions * plan.Limits.StageSlackFactor);

    private bool HandoffWantsHuman(TrackerSnapshot track)
        => plan.Conventions.MentionsHuman(track.HandoffBlock);

    // ---------------------------------------------------------------- gates

    private async Task<IReadOnlyList<GateResult>> RunGateBatteryAsync(CancellationToken ct, bool fastOnly = false)
    {
        _curGate = fastOnly ? "battery:fast" : "battery:full";
        try
        {
            var gates = await Gates.RunBatteryAsync(Log, LogWithOutcome, sink.GateProgress, ct, fastOnly).ConfigureAwait(false);
            return gates;
        }
        finally { _curGate = null; }
    }

    private void EmitGates(IReadOnlyList<GateResult> gates, string scope, string? sessionId = null)
    {
        Gates.PersistGates(gates, scope, sessionId);
    }

    // ---------------------------------------------------------------- budget

    /// <summary>Returns true if the run is now parked at <c>AwaitingOwner</c> due to a budget cap.</summary>
    private bool CheckBudgetCap()
    {
        if (plan.Limits.MaxRunCostUsd is { } costCap && _runCostUsd >= costCap)
        {
            events.Emit(new OwnerApprovalRequested { StageId = state.CurrentStage ?? "?" });
            state.Status = RunStatus.AwaitingOwner;
            state.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
            Log($"budget cap: ${_runCostUsd:0.00} >= ${costCap:0.00} (limit) — awaiting owner approval to continue");
            SaveAndReport();
            return true;
        }
        if (plan.Limits.MaxRunTokens is { } tokenCap && _runTokens >= tokenCap)
        {
            events.Emit(new OwnerApprovalRequested { StageId = state.CurrentStage ?? "?" });
            state.Status = RunStatus.AwaitingOwner;
            state.AwaitingOwnerReason = AwaitingOwnerReason.Budget;
            Log($"token cap: {_runTokens / 1000.0:0.#}k >= {tokenCap / 1000.0:0.#}k (limit) — awaiting owner approval to continue");
            SaveAndReport();
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- DNS preflight (legacy — likely no callers; delete in M1.4)

    /// <summary>Resolves the configured DNS hosts to verify network health before spawning.
    /// Returns true if all hosts resolve or the check is disabled.</summary>
    private async Task<bool> CheckDnsPreflightAsync()
    {
        var cfg = plan.Limits.DnsHealthCheck;
        if (cfg is not { Enabled: true } || cfg.Hosts is not { Count: > 0 }) return true;
        foreach (var host in cfg.Hosts)
        {
            try
            {
                await Dns.GetHostEntryAsync(host).ConfigureAwait(false);
                Log($"DNS preflight: {host} OK");
            }
            catch (Exception ex)
            {
                Log($"DNS preflight FAIL: {host} — {ex.Message}");
                return false;
            }
        }
        Log("DNS preflight: all hosts healthy");
        return true;
    }

    // ---------------------------------------------------------------- control & plumbing

    private async Task<ControlAction?> HandleControlAsync(bool inSession = false, CancellationToken ct = default)
    {
        var cmd = sink.PollControl() ?? PollInbox() ?? await ReadControlFileAsync(ct).ConfigureAwait(false);
        if (cmd is not { } c) return null;
        return await Dispatcher.DispatchAsync(c, inSession, ct).ConfigureAwait(false);
    }

    private ControlCommand? PollInbox() =>
        _controlInbox != null && _controlInbox.TryDequeue(out var c) ? c : null;

    private async Task<ControlCommand?> ReadControlFileAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_controlPath)) return null;
            var writeTime = File.GetLastWriteTimeUtc(_controlPath);
            if (_lastControlWrite == writeTime) return null; // already processed this version
            _lastControlWrite = writeTime;
            var text = await File.ReadAllTextAsync(_controlPath, ct).ConfigureAwait(false);
            var parsed = ControlFile.Parse(text);
            if (parsed.Action == null) return null;
            if (parsed.Confirmed && parsed.IntentId != null)
                Log($"control confirmed [intent={parsed.IntentId}]");
            return parsed;
        }
        // A malformed/racing control.json is operator input, not an engine fault — ignore this poll
        // and let the next one pick up a well-formed file rather than crash the loop.
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private void DeleteControlFile()
    {
        try { if (File.Exists(_controlPath)) File.Delete(_controlPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _lastControlWrite = null;
    }

    private void RecoverFromCrash()
    {
        var recovered = false;

        // Existing state.json-based path (authoritative for transient control fields the log
        // doesn't yet carry — additive discipline).
        if (state.Status is RunStatus.Running or RunStatus.VerifyingGates or RunStatus.Backoff)
        {
            var last = state.History.LastOrDefault();
            if (last != null && last.EndedUtc == null)
            {
                last.EndedUtc = DateTime.UtcNow;
                last.Outcome = SessionOutcome.Interrupted;
                Verdicts.QueueResume(last, "conductor crashed or was killed mid-session");
                Log($"recovered: session #{last.Number} was interrupted — will resume its agent session");
                recovered = true;
            }
            state.Status = RunStatus.Idle;
            Save();
        }

        // B2.3: event-log-based recovery — the event log may know about a crash that state.json
        // missed (double-hard crash between save and session finish, or a torn state.json write).
        if (!recovered && state.PendingResume == null)
        {
            var eventsPath = Path.Combine(plan.StateDir, "events.jsonl");
            if (File.Exists(eventsPath))
            {
                var evts = EventLog.ReadAll(eventsPath);
                var interrupted = RunStateProjection.FindInterruptedSession(evts);
                if (interrupted != null)
                {
                    var rec = state.History.FirstOrDefault(h => h.Number == interrupted.Number);
                    if (rec != null)
                    {
                        if (rec.EndedUtc == null) rec.EndedUtc = DateTime.UtcNow;
                        rec.Outcome = SessionOutcome.Interrupted;
                        Verdicts.QueueResume(rec, "event log shows interrupted session — recovering");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(interrupted.AgentSessionId))
                        {
                            Log($"recovered from event log: session #{interrupted.Number} has no AgentSessionId — marking needs-attention (cannot resume without a session id)");
                            state.Status = RunStatus.NeedsHuman;
                            state.AttentionReason = $"Orphaned session #{interrupted.Number} in events.jsonl has no AgentSessionId — manual review needed.";
                            Save();
                        }
                        else
                        {
                            rec = new SessionRecord
                            {
                                Number = interrupted.Number,
                                Stage = interrupted.StageId,
                                Kind = SessionKind.Deliver,
                                Attempt = 1,
                                StartedUtc = DateTime.UtcNow,
                                ClaudeSessionId = interrupted.AgentSessionId,
                                Outcome = SessionOutcome.Interrupted,
                            };
                            state.History.Add(rec);
                            Verdicts.QueueResume(rec, "event log shows interrupted session — recovering from orphaned SessionStarted");
                        }
                    }
                    if (state.Status != RunStatus.NeedsHuman)
                    {
                        Log($"recovered from event log: session #{interrupted.Number} was interrupted — will resume");
                        state.Status = RunStatus.Idle;
                        Save();
                    }
                }

                // B9.2: rebuild decomposed-checkpoints set from TaskAdded events so we don't
                // re-decompose after a crash.
                foreach (var evt in evts)
                {
                    if (evt is TaskAdded ta)
                        _decomposedCheckpoints.Add(ta.CheckpointId);
                }
            }
        }
    }

    private void WarnOnBranchPattern()
    {
        if (string.IsNullOrWhiteSpace(plan.BranchPattern)) return;
        var branch = Git.Branch(plan.Repo);
        if (!Regex.IsMatch(branch, plan.BranchPattern, RegexOptions.None, ProgressConventions.RegexTimeout))
            Log($"⚠ branch '{branch}' does not match plan branchPattern '{plan.BranchPattern}' — check before letting sessions commit");
    }

    // ---------------------------------------------------------------- save, report, snapshot

    private void Save() => state.Save(statePath);

    /// <summary>Emit the terminal event for a session from its finalized record (single choke point:
    /// the record's Outcome is set on every RunSession exit path). Also emits CheckpointConfirmed for
    /// each row that flipped DONE in a gate-green, committed session (an Advanced outcome).</summary>
    private void EmitSessionFinished(SessionRecord rec)
    {
        var sid = rec.Number.ToString();
        events.Emit(new SessionFinished
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
                events.Emit(new CheckpointConfirmed { SessionId = sid, CheckpointId = id, StageId = rec.Stage });

        // F1: additive run.db write alongside events.jsonl
        if (_runDb is { } db)
        {
            db.RecordSession(state.RunId, rec.Stage, rec.Number, rec.Kind.ToString(),
                rec.StartedUtc, rec.EndedUtc, rec.Outcome?.ToString(), rec.ClaudeSessionId,
                rec.ResumeCount, rec.Attempt, rec.GateSummary, rec.ResultSummary,
                rec.NewCommits.Count, rec.NewlyDone.Count > 0 ? string.Join(",", rec.NewlyDone) : null);
            if (rec.CostUsd is { } costUsd)
                db.RecordCost(state.RunId, rec.Number, "agent",
                    rec.TokensInput ?? 0, rec.TokensOutput ?? 0, rec.TokensReasoning ?? 0,
                    rec.TokensCacheRead ?? 0, costUsd,
                    (long)((rec.EndedUtc - rec.StartedUtc)?.TotalMilliseconds ?? 0));
            if (rec.OverheadCostUsd is { } ovCostUsd)
                db.RecordCost(state.RunId, rec.Number, "gate",
                    0, 0, 0, 0, ovCostUsd, 0);

            // F1.2: update checkpoints marked as done in this session
            if (rec.NewlyDone.Count > 0)
            {
                var commit = rec.NewCommits.Count > 0 ? rec.NewCommits[^1].Split(' ')[0] : "-";
                var evidence = rec.GateSummary ?? "completed";
                foreach (var cpId in rec.NewlyDone)
                    db.UpdateCheckpoint(state.RunId, cpId, "DONE", commit, evidence);
            }

            // F1.3: persist the handoff block if present in the tracker (read after session)
            var track = ReadTrackerSafe();
            if (!string.IsNullOrWhiteSpace(track.HandoffBlock))
                db.WriteHandover(state.RunId, rec.Number, rec.Stage, track.HandoffBlock);

            // F1.2: regenerate the tracker FROM run.db after every session
            RegenerateTracker(track);
        }

        // F8.2: session-end one-liner with score pushed to Telegram
        _ = telegram.PushSessionEndAsync(rec.Number, rec.Stage, rec.Outcome?.ToString() ?? "Unknown",
            rec.GateSummary, rec.ResultSummary, rec.CostUsd, state.PendingFix?.VerifierScore);
    }

    private void SaveAndReport()
    {
        Save();
        TrackerSnapshot track;
        // Report render tolerates a transient tracker read failure (→ empty snapshot); the main loop's
        // authoritative read is what escalates a broken tracker to the human.
        try { track = _progress.Read(plan, CancellationToken.None); }
        catch (Exception) { track = new TrackerSnapshot(); }
        Reporter.WriteAndPublish(plan, state, track, Ctx.LastGates, Log);
        PushIdleSnapshot();
    }

    // ---------------------------------------------------------------- logging

    private void Log(string line)
    {
        Log(line, null);
    }

    private void Log(string line, string? outcome)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        // Legacy plain log (.conductor/conductor.log) kept additively for humans/back-compat; the
        // structured Serilog sink under .conductor/logs/ is the authoritative record.
        try { File.AppendAllText(_logPath, stamped + Environment.NewLine); }
        catch (IOException) { /* plain log is best-effort; the structured log below still records it */ }
        catch (UnauthorizedAccessException) { /* ditto — never let narration I/O break the run */ }
        var prev = _outcome;
        _outcome = outcome;
        try
        {
            using (BeginCorrelationScope())
                logger.LogInformation("{ConductorMessage}", line);
        }
        finally { _outcome = prev; }
        sink.Log(stamped);
    }

    private void LogWithOutcome(string line, string? outcome) => Log(line, outcome);

    /// <summary>Pushes the current runId/sessionId/stage/gate as a logging scope so every structured
    /// line is correlated; absent values are omitted (they render empty in the sink template).</summary>
    private IDisposable? BeginCorrelationScope()
    {
        var scope = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(state.RunId)) scope["runId"] = state.RunId;
        if (state.SessionCounter > 0) scope["sessionId"] = state.SessionCounter.ToString();
        if (!string.IsNullOrEmpty(state.CurrentStage)) scope["stage"] = state.CurrentStage;
        if (_curGate != null) scope["gate"] = _curGate;
        if (_outcome != null) scope["outcome"] = _outcome;
        return scope.Count > 0 ? logger.BeginScope(scope) : null;
    }

    // ---------------------------------------------------------------- notifications

    private void Notify(string message)
    {
        // B6: push to Telegram (fire-and-forget — the hosted service owns its own queue).
        _ = telegram.PushAsync(message);
        // B6.4: fire webhook notifications (generic/Discord/Slack).
        webhooks.FireAsync(message);

        var n = plan.Notify;
        if (n == null || string.IsNullOrWhiteSpace(n.Command)) return;
        try
        {
            var args = n.Args.Select(a => a.Replace("{message}", message));
            ProcessRunner.Run(n.Command, args, plan.Repo, TimeSpan.FromMinutes(1));
        }
        catch (Exception ex) { Log($"notify failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- process lock

    private bool AcquireLock()
    {
        try
        {
            if (File.Exists(_lockPath))
            {
                var pidText = File.ReadAllText(_lockPath).Trim();
                if (int.TryParse(pidText, out var pid))
                {
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
                        if (!p.HasExited)
                        {
                            sink.Log($"another conductor (pid {pid}) is already running this plan — exiting");
                            return false;
                        }
                    }
                    catch (ArgumentException) { /* stale lock — process gone */ }
                }
            }
            File.WriteAllText(_lockPath, Environment.ProcessId.ToString());
            return true;
        }
        catch (Exception ex)
        {
            sink.Log($"could not acquire lock: {ex.Message}");
            return false;
        }
    }

    private void ReleaseLock()
    {
        // Best-effort unlock on shutdown: if the lock file is already gone or transiently locked, a
        // stale entry is reclaimed on the next start by the pid-liveness check in AcquireLock.
        try { if (File.Exists(_lockPath)) File.Delete(_lockPath); }
        catch (IOException) { /* reclaimed next start via pid-liveness */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    // ---------------------------------------------------------------- state dir

    private void EnsureStateDirGitignore()
    {
        var gi = Path.Combine(plan.StateDir, ".gitignore");
        if (!File.Exists(gi))
            File.WriteAllText(gi, "*\n!.gitignore\n!REPORT.md\n");
    }

    // ---------------------------------------------------------------- F1.2 tracker-as-view helpers

    /// <summary>Seed checkpoints from the existing tracker markdown into run.db.
    /// Idempotent — re-seeding preserves status already set by <see cref="RunDb.UpdateCheckpoint"/>.</summary>
    private void SeedCheckpointsFromTracker()
    {
        if (_runDb is not { } db) return;
        var track = ReadTrackerSafe();
        if (track.Checkpoints.Count == 0) return;

        var cps = track.Checkpoints.Select(c => (c.Id, c.StageId, c.Title,
            c.IsDone ? "DONE" : c.IsInProgress ? "IN PROGRESS" : c.IsBlocked ? "BLOCKED" : "TODO",
            c.Commit, c.Evidence));
        db.SeedCheckpoints(state.RunId, cps);
        Log($"seeded {track.Checkpoints.Count} checkpoints from tracker into run.db");
    }

    /// <summary>Read the tracker file without throwing. Returns an empty snapshot on any error
    /// (file not found, locked, permission denied, parse failure, etc.). The authoritative
    /// read in the main loop escalates a genuinely broken tracker; this helper is defensive.</summary>
    private TrackerSnapshot ReadTrackerSafe()
    {
        try { return _progress.Read(plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

    /// <summary>Regenerate TRACKER.md from run.db. Uses the current tracker's handoff as the fallback
    /// (preserves the handoff block the agent last wrote if no DB handover exists yet).</summary>
    private void RegenerateTracker(TrackerSnapshot currentTrack)
    {
        if (_runDb is not { } db) return;
        try
        {
            TrackerGenerator.Write(plan, db, state.RunId, currentTrack.HandoffBlock);
        }
        catch (Exception ex)
        {
            Log($"tracker regeneration failed: {ex.Message}");
        }
    }
}
