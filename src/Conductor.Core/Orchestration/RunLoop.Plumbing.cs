using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Integrations.Messaging;
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

    // KS3.4: which stage runs next, whether one runs at all, and what kind of session it is are
    // StageSelection.NextAction's answers now — shared verbatim with `preflight`'s compose leg, so
    // the loop keeps no private vocabulary for the decision itself.

    private int MaxAttempts(StageConfig stage) => StageSelection.MaxAttempts(_ctx.Plan, stage);

    // ── per-stage overrides (M3.2) ──

    private void ApplyStageOverrides(StageConfig stage)
    {
        _ctx.State.SkipGatesThisStage = stage.Overrides?.SkipGates == true;
        _ctx.State.SkipCommitThisStage = stage.Overrides?.SkipCommit == true;
        // P2: the QA dial owns verification skipping when set (off → skip; everySession/phaseGate
        // → verify, superseding a stale overrides.skipVerification); absent → the override decides.
        _ctx.State.SkipVerificationThisStage = _ctx.Qa.EffectiveSkipVerification(_ctx.Plan, stage);
        if (stage.Overrides is { } o)
        {
            var flags = new List<string>();
            if (o.SkipGates == true) flags.Add("skip-gates");
            if (o.SkipCommit == true) flags.Add("skip-commit");
            if (o.SkipVerification == true) flags.Add("skip-verification");
            if (flags.Count > 0) _ctx.Log($"stage overrides: {string.Join(", ", flags)}");
        }
        if (DefaultQaPolicy.EffectiveRule(_ctx.Plan.Pipeline?.Qa, stage.Qa) is { } dial)
            _ctx.Log($"qa dial: {dial.Mode}{(dial.VerifierThreshold is { } t ? $" (threshold {t})" : "")}");
    }

    // The spend rail — the ceiling in force, the park that reaching it causes, and the un-park a raised
    // ceiling triggers — lives in RunLoop.Budget.cs (KS5.4).

    // ---------------------------------------------------------------- process lock

    // SC2.1: the lock is no longer only a mutex — it is the engine's liveness, read back by
    // `conductor status` so the verdict window stops looking like a crash. EngineLock owns both halves.
    private bool AcquireLock()
    {
        try
        {
            var holder = EngineLock.Read(_ctx.Plan.StateDir);
            if (holder != null && EngineLock.IsLive(holder))
            {
                _ctx.Sink.Log($"another conductor (pid {holder.Pid}) is already running this plan — exiting");
                return false;
            }
            // Nothing there, a pid the OS has forgotten, or an id since recycled: the lock is stale.
            EngineLock.Write(_ctx.Plan.StateDir);
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
        EngineLock.Delete(_ctx.Plan.StateDir);
        // SF5.4: a finished run must not leave its stage id sitting in a terminal tab — a stale title
        // reads as live, which is worse than no title at all.
        Core.Fleet.ProcessTitle.Restore();
    }

    // ---------------------------------------------------------------- save, report, snapshot

    private void SaveAndReport()
    {
        _ctx.Save();
        Reporter.WriteAndPublish(_ctx.Plan, _ctx.State, _ctx.ReadWork(), _ctx.LastGates, _ctx.Log, store: _ctx.Store,
            onNewOwnerItems: _ctx.NotifyNewOwnerQueueItems);
        PushIdleSnapshot();
    }

    private void PushIdleSnapshot() => _ctx.Sink.Snapshot(BaseSnapshot(_ctx.ReadWork()));

    private void PushSessionSnapshot(AgentSession agent, SessionRecord rec, StageConfig _, int attempt, int maxAttempts, TrackerSnapshot track)
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
            SatelliteCommits = rec.SatelliteCommits,
            NewlyDone = rec.NewlyDone,
            CostUsd = rec.CostUsd,
            TokensInput = rec.TokensInput,
            TokensOutput = rec.TokensOutput,
            TokensReasoning = rec.TokensReasoning,
            TokensCacheRead = rec.TokensCacheRead,
        });
        // W1.1: CheckpointConfirmed is emitted by the CONFIRM path (IRunStore.ConfirmCheckpoints,
        // called by the verdict engine after gates + verify evidence — M4.1), not here at Advanced.
        // The claim moment is already visible as the done-status TaskStatusChanged below.

        if (_ctx.Store is { } db)
        {
            db.RecordSession(_ctx.State.RunId, rec.Stage, rec.Number, rec.Kind.ToString(),
                rec.StartedUtc, rec.EndedUtc, rec.Outcome?.ToString(), rec.ClaudeSessionId,
                rec.ResumeCount, rec.Attempt, rec.GateSummary, rec.ResultSummary,
                rec.NewCommits.Count, rec.NewlyDone.Count > 0 ? string.Join(",", rec.NewlyDone) : null,
                rec.Digest.ToJson(),
                // K1.2: null when the session had no ceiling or never crossed the soft threshold —
                // deliberately not an empty object, which would read as "nudged, nothing happened".
                rec.SoftBreak is { } sb ? SoftBreak.ToJson(sb) : null,
                // K3.3: the build and the limits AS OF THIS SESSION. The run row carries the same
                // pair, but only the latest — limits are editable in flight and a resume can be a
                // different binary, so "which cap governed session 9" is only answerable per session.
                EngineStamp.Current.Full,
                RunLimitsSnapshot.From(_ctx.Plan.Limits).ToJson(),
                // K4.1: how full the window ran for this session, beside the limits that were meant to
                // govern it — the two only mean something read together.
                rec.Context);
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
                // SC4.3: a checkpoint delivered in a declared satellite has a commit — it is just not
                // in this repo. Recording "-" for it is how sk #3's delivered work read as nothing.
                var commit = rec.NewCommits.Count > 0 ? rec.NewCommits[^1].Split(' ')[0]
                    : SessionProgress.LastSatelliteCommitRef(rec) ?? "-";

                // SF0.2 (bug #10, the rider): `rec.GateSummary ?? "completed"` was dead code.
                // SessionRecord.GateSummary is a NON-NULLABLE string that defaults to "", so `??`
                // could never fire — and it is still "" on every session kind that returns before the
                // battery runs, which is precisely the verify and audit sessions this change just
                // taught to carry claims. Worse on the delivery path, where it holds a real battery
                // token and OVERWROTE the artifact path the agent claimed with
                // `task --done --evidence <path>` — the one field a reviewer reads, replaced by
                // "engine-fast:OK". Precedence: the agent's own evidence wins, the battery summary
                // stands in when the claim carried none, "completed" is the last resort.
                var battery = string.IsNullOrWhiteSpace(rec.GateSummary) ? "completed" : rec.GateSummary;
                var claimed = db.GetCheckpoints(_ctx.State.RunId)
                    .ToDictionary(c => c.Id, c => c.Evidence, StringComparer.OrdinalIgnoreCase);
                foreach (var cpId in rec.NewlyDone)
                {
                    var evidence = claimed.TryGetValue(cpId, out var agentEvidence)
                        && !string.IsNullOrWhiteSpace(agentEvidence) && agentEvidence != "-"
                        ? agentEvidence
                        : battery;
                    db.UpdateCheckpoint(_ctx.State.RunId, cpId, "DONE", commit, evidence, source: "engine");
                }
            }

            var track = ReadTrackerSafe();
            if (!string.IsNullOrWhiteSpace(track.HandoffBlock))
                db.WriteHandover(_ctx.State.RunId, rec.Number, rec.Stage, track.HandoffBlock);

            RegenerateTracker(track);
        }

        WriteSessionHistory(rec);

        // K5.2: the record goes over WHOLE — including what a rollover landed, which K1.1 records
        // and nothing rendered. The notifier bounds it once (a Verify ResultSummary can run to
        // several KB of verdict JSON); cutting it here as well is how the same paragraph came to be
        // cut twice.
        _ = _ctx.Messenger.PushSessionEndAsync(new SessionEndPush(
            rec.Number, rec.Stage, rec.Outcome?.ToString() ?? "Unknown", rec.GateSummary,
            rec.ResultSummary, rec.CostUsd, _ctx.State.PendingFix?.VerifierScore,
            SessionProgress.WorkCommits(rec).Count, rec.NewlyDone,
            rec.Outcome == SessionOutcome.RolledOver,
            // K5.4: the shas themselves, so the landed line can be links, and the session's own
            // wall-clock, which the record has always carried and no message ever rendered.
            SessionProgress.WorkCommits(rec),
            rec.EndedUtc is { } ended ? ended - rec.StartedUtc : null));

        // KS9.2: the session boundary is the mirror's main beat — one pass, after the checkpoint
        // writes and the tracker regeneration above, so the board it computes is the board the
        // tracker just showed. Fire-and-forget through RunContext: it cannot throw and cannot block.
        _ctx.MirrorBoard($"session {rec.Number} end");

        // DV6.3: and the board goes OUT as a page. Same beat and the same reason as the mirror above
        // — after the checkpoint writes and the tracker regeneration, so the page shows the board the
        // tracker just showed — but with no inbound anything: it is a file, pushed, not a view served.
        var boardTrack = _ctx.ReadWork();
        _ctx.PublishBoard($"session {rec.Number} end", boardTrack, BaseSnapshot(boardTrack));
    }


    // ---------------------------------------------------------------- notifications

    /// <summary>FU-OWNER-11 — the run says, once, which machine it is running on and which engine
    /// build is driving it. The session-level identity rides every message (see
    /// <c>TelegramService.IdentityLine</c>), but repo and build are run-scoped facts that would
    /// otherwise never reach the chat at all: an owner reading a notification hours later could not
    /// tell which checkout it came from, and could not date it to a binary. The version comes from
    /// <see cref="BuildInfo.Current"/> — the assembly's own stamp, FU-OWNER-10's field — never from a
    /// hand-maintained constant, which is exactly how a hand-typed message once quoted a version the
    /// engine had already replaced.</summary>
    private void NotifyRunStart()
    {
        // KS11.3 / CHAPAR CH-4: the rules first, then the news. A chat that has not been told what
        // this run is, what will arrive and what it may ask cannot read anything that follows —
        // and the very next line is the first thing that follows.
        if (_ctx.Notifier.AllowOneOff()) _ = _ctx.Messenger.PushOnboardingAsync();

        var verb = _ctx.State.SessionCounter > 0 ? "resumed" : "started";
        Notify($"Conductor {_ctx.Plan.Name}: run {verb} — repo {_ctx.Plan.Repo} " +
               $"(branch {Git.Branch(_ctx.Plan.Repo)}) · engine {BuildInfo.Current.Full}");
    }

    // ---------------------------------------------------------------- KS9.2 the live mirror

    /// <summary>Attach the run's GitHub mirror, once, at run start — and push immediately, so a run
    /// that is resumed after a process died with an unpushed tail catches up before its first session
    /// rather than at the end of it. Null when the plan has not opted in, which is the default.</summary>
    private void AttachBoardMirror()
    {
        if (_ctx.Store is not { } db) return;
        // The dance is CA2000's own prescription for a transfer of dispose ownership: create into a
        // local, hand it over, null the local so the finally cannot double-dispose what the context
        // now owns. RunContext disposes it in RunLoop's finally.
        GithubMirror? mirror = null;
        try
        {
            mirror = GithubMirror.TryCreate(_ctx.Plan, db, _ctx.State.RunId, _ctx.Log);
            _ctx.AttachMirror(mirror);
            mirror = null;
        }
        finally { mirror?.Dispose(); }
        _ctx.MirrorBoard("run start");
    }

    private void Notify(string message, PushSeverity severity = PushSeverity.Quiet)
    {
        if (!_ctx.Notifier.AllowOneOff()) return;   // KS2.6: a dry run reaches nobody, on every leg
        _ = _ctx.Messenger.PushAsync(message, severity);
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

    /// <summary>W1.2: run-start boundary of the ONE plan→graph sync (adds, title refreshes,
    /// retire-as-archived, revives, zero-item-stage scaffolds — upsert-never-clobber). The tracker
    /// was the sync's input moments ago, so it is not regenerated here; the view catches up at the
    /// next mutation boundary or session end. The G4 restart split-brain (two seeds disagreeing) is
    /// structurally gone: this is the only seed.</summary>
    private void SyncWorkGraphFromDeclared()
    {
        if (_ctx.Store is not { } db) return;
        WorkGraphSync.Sync(_ctx.Plan, db, _ctx.State.RunId, _ctx.Log, regenerateTracker: false);
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

            // transcript.md — human-readable rendering of the raw agent stream (logs/session-NNN.jsonl).
            // The design doc (M2.4) lists transcript.md alongside prompt/verdict/handover/cost; the raw
            // stream is captured as NDJSON, so we fold it into readable markdown here rather than ship the
            // wire format into the history dir.
            var rawPath = Path.Combine(_ctx.Plan.StateDir, "logs", $"session-{rec.Number:000}.jsonl");
            if (File.Exists(rawPath))
                File.WriteAllText(Path.Combine(sessionDir, "transcript.md"), RenderTranscript(rawPath, rec));

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
                // K4.1: the four numbers above are integrals; these three say how full the window ran.
                // Null on a session no provider instrumented, and absent from the artifact rather than
                // recorded as zero.
                contextHighWater = rec.Context?.HighWaterTokens,
                contextMeanTurn = rec.Context?.MeanTurnTokens,
                contextTurns = rec.Context?.Turns,
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

    /// <summary>Fold the raw agent NDJSON stream (logs/session-NNN.jsonl) into readable markdown for
    /// the session-history dir. Best-effort and provider-shaped like the opencode/claude wire vocab
    /// (text / tool_use / error); any line we cannot parse is preserved verbatim in a code fence so a
    /// new provider format never silently drops content.</summary>
    internal static string RenderTranscript(string rawJsonlPath, SessionRecord rec)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Session {rec.Number:000} — {rec.Stage} — {rec.Kind}");
        sb.AppendLine();

        string[] lines;
        try { lines = File.ReadAllLines(rawJsonlPath); }
        catch (IOException) { return sb.ToString(); }

        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿'); // strip a UTF-8 BOM on the first line
            if (line.Length == 0) continue;

            JsonElement part = default;
            string? type;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                type = root.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (root.TryGetProperty("part", out var p) && p.ValueKind == JsonValueKind.Object)
                    part = p.Clone();
            }
            catch (JsonException)
            {
                sb.AppendLine("```"); sb.AppendLine(line); sb.AppendLine("```"); sb.AppendLine();
                continue;
            }

            switch (type)
            {
                case "text":
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var txt)
                        && txt.GetString() is { Length: > 0 } s)
                    { sb.AppendLine(s); sb.AppendLine(); }
                    break;
                case "tool_use":
                    var tool = part.ValueKind == JsonValueKind.Object && part.TryGetProperty("tool", out var tl)
                        ? tl.GetString() : "tool";
                    sb.AppendLine($"- **{tool}** — {TranscriptTitle(part)}");
                    break;
                case "error":
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var et))
                        sb.AppendLine($"> **error:** {et.GetString()}");
                    sb.AppendLine();
                    break;
                // step_start / step_finish are cost/token bookkeeping — recorded in cost.json, not here.
            }
        }
        return sb.ToString();
    }

    private static string TranscriptTitle(JsonElement part) =>
        part.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
            && st.TryGetProperty("title", out var title)
            ? title.GetString() ?? "" : "";
}
