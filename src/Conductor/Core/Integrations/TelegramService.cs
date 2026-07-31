using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conductor.Core.Planning;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>Pushes status messages to a Telegram bot; handles /status and (B6.2) inline-keyboard
/// callbacks that write control.json. Registered as an <see cref="IHostedService"/> so long-polling
/// starts with the host and stops on disposal. If no <see cref="TelegramConfig"/> is configured
/// or the bot token env-var is missing, the service is a no-op.</summary>
public interface ITelegramService
{
    Task PushAsync(string message, CancellationToken ct = default);
    Task PushWithKeyboardAsync(string message, IReadOnlyList<(string Text, string CallbackData)> buttons,
        CancellationToken ct = default);
    Task PushSessionEndAsync(int sessionNumber, string stage, string outcome, string? gateSummary,
        string? resultSummary, decimal? costUsd, decimal? score, CancellationToken ct = default);
}

public sealed partial class TelegramService : IHostedService, ITelegramService, IDisposable
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TelegramConfig? _cfg;
    private readonly string? _token;
    internal readonly PlanConfig _plan;
    internal readonly RunState _state;
    internal readonly IProgressProvider _progress;
    internal readonly ILogger<TelegramService> _log;
    /// <summary>SC1.2: the ack is how <c>POST /telegram/test</c> can route through the REAL queue and
    /// still answer its HTTP caller — the send loop completes it with null on success or the error
    /// text on failure. Every ordinary push leaves it null and stays fire-and-forget.</summary>
    private readonly Channel<(string ChatId, string Text, string? KeyboardJson, TaskCompletionSource<string?>? Ack)> _sendQueue;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private Task? _sendTask;
    private int _offset;
    internal bool _started;
    internal readonly IRunStore? _store;
    internal DateTime _lastDigestUtc = DateTime.UtcNow;
    internal readonly Dictionary<string, bool> _pendingInjections = new(StringComparer.Ordinal);

    /// <summary>M8.2: last time getUpdates succeeded, and the last poll/send error message (if
    /// any) — surfaced by the /telegram/status endpoint so the Face can show live connection
    /// health, not just "configured or not".</summary>
    internal DateTime? _lastPollUtc;
    internal string? _lastError;
    internal string? _botUsername;

    internal const string DefaultApiRoot = "https://api.telegram.org";

    /// <summary>Bot API prefix up to and including <c>/bot</c>; the token is appended per call.</summary>
    private readonly string _apiBase;

    public TelegramService(
        PlanConfig plan,
        RunState state,
        ILogger<TelegramService> logger,
        IRunStore? store = null)
    {
        _plan = plan;
        _state = state;
        _progress = ProgressProviderFactory.Create(plan);
        _log = logger;
        _cfg = plan.Telegram;
        _token = ResolveToken(plan);
        _store = store;

        var root = string.IsNullOrWhiteSpace(_cfg?.ApiBaseUrl) ? DefaultApiRoot : _cfg!.ApiBaseUrl!.Trim();
        _apiBase = root.TrimEnd('/') + "/bot";

        _sendQueue = Channel.CreateUnbounded<(string, string, string?, TaskCompletionSource<string?>?)>(
            new UnboundedChannelOptions { SingleReader = true });

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(65) };
    }

    /// <summary>Env var wins (unchanged, existing behavior); falls back to the M8.2 local secrets
    /// file (SecretsStore) so the token can also be typed into the Face's guided setup instead of
    /// set as an environment variable.</summary>
    private static string? ResolveToken(PlanConfig plan)
    {
        var fromEnv = Environment.GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN")?.Trim();
        if (fromEnv is { Length: > 0 }) return fromEnv;
        return SecretsStore.TryReadTelegramToken(plan.StateDir);
    }

    internal bool IsConfigured => _cfg != null && _token != null;

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

            // A test that sends nothing is not a passing test, however valid the token: with no
            // allowed chat id there is nobody to deliver to, and the old "true" here is what let the
            // Face tick step 3 of its guided setup on a bot that could never reach the owner.
            if (_cfg.AllowedChatIds.Count == 0)
                return new TelegramTestOutcome(false, me.Username, TelegramReadiness.NoChatIds, false,
                    "the token is valid, but no test message was sent — there is no chat to send it to");

            return _started
                ? await SendTestViaQueueAsync(_cfg.AllowedChatIds[0], me.Username, ct).ConfigureAwait(false)
                : await SendTestBypassingQueueAsync(_cfg.AllowedChatIds[0], me.Username, ct).ConfigureAwait(false);
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
        if (!_sendQueue.Writer.TryWrite((chatId, text, null, ack)))
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

    /// <summary>SC1.2: logs on BOTH outcomes. The silent early return is the exact shape of the bug
    /// SC1.1 fixed — a process that has decided to deliver nothing for the rest of the run, and says
    /// so nowhere. Not-started names the missing half in doctor's own words; started names the poll
    /// interval and how many chat ids it will actually reach, because "started" with an empty
    /// allowedChatIds is push-only to nobody and would otherwise read as success.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            var missing = TelegramReadiness.MissingHalf(
                hasBlock: _cfg is not null, hasToken: _token is not null,
                allowedChatIds: _cfg?.AllowedChatIds.Count ?? 0, started: false);
            // No telegram block at all is an ordinary, deliberate choice; a block that cannot deliver
            // is a misconfiguration the owner meant to work, and warrants the louder level.
            if (_cfg is null) _log.LogInformation("Telegram not started: {Reason}", missing);
            else _log.LogWarning("Telegram not started: {Reason}", missing);
            return Task.CompletedTask;
        }

        _started = true;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token), CancellationToken.None);

        var chatIds = _cfg!.AllowedChatIds.Count;
        if (chatIds == 0)
            _log.LogWarning("Telegram bot started (poll interval {Interval}s) but will deliver nothing: {Reason}",
                _cfg.PollIntervalSeconds, TelegramReadiness.NoChatIds);
        else
            _log.LogInformation("Telegram bot started (poll interval {Interval}s, {ChatIds} allowed chat id(s))",
                _cfg.PollIntervalSeconds, chatIds);
        return Task.CompletedTask;
    }

    /// <summary>SC1.1: how long the send queue is allowed to flush before shutdown stops waiting.
    /// The run's last act is a fire-and-forget session-end push, so cancelling the send loop the
    /// instant the loop exits is how "the push arrives" quietly degrades into "the push was queued".</summary>
    internal static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(10);

    public async Task StopAsync(CancellationToken ct)
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

    public void Dispose()
    {
        _cts.Dispose();
        _http.Dispose();
    }

    // Every real caller is `_ = Push…(…)` fire-and-forget, so an exception here would be an
    // unobserved task exception nobody ever sees. The queue is unbounded, so TryWrite never blocks
    // and never throws — it just returns false once StopAsync has closed the channel.
    public Task PushAsync(string message, CancellationToken ct = default)
    {
        if (!_started || _cfg?.AllowedChatIds is not { Count: > 0 } ids) return Task.CompletedTask;
        foreach (var cid in ids)
            _sendQueue.Writer.TryWrite((cid, message, null, null));
        return Task.CompletedTask;
    }

    public Task PushWithKeyboardAsync(string message,
        IReadOnlyList<(string Text, string CallbackData)> buttons, CancellationToken ct = default)
    {
        if (!_started || _cfg is not { EnableTwoWay: true, AllowedChatIds.Count: > 0 }) return Task.CompletedTask;
        var kb = BuildInlineKeyboard(buttons);
        foreach (var cid in _cfg.AllowedChatIds)
            _sendQueue.Writer.TryWrite((cid, message, kb, null));
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_cfg!.PollIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                await MaybeSendDailyDigestAsync(ct).ConfigureAwait(false);
                _lastPollUtc = DateTime.UtcNow;
                _lastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log.LogWarning(ex, "Telegram poll error");
            }
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var url = $"{_apiBase}{_token}/getUpdates?offset={_offset}&timeout=30";
        var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TgResponse>(JsonOpts, ct).ConfigureAwait(false);
        if (body is not { Ok: true, Result: { Count: > 0 } updates }) return;

        foreach (var upd in updates)
        {
            _offset = upd.UpdateId + 1;
            await HandleUpdateAsync(upd, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleUpdateAsync(TgUpdate upd, CancellationToken ct)
    {
        if (upd.Message is { } msg)
        {
            var chatId = msg.Chat?.Id.ToString(CultureInfo.InvariantCulture);
            if (!IsAllowed(chatId)) return;
            await HandleMessageAsync(chatId!, msg, ct).ConfigureAwait(false);
        }
        if (upd.CallbackQuery is { } cb)
        {
            var chatId = cb.Message?.Chat?.Id.ToString(CultureInfo.InvariantCulture)
                         ?? cb.From?.Id.ToString(CultureInfo.InvariantCulture);
            if (!IsAllowed(chatId)) return;
            await HandleCallbackAsync(cb, ct).ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        // ReadAllAsync completes normally once the writer is closed AND the backlog is drained —
        // that is what lets StopAsync flush the final session-end push instead of dropping it.
        try
        {
            await foreach (var item in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(item.ChatId, item.Text, ct, item.KeyboardJson).ConfigureAwait(false);
                    item.Ack?.TrySetResult(null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    item.Ack?.TrySetResult("the send queue was shutting down");
                    break;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _log.LogWarning(ex, "Telegram send error");
                    item.Ack?.TrySetResult(ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
    }

    internal async Task SendAsync(string chatId, string text, CancellationToken ct,
        string? keyboardJson = null)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
        };
        if (keyboardJson != null)
        {
            using var kbDoc = JsonDocument.Parse(keyboardJson);
            payload["reply_markup"] = kbDoc.RootElement.Clone();
        }

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_apiBase}{_token}/sendMessage", content, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async Task AnswerCallbackAsync(string callbackQueryId, CancellationToken ct)
    {
        try
        {
            var url = $"{_apiBase}{_token}/answerCallbackQuery?callback_query_id={Uri.EscapeDataString(callbackQueryId)}";
            await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogWarning(ex, "answerCallbackQuery failed"); }
    }

    internal async Task WriteControlFileAsync(string action, bool confirmed = false, string? intentId = null)
    {
        try
        {
            var path = Path.Combine(_plan.StateDir, "control.json");
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["command"] = action,
                ["issuedUtc"] = DateTime.UtcNow.ToString("O"),
                ["confirmed"] = confirmed,
            };
            if (intentId != null) payload["intentId"] = intentId;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOpts), _cts.Token).ConfigureAwait(false);
            _log.LogInformation("Telegram wrote control.json: {Action} (confirmed={Confirmed})", action, confirmed);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to write control.json"); }
    }

    private bool IsAllowed(string? chatId)
    {
        if (chatId == null || _cfg?.AllowedChatIds is not { Count: > 0 } ids) return false;
        return ids.Contains(chatId, StringComparer.Ordinal);
    }
}

/// <summary>No-op stub when Telegram is not configured.</summary>
public sealed class NoOpTelegramService : ITelegramService
{
    public Task PushAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushWithKeyboardAsync(string message,
        IReadOnlyList<(string Text, string CallbackData)> buttons, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushSessionEndAsync(int sessionNumber, string stage, string outcome, string? gateSummary,
        string? resultSummary, decimal? costUsd, decimal? score, CancellationToken ct = default) => Task.CompletedTask;
}
