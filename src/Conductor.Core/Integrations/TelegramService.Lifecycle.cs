using Conductor.Core.Courier;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>SC1.3: the service's LIFECYCLE — start, stop, and the live reload that makes a token or a
/// telegram block arriving mid-run take effect on the running engine. Split out of TelegramService.cs
/// when the reload pushed that file past the architecture ratchet's line ceiling; the split is by
/// responsibility, not by line count: everything here answers "is this service running, and on what?",
/// and nothing here formats or sends a message.</summary>
public sealed partial class TelegramService
{
    /// <summary>SC1.2: logs on BOTH outcomes. The silent early return is the exact shape of the bug
    /// SC1.1 fixed — a process that has decided to deliver nothing for the rest of the run, and says
    /// so nowhere. Not-started names the missing half in doctor's own words; started names the poll
    /// interval and how many chat ids it will actually reach, because "started" with an empty
    /// allowedChatIds is push-only to nobody and would otherwise read as success.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { StartCore(); }
        finally { _gate.Release(); }
    }

    /// <summary>The start itself. Caller holds <see cref="_gate"/>. Idempotent: starting an already
    /// started service is a no-op rather than a second pair of loops.</summary>
    private void StartCore()
    {
        if (_started) return;

        // DV4.3 / findings 6.9: the token handover. Decided BEFORE IsConfigured, because the
        // destination state is a machine where the courier holds the token and this run has none -
        // and a run that bailed out at "no token" there would never push at all, having handed its
        // phone to a process that is holding it perfectly well.
        _pollingRefusedBy = CourierPrecedence.PollingRefusal(CourierStateHome);
        if (_pollingRefusedBy is { Length: > 0 } && _cfg is { } block && block.ChatCount > 0)
        {
            StartThroughCourier(block);
            return;
        }

        _pollingRefusedBy = null;
        if (!IsConfigured)
        {
            var missing = TelegramReadiness.MissingHalf(
                hasBlock: _cfg is not null, hasToken: _token is not null,
                allowedChatIds: _cfg?.ChatCount ?? 0, started: false);
            // No telegram block at all is an ordinary, deliberate choice; a block that cannot deliver
            // is a misconfiguration the owner meant to work, and warrants the louder level.
            if (_cfg is null) _log.LogInformation("Telegram not started: {Reason}", missing);
            else _log.LogWarning("Telegram not started: {Reason}", missing);
            return;
        }

        // A stopped service has a completed queue and a cancelled token; both are per-run-of-the-loops,
        // not per-object, or a reload could only ever stop Telegram and never bring it back.
        _cts = new CancellationTokenSource();
        _sendQueue = NewSendQueue();
        var queue = _sendQueue;
        var token = _cts.Token;

        _started = true;
        _pollTask = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
        _sendTask = Task.Run(() => SendLoopAsync(queue, token), CancellationToken.None);

        // DV2.3, bug #64: this counted AllowedChatIds — the RAW allow-list — while every other surface
        // counts TelegramConfig.ChatCount, whose own doc comment names exactly this misreport as its
        // reason to exist. On a plan that declares its chats the KS11.2 way (a `chats` block, no
        // `allowedChatIds`) the raw list is empty and the resolved set is not, so the engine announced
        // at startup that it would deliver nothing while delivering perfectly, and /telegram/status
        // flatly contradicted the log line an operator had already read and believed.
        var chatIds = _cfg!.ChatCount;
        if (chatIds == 0)
            _log.LogWarning("Telegram bot started (poll interval {Interval}s) but will deliver nothing: {Reason}",
                _cfg.PollIntervalSeconds, TelegramReadiness.NoChatIds);
        else
            _log.LogInformation("Telegram bot started (poll interval {Interval}s, {ChatIds} allowed chat id(s))",
                _cfg.PollIntervalSeconds, chatIds);
    }

    /// <summary>Courier mode: neither loop runs, because both belong to the token and the courier
    /// owns it. The poll loop would fight it for updates (the 409 above); the send loop would push
    /// straight to the Bot API, and 6.9 is explicit that the run pushes THROUGH the courier or not
    /// at all - two writers on one bot is how a chat ends up with a run's message stamped by nothing
    /// and a courier's reply threaded under it.
    ///
    /// <para>The service still reports <c>IsLive</c>, and that is deliberate: composition, profiles
    /// and the evidence browser must go on producing messages, or a courier that comes back finds a
    /// run that stopped talking. Where those messages go is <see cref="CourierChannel"/>'s problem,
    /// and whether they arrived is DV1.1's.</para></summary>
    private void StartThroughCourier(TelegramConfig block)
    {
        _cts = new CancellationTokenSource();
        _sendQueue = NewSendQueue();
        _courier = new CourierChannel(
            ResolveTargets(block),
            CourierStateHome,
            origin: _plan.Name,
            log: m => _log.LogWarning("{Message}", m),
            stamp: _composer.Stamp);
        _started = true;

        _log.LogWarning("Telegram polling not started: {Reason}", _pollingRefusedBy);
        _log.LogInformation("Telegram pushes go through the courier ({ChatIds} chat id(s))", block.ChatCount);
    }

    /// <summary>
    /// SC1.3: re-resolve the token and the telegram block, and make the live service match — start it
    /// if it now can deliver, restart it if what it is running on changed, stop it if the block went
    /// away. This is what makes a token typed into the Face, or a telegram block added by a plan
    /// edit, take effect on the RUNNING engine; both were previously frozen into readonly fields at
    /// construction, so the only surface that could have been honest about them would have said
    /// "restart required" — and none of them did.
    /// </summary>
    /// <param name="freshPlan">The reloaded plan (from the run loop's session-boundary swap), or null
    /// to re-read only what lives outside the plan — which is where the bot token lives.</param>
    /// <returns>What actually happened, in one sentence a surface can print verbatim.</returns>
    internal async Task<TelegramReloadOutcome> ReloadAsync(PlanConfig? freshPlan = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldCfg = _cfg;
            var oldToken = _token;
            AdoptPlan(freshPlan ?? _plan);
            var changed = !string.Equals(oldToken, _token, StringComparison.Ordinal) || !SameBlock(oldCfg, _cfg);

            if (_started && !changed)
                return Outcome(false, "no change — the running engine is already using these settings");

            // Restart rather than mutate in place: the poll loop holds the interval and the API base
            // it started with, and the queue is keyed to the token that will be used to send.
            if (_started) await StopCoreAsync().ConfigureAwait(false);
            StartCore();
            return Outcome(changed, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The run loop's session-boundary plan swap, which must never be able to fail a run:
    /// a reload that throws is logged and the previous state stands.</summary>
    internal async Task ApplyPlanAsync(PlanConfig freshPlan, CancellationToken ct = default)
    {
        try
        {
            var outcome = await ReloadAsync(freshPlan, ct).ConfigureAwait(false);
            if (outcome.Changed) _log.LogInformation("Telegram reloaded with the swapped plan: {Message}", outcome.Message);

            // KS11.3 / CH-4: a chat ADDED by this reload is the case the onboarding message exists
            // for - it starts receiving session-end pushes mid-run with no frame at all. The call is
            // idempotent per chat, so the ones that were already here hear nothing.
            await _surface.PushOnboardingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Telegram could not apply the reloaded plan — it is still running on the previous configuration");
        }
    }

    /// <summary>Says what the service will do NOW, from the same helper doctor and /telegram/status
    /// use, so a reload cannot invent its own vocabulary for a state those two already name.</summary>
    private TelegramReloadOutcome Outcome(bool changed, string? overrideMessage)
    {
        var missing = TelegramReadiness.MissingHalf(
            hasBlock: _cfg is not null, hasToken: _token is not null,
            allowedChatIds: _cfg?.ChatCount ?? 0, started: _started);
        var message = overrideMessage ?? (missing is null
            ? $"the running engine picked it up — Telegram is delivering to {_cfg!.ChatCount} chat id(s) now, no restart needed"
            : $"saved, but this run still will not deliver: {missing}");
        return new TelegramReloadOutcome(changed, _started, missing is null, message);
    }

    /// <summary>Everything a running poll/send loop was built from. Deliberately not a fingerprint
    /// string: a string holding the bot token is one careless log line away from leaking it.</summary>
    private static bool SameBlock(TelegramConfig? a, TelegramConfig? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        return a.PollIntervalSeconds == b.PollIntervalSeconds
            && a.EnableTwoWay == b.EnableTwoWay
            && string.Equals(a.ApiBaseUrl, b.ApiBaseUrl, StringComparison.Ordinal)
            && a.AllowedChatIds.SequenceEqual(b.AllowedChatIds, StringComparer.Ordinal)
            // KS11.2: the chats block is part of what the loops were built from. Comparing only the
            // old list would let an edit that adds an observer chat - or DEMOTES an admin one - be
            // read as "nothing changed", and the running loops would keep the old permissions.
            && ResolveTargets(a).SequenceEqual(ResolveTargets(b));
    }

    /// <summary>SC1.1: how long the send queue is allowed to flush before shutdown stops waiting.
    /// The run's last act is a fire-and-forget session-end push, so cancelling the send loop the
    /// instant the loop exits is how "the push arrives" quietly degrades into "the push was queued".</summary>
    internal static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(10);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>The stop itself. Caller holds <see cref="_gate"/>. Leaves the object restartable —
    /// SC1.3 restarts it in place when a reloaded token or block changes what it should be running.
    /// Takes no CancellationToken by design: shutdown drains, and a caller's already-cancelled token
    /// would turn "flush the last push" into "drop it", which is the bug SC1.1 fixed.</summary>
    private async Task StopCoreAsync()
    {
        var poll = _pollTask;
        var send = _sendTask;
        if (poll == null && send == null) { _started = false; return; }

        // 1. Close the queue and let the send loop drain what is already in it. Nothing new can be
        //    enqueued after this (PushAsync uses TryWrite, which just returns false on a closed
        //    channel) so the drain always terminates.
        _sendQueue.Writer.TryComplete();
        if (send != null)
        {
            try { await send.WaitAsync(DrainGrace, CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _log.LogWarning("Telegram send queue did not drain within {Grace}s — some pushes were not delivered",
                    DrainGrace.TotalSeconds);
            }
            catch (OperationCanceledException) { /* already cancelled elsewhere — nothing left to flush */ }
        }

        // 2. Then stop the long-poll. Its loops end by cancellation, so the tasks complete in the
        //    Canceled state — awaiting them bare would rethrow and turn a clean exit into a crash.
        await _cts.CancelAsync().ConfigureAwait(false);
        _started = false;
        if (poll != null)
        {
            try { await poll.WaitAsync(DrainGrace, CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { /* a wedged long-poll must not hold the process open */ }
            catch (OperationCanceledException) { /* expected: the loop was cancelled, not failed */ }
        }

        _pollTask = null;
        _sendTask = null;
    }
}
