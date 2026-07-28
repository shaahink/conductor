using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>Control ingress + crash recovery for the run loop: polling the three command ingresses
/// (TUI queue, in-process inbox, control.json), dispatching them, and restoring interrupted state.
/// Split from RunLoop.cs to keep each partial under the architecture ratchet's line ceiling.</summary>
#pragma warning disable MA0045 // sync file I/O by design — fast local writes, not hot-path
public sealed partial class RunLoop
{
    // ---------------------------------------------------------------- control & plumbing

    internal async Task<ControlAction?> HandleControlAsync(bool inSession = false, CancellationToken ct = default)
    {
        var cmd = _ctx.Sink.PollControl() ?? PollInbox() ?? await ReadControlFileAsync(ct).ConfigureAwait(false);
        if (cmd is not { } c) return null;
        return await Dispatcher.DispatchAsync(c, inSession, ct).ConfigureAwait(false);
    }

    private ControlCommand? PollInbox() =>
        _ctx.ControlInbox != null && _ctx.ControlInbox.TryDequeue(out var c) ? c : null;

    private async Task<ControlCommand?> ReadControlFileAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_ctx.ControlPath)) return null;
            var writeTime = File.GetLastWriteTimeUtc(_ctx.ControlPath);
            if (_ctx.LastControlWrite == writeTime) return null;
            _ctx.LastControlWrite = writeTime;
            var text = await File.ReadAllTextAsync(_ctx.ControlPath, ct).ConfigureAwait(false);
            var parsed = ControlFile.Parse(text);
            if (parsed.Action == null) return null;
            if (parsed.Confirmed && parsed.IntentId != null)
                _ctx.Log($"control confirmed [intent={parsed.IntentId}]");
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private void DeleteControlFile()
    {
        try { if (File.Exists(_ctx.ControlPath)) File.Delete(_ctx.ControlPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _ctx.LastControlWrite = null;
    }

    /// <summary>A control.json left behind by a PREVIOUS process must never steer this one — the file
    /// ingress is same-run control, not cross-run scheduling. The 2026-07-17 dogfood hit exactly this:
    /// an abort written to stop run A survived A's death (mid-session aborts deliberately leave the
    /// file for re-processing) and instantly killed run B at startup — from the owner's terminal, a
    /// silent immediate exit. Purged once before the first poll, and loudly.</summary>
    private void PurgeStaleControlFile()
    {
        try
        {
            if (!File.Exists(_ctx.ControlPath)) return;
            var action = "unreadable";
            try { action = ControlFile.Parse(File.ReadAllText(_ctx.ControlPath)).Action?.ToString() ?? "no action"; }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
            var written = File.GetLastWriteTime(_ctx.ControlPath);
            File.Delete(_ctx.ControlPath);
            _ctx.Log($"stale control.json from a previous run ({action}, written {written:HH:mm:ss}) — removed, not executed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't delete it — at least make the poller's dedupe treat it as already seen.
            _ctx.LastControlWrite = File.Exists(_ctx.ControlPath) ? File.GetLastWriteTimeUtc(_ctx.ControlPath) : null;
            _ctx.Log($"stale control.json could not be removed ({ex.GetType().Name}) — it will be ignored this run");
        }
    }

    private void RecoverFromCrash()
    {
        var recovered = false;

        // An aborted run is stopped, not discarded: `conductor run` on it means "continue". Without
        // this reset the loop's first status check re-exits immediately — the same silent instant
        // death as a stale abort, but persisted (2026-07-17 dogfood).
        if (_ctx.State.Status == RunStatus.Aborted)
        {
            _ctx.State.Status = RunStatus.Idle;
            _ctx.Log("previous run ended aborted — continuing it (abort again with `conductor abort` if that was not the intent)");
            _ctx.Save();
        }

        if (_ctx.State.Status is RunStatus.Running or RunStatus.VerifyingGates or RunStatus.Backoff)
        {
            var last = _ctx.State.History.LastOrDefault();
            if (last != null && last.EndedUtc == null)
            {
                last.EndedUtc = DateTime.UtcNow;
                last.Outcome = SessionOutcome.Interrupted;
                _verdicts.QueueResume(last, "conductor crashed or was killed mid-session");
                _ctx.Log($"recovered: session #{last.Number} was interrupted — will resume its agent session");
                recovered = true;
            }
            _ctx.State.Status = RunStatus.Idle;
            _ctx.Save();
        }

        if (!recovered && _ctx.State.PendingResume == null)
        {
            if (_ctx.Store is { } store)
            {
                var interrupted = store.FindInterruptedSession(_ctx.State.RunId);
                if (interrupted != null)
                {
                    var rec = _ctx.State.History.FirstOrDefault(h => h.Number == interrupted.Number);
                    if (rec != null)
                    {
                        if (rec.EndedUtc == null) rec.EndedUtc = DateTime.UtcNow;
                        rec.Outcome = SessionOutcome.Interrupted;
                        _verdicts.QueueResume(rec, "event log shows interrupted session — recovering");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(interrupted.AgentSessionId))
                        {
                            _ctx.Log($"recovered from event log: session #{interrupted.Number} has no AgentSessionId — marking needs-attention (cannot resume without a session id)");
                            _ctx.State.Status = RunStatus.NeedsHuman;
                            _ctx.State.AttentionReason = $"Orphaned session #{interrupted.Number} in run.db has no AgentSessionId — manual review needed.";
                            _ctx.Save();
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
                            _ctx.State.History.Add(rec);
                            _verdicts.QueueResume(rec, "event log shows interrupted session — recovering from orphaned SessionStarted");
                        }
                    }
                    if (_ctx.State.Status != RunStatus.NeedsHuman)
                    {
                        _ctx.Log($"recovered from event log: session #{interrupted.Number} was interrupted — will resume");
                        _ctx.State.Status = RunStatus.Idle;
                        _ctx.Save();
                    }
                }

                var events = store.ReadAllEvents(_ctx.State.RunId);
                foreach (var evt in events)
                {
                    if (evt is TaskAdded ta)
                        _ctx.DecomposedCheckpoints.Add(ta.CheckpointId);
                }
            }
        }
    }

    private void WarnOnBranchPattern()
    {
        if (string.IsNullOrWhiteSpace(_ctx.Plan.BranchPattern)) return;
        var branch = Git.Branch(_ctx.Plan.Repo);
        if (!Regex.IsMatch(branch, _ctx.Plan.BranchPattern, RegexOptions.None, ProgressConventions.RegexTimeout))
            _ctx.Log($"⚠ branch '{branch}' does not match plan branchPattern '{_ctx.Plan.BranchPattern}' — check before letting sessions commit");
    }

    /// <summary>W3.3: an unbounded run is a policy choice, not a default anyone opted into. The
    /// U-series run had no cap and spent $139.68 before dying, so the run says so out loud at start
    /// (and `doctor` warns). Caps stay the owner's to set — nothing is invented here.</summary>
    private void WarnOnUnboundedSpend()
    {
        if (_ctx.Options.DryRun) return;
        if (_ctx.Plan.Limits.MaxRunCostUsd.HasValue || _ctx.Plan.Limits.MaxRunTokens.HasValue) return;
        _ctx.Log("⚠ no spend cap: limits.maxRunCostUsd and limits.maxRunTokens are both unset — " +
                 "this run can spend without bound (set one in the plan, or in the Face's Plan tab)");
    }

    /// <summary>W3.2: one ~$0.001 ping at run start so a run cannot begin on a dead credential.
    /// Once per process, never per session — the point is to fail before the first session's spend,
    /// not to re-bill the check all night. A failure parks for a human instead of starting: the
    /// U-series run began on a token that expired, and thirteen sessions later the symptom was a
    /// generic agent error.</summary>
    private async Task AuthPreflightAsync(CancellationToken ct)
    {
        if (_ctx.Options.DryRun || !_ctx.Plan.Limits.AuthPreflight) return;
        if (!AuthSmokeTest.CanProbe(_ctx.Plan.Agent)) return;

        var result = await AuthSmokeTest.RunAsync(_ctx.Plan, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        if (result.Passed)
        {
            _ctx.Log($"auth preflight: {result.Message}");
            return;
        }
        _ctx.Log($"auth preflight FAILED: {result.Message}");
        _verdicts.NeedsHuman($"auth preflight failed before session 1 — {result.Message}");
    }

    private void EnsureStateDirGitignore()
    {
        var gi = Path.Combine(_ctx.Plan.StateDir, ".gitignore");
        if (!File.Exists(gi))
            File.WriteAllText(gi, "*\n!.gitignore\n!REPORT.md\n");
    }
}
