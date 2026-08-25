using System.Globalization;
using System.Net.Http.Json;
using System.Threading.Channels;

using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Integrations;

/// <summary>The Test button's leg of the service: prove the token against getMe, then send a real
/// message down the REAL send queue and wait to hear that it landed.
///
/// <para>Its own file because it is the only part of this service that exists to be WRONG loudly —
/// SC1.1 shipped a Test button that reported success for the entire life of a feature that delivered
/// nothing, because it sent down a parallel path. Everything here is about not being that again, and
/// none of it sits on the path a run push actually takes.</para></summary>
public sealed partial class TelegramService
{
    /// <summary>SC1.2: how long the queue-routed test waits for the send loop to report back before
    /// it gives up and says so. A wedged send loop must not hold an HTTP request open forever.</summary>
    internal static readonly TimeSpan TestAckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Validates the configured token against Telegram's getMe, then sends a real test push.
    /// SC1.2: the push now goes through the SAME send queue every run push uses when the service is
    /// running, so a green test proves the delivery path rather than a parallel one that happens to
    /// work. Bypassing <c>_started</c> and the queue is exactly why the Face's Test button reported
    /// success for the entire life of a feature that delivered nothing (SC1.1); when the queue really
    /// is unavailable the test still sends, but says loudly — in the reply AND in the Telegram message
    /// itself — that it bypassed it.</summary>
    internal async Task<TelegramTestOutcome> TestConnectionAsync(CancellationToken ct)
    {
        if (_cfg == null)
            return new TelegramTestOutcome(false, null, "Telegram is not configured on this plan", false, TelegramReadiness.NoBlock);
        if (_token == null)
            return new TelegramTestOutcome(false, null, "no bot token — set CONDUCTOR_TELEGRAM_TOKEN or save one from the Face",
                false, TelegramReadiness.NoToken);

        try
        {
            var resp = await _http.GetAsync($"{_apiBase}{_token}/getMe", ct).ConfigureAwait(false);
            var body = await resp.Content.ReadFromJsonAsync<TgGetMeResponse>(JsonOpts, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || body is not { Ok: true, Result: { } me })
            {
                var err = $"getMe failed: HTTP {(int)resp.StatusCode}";
                _lastError = err;
                return new TelegramTestOutcome(false, null, err, false, null);
            }

            _botUsername = me.Username;

            // A test that sends nothing is not a passing test, however valid the token: with no chat
            // to deliver to there is nobody to reach, and the old "true" here is what let the Face
            // tick step 3 of its guided setup on a bot that could never reach the owner.
            //
            // DV2.3, bug #65: the check and the send both read AllowedChatIds — the RAW allow-list.
            // On a plan that declares its chats the KS11.2 way that list is empty while the resolved
            // set is not, so this endpoint reported "there is no chat to send it to" for a bot that
            // delivered perfectly, and a Face guided setup could not be completed on a correct plan.
            // The strand doc filed it as an empty-list INDEX crash; it is not — the guard above the
            // index is real. The defect is the false negative, from the same raw-vs-resolved read as
            // the startup line (#64). Targets is the resolved set, and admin comes first because a
            // test message is an admin's proof, not something to post into an observer chat.
            var targets = Targets;
            if (targets.Count == 0)
                return new TelegramTestOutcome(false, me.Username, TelegramReadiness.NoChatIds, false,
                    "the token is valid, but no test message was sent — there is no chat to send it to");
            var target = targets.FirstOrDefault(t => t.Profile == ChatProfile.Admin, targets[0]);

            return _started
                ? await SendTestViaQueueAsync(target.ChatId, me.Username, ct).ConfigureAwait(false)
                : await SendTestBypassingQueueAsync(target.ChatId, me.Username, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var err = $"could not reach Telegram: {ex.Message}";
            _lastError = err;
            return new TelegramTestOutcome(false, null, err, false, null);
        }
    }

    /// <summary>The honest path: hand the message to the live queue and report what the send loop
    /// actually did with it. Everything this exercises — started flag, queue, send loop, token,
    /// chat id — is exercised by a real session-end push too.</summary>
    private async Task<TelegramTestOutcome> SendTestViaQueueAsync(string chatId, string? bot, CancellationToken ct)
    {
        var ack = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var text = $"✅ Conductor test message — bot @{bot} is connected. Sent through the live push queue, "
                 + "the same path every run notification takes.";
        var queue = _sendQueue;   // SC1.3: the queue this test's ack belongs to, even if a reload swaps it
        if (!queue.Writer.TryWrite(new OutboundMessage(chatId, text, Ack: ack, Severity: PushSeverity.Alert)))
            return new TelegramTestOutcome(false, bot, "the send queue is closed — the service is shutting down",
                false, TelegramReadiness.NotStarted);

        string? error;
        try { error = await ack.Task.WaitAsync(TestAckTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new TelegramTestOutcome(false, bot, "queued, but the send loop did not report back within 30s",
                true, "the message is still sitting in the live queue — real pushes are stuck the same way");
        }

        return error is null
            ? new TelegramTestOutcome(true, bot, null, true,
                "sent through the live send queue — the same path every run push takes")
            : new TelegramTestOutcome(false, bot, error, true,
                "the live send queue accepted the message and failed to deliver it — real pushes are failing the same way");
    }

    /// <summary>The loud path. The queue is not running, so this proves only that Telegram can be
    /// reached from this process — NOT that this run will ever notify anybody. Both the HTTP reply
    /// and the message that lands on the phone say so.</summary>
    private async Task<TelegramTestOutcome> SendTestBypassingQueueAsync(string chatId, string? bot, CancellationToken ct)
    {
        const string bypassed = "sent DIRECTLY, bypassing the send queue — this test did NOT prove delivery: "
                              + TelegramReadiness.NotStarted;
        try
        {
            await SendAsync(chatId, $"⚠️ Conductor test message — bot @{bot} answered, but the push queue is NOT "
                + "running in this process, so real notifications from this run are being dropped.", ct).ConfigureAwait(false);
            return new TelegramTestOutcome(true, bot, null, false, bypassed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var err = $"getMe succeeded (@{bot}) but the test message failed: {ex.Message}";
            _lastError = err;
            return new TelegramTestOutcome(false, bot, err, false, bypassed);
        }
    }
}
