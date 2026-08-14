using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
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
        // KS3.4 round 4: the state-only transitions (aborted-continue; a crash's persisted status
        // becoming a queued resume) are CrashRecovery.Apply's — shared verbatim with `preflight`'s
        // compose leg, so a hard-killed engine drills as the Resume this loop will actually compose.
        // This method keeps the side effects (log, save) and the store-backed orphan recovery below,
        // which needs a live store.
        var recovery = CrashRecovery.Apply(_ctx.State);

        // An aborted run is stopped, not discarded: `conductor run` on it means "continue". Without
        // this reset the loop's first status check re-exits immediately — the same silent instant
        // death as a stale abort, but persisted (2026-07-17 dogfood).
        if (recovery.ContinuedAborted)
        {
            _ctx.Log("previous run ended aborted — continuing it (abort again with `conductor abort` if that was not the intent)");
            _ctx.Save();
        }

        if (recovery.LiftedCrashStatus)
        {
            if (recovery.Interrupted is { } cut)
                _ctx.Log($"recovered: session #{cut.Number} was interrupted — will resume its agent session");
            _ctx.Save();
        }

        if (recovery.Interrupted is null && _ctx.State.PendingResume == null)
        {
            if (_ctx.Store is { } store)
            {
                // KS3.4 round 5: the transitions live in CrashRecovery.ApplyOrphan — shared with
                // `preflight`'s compose leg, which reads the same run.db read-only — so an orphaned
                // SessionStarted drills as the Resume (or the NeedsHuman park) this loop enacts.
                // This method keeps only the side effects: logging and saving.
                var orphan = CrashRecovery.ApplyOrphan(_ctx.State, store);
                if (orphan.ParkedOrphanNumber is { } unresumable)
                {
                    _ctx.Log($"recovered from event log: session #{unresumable} has no AgentSessionId — marking needs-attention (cannot resume without a session id)");
                    _ctx.Save();
                }
                else if (orphan.Resumed is { } rec && _ctx.State.Status != RunStatus.NeedsHuman)
                {
                    _ctx.Log($"recovered from event log: session #{rec.Number} was interrupted — will resume");
                    _ctx.Save();
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

    /// <summary>SF0.1 / FU-OWNER-12: say once, at run start, whether this run's pushes can reach a
    /// phone at all. The verdict was already computed and already good — <c>doctor</c> warns it and
    /// <c>GET /telegram/status</c> answers it in the identical words — but both only answer when
    /// ASKED, and a run that is never asked logs nothing: <c>grep -ci telegram .conductor/conductor.log</c>
    /// returned 0 on a live run. So an operator watching a silent chat could not tell "nothing has
    /// happened yet" from "nothing can ever be delivered". Logged next to the start line, from
    /// <see cref="Conductor.Core.Integrations.TelegramReadiness"/>, so all three surfaces stay in one
    /// voice (SC1.2).</summary>
    private void LogNotificationReadiness()
    {
        _ctx.Log(_ctx.Telegram.DeliveryBlocker is { } blocker
            ? $"notifications: telegram will NOT deliver — {blocker}"
            : "notifications: telegram will deliver this run's pushes");
    }

    /// <summary>K3.3: say at launch when the engine driving this run was built from a dirty tree.
    /// A client-site run executed on <c>0.2.3-alpha…dirty</c> and nobody knew until the machine was
    /// asked afterwards — the binary claims a version that no commit can reproduce, so every verdict
    /// the run produces is unattributable. The run is NOT blocked (a working-tree build is the normal
    /// way this repo tests itself); it is only recorded, in the log and in the run row.</summary>
    private void WarnOnDirtyEngine()
    {
        var engine = EngineStamp.Current;
        if (!engine.Dirty) return;
        _ctx.Log($"⚠ dirty engine: this run is driven by {engine.Full} — built from a working tree with " +
                 "uncommitted changes, so its commit does not reproduce this binary");
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

        // KS5.2: the probe's own bill, against session 0 — the run has not started one yet, and a row
        // keyed to a session that does not exist is still a row, which is what the ledger is for.
        var result = await AuthSmokeTest.RunAsync(_ctx.Plan, TimeSpan.FromSeconds(45), ct,
            onSpend: r => _ctx.Ledger.Record(r, _ctx.State.SessionCounter, "auth preflight probe")).ConfigureAwait(false);
        if (result.Passed)
        {
            _ctx.Log($"auth preflight: {result.Message}");
            return;
        }
        _ctx.Log($"auth preflight FAILED: {result.Message}");
        _verdicts.NeedsHuman($"auth preflight failed before session 1 — {result.Message}");
    }

    /// <summary>G3.1 `run --paused`: park the run before the first session so the operator can author
    /// the plan / pre-seed the kanban with the control plane up. Pure so the flag→status wiring is
    /// unit-testable. Never masks a state that needs attention (NeedsHuman/Aborted keep their reason),
    /// and dry runs ignore it (nothing spawns anyway).</summary>
    internal static bool ApplyStartPause(RunState state, RunOptions opts)
    {
        if (!opts.StartPaused || opts.DryRun) return false;
        if (state.Status is RunStatus.NeedsHuman or RunStatus.Aborted) return false;
        state.Status = RunStatus.Paused;
        return true;
    }

    private void EnsureStateDirGitignore()
    {
        var gi = Path.Combine(_ctx.Plan.StateDir, ".gitignore");
        if (!File.Exists(gi))
            File.WriteAllText(gi, "*\n!.gitignore\n!REPORT.md\n");
    }

    /// <summary>KS2.6: what a <c>--dry-run</c> says when it walks into a park. A preview never waits,
    /// so it reports the park and stops rather than spinning the loop against an unchanged fact —
    /// which is how one handoff mentioning the escalation token produced roughly two hundred phone
    /// notifications. The sentence carries the reason, because "parked" without it is the same
    /// unanswerable message the flood was made of.</summary>
    private void LogDryRunPark()
    {
        var reason = _ctx.State.AttentionReason is { Length: > 0 } r ? r : "no reason recorded";
        _ctx.Sink.Log($"--- DRY RUN: this run is parked at {_ctx.State.Status} — {reason}. " +
                      "Nothing would be spawned until it is cleared (`conductor resume` / `conductor approve`) ---");
        _ctx.Log($"dry run: parked at {_ctx.State.Status} — {reason}; previewing no further");
    }

    /// <summary>KS2.6: a preflight backoff park SAYS it is parked, and for how long.
    /// <para>The DNS/preflight branch only ever logged. A transient network cut therefore produced a
    /// silent multi-hour park — the observed case was fourteen hours of doubling backoff with nothing
    /// on the owner's phone — because the backoff maxes at an hour and every re-check after the first
    /// wrote a log line nobody was watching. The push fires per ESCALATION rather than once at the
    /// start: the incident key carries the consecutive-failure count, so each new, longer park is a
    /// new incident and each repeat inside one is suppressed by <see cref="ParkNotifier"/>.</para></summary>
    private void NotifyPreflightPark(int consecutiveFailures, int backoffSeconds, string detail)
    {
        _ctx.Log($"preflight FAILED (×{consecutiveFailures}): {detail} — parking {backoffSeconds}s");
        if (!_ctx.Notifier.Admit(nameof(RunStatus.Waiting), $"preflight x{consecutiveFailures}")) return;
        var window = backoffSeconds >= 120
            ? FormattableString.Invariant($"{backoffSeconds / 60.0:0.#} minutes")
            : FormattableString.Invariant($"{backoffSeconds} seconds");
        Notify($"Conductor {_ctx.Plan.Name}: preflight failed (×{consecutiveFailures}) — {detail}. " +
               $"PARKED, backing off {window} before the next check.", PushSeverity.Alert);
        _ = _ctx.Telegram.PushWithKeyboardAsync(
            $"Conductor {_ctx.Plan.Name}: parked on preflight — {detail} (backing off {window})",
            [("Resume", "resume"), ("Skip", "skip")], CancellationToken.None);
    }
}
