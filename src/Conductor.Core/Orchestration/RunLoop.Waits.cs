using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class RunLoop
{
    // ---------------------------------------------------------------- session-boundary waits
    //
    // The timers that hold the loop at the session boundary without changing WHAT runs next: the
    // agent-declared wait (persisted), the failure and stall backoffs and the DNS park (in-process).
    // Split out of RunAsync at KS3.4 round 3 when the loop crossed the 500-line ceiling — same
    // blocks, same order, same messages; `return true` here is `continue` there. Never called in
    // dry-run mode: the caller hoists the guard the four blocks used to carry each.

    /// <summary>True while a timer still holds the loop at the session boundary — the caller goes
    /// around. False once every timer has expired (expired timers are cleared on the way through,
    /// exactly as the inline blocks cleared them).</summary>
    private async Task<bool> HeldAtSessionBoundaryAsync(CancellationToken ct)
    {
        // SC5.1: the wait an agent declared. It lives in RunState, not in a field of this
        // process, so an engine restarted mid-wait resumes the wait rather than paying for
        // a session that would only re-derive the same timestamp. Clearing it here is the
        // "respawn once": the next turn of the loop spawns exactly one session.
        if (_ctx.State.BlockedUntilUtc is { } blockedUntil)
        {
            if (DateTime.UtcNow < blockedUntil)
            {
                PushIdleSnapshot();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                return true;
            }
            var waited = _ctx.State.BlockedSinceUtc is { } since
                ? $" after {(DateTime.UtcNow - since).TotalMinutes:0.#}m asleep" : "";
            _ctx.Log($"blocked-until window opened ({blockedUntil:HH:mm:ss}Z){waited} — resuming: {_ctx.State.BlockedReason}");
            _ctx.State.BlockedUntilUtc = null;
            _ctx.State.BlockedReason = null;
            _ctx.State.BlockedSinceUtc = null;
            if (_ctx.State.Status == RunStatus.Waiting) _ctx.State.Status = RunStatus.Idle;
            _ctx.Save();
        }

        if (_ctx.BackoffUntil is { } until)
        {
            if (DateTime.UtcNow < until) { PushIdleSnapshot(); await Task.Delay(1000, ct).ConfigureAwait(false); return true; }
            _ctx.BackoffUntil = null;
            _ctx.State.Status = RunStatus.Idle;
            _ctx.Log("backoff over — resuming");
        }

        if (_ctx.StallBackoffUntil is { } sbUntil)
        {
            if (DateTime.UtcNow < sbUntil)
            {
                PushIdleSnapshot();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                return true;
            }
            _ctx.StallBackoffUntil = null;
            _ctx.Log("stall backoff over — resuming");
        }

        if (_ctx.DnsParkedUntil is { } dpUntil)
        {
            if (DateTime.UtcNow < dpUntil)
            {
                PushIdleSnapshot();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                return true;
            }
            _ctx.DnsParkedUntil = null;
            // KS5.4: through PreflightAsync, so the ceiling this recheck compares against is the one
            // an approval has actually raised — see RunLoop.Budget.cs.
            var recheckResults = await PreflightAsync(_ctx).ConfigureAwait(false);
            if (PreflightHealth.AllPassed(recheckResults))
            {
                _ctx.PreflightConsecutiveFailures = 0;
                _ctx.Log("preflight recovered — resuming session");
            }
            else
            {
                _ctx.PreflightConsecutiveFailures++;
                var backoff = PreflightHealth.ComputeBackoff(
                    _ctx.PreflightConsecutiveFailures,
                    _ctx.Plan.Limits.DnsHealthCheck?.IntervalSeconds ?? 60,
                    _ctx.Plan.Limits.DnsHealthCheck?.BackoffMultiplier ?? 2.0,
                    _ctx.Plan.Limits.DnsHealthCheck?.MaxBackoffSeconds ?? 3600);
                _ctx.DnsParkedUntil = DateTime.UtcNow.AddSeconds(backoff);
                NotifyPreflightPark(_ctx.PreflightConsecutiveFailures, backoff, "still failing");
                PushIdleSnapshot();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }
}
