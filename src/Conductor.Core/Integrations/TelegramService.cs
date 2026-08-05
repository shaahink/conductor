using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conductor.Core.Planning;
using Conductor.Core.Events;
using Conductor.Core.Integrations.Messaging;
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
    /// <summary>SF0.1 / FU-OWNER-12: <c>null</c> when a push from this run will actually be
    /// delivered, else the missing half in <see cref="TelegramReadiness"/>' own words — the same
    /// sentence <c>doctor</c> and <c>GET /telegram/status</c> print, so the surfaces cannot drift
    /// (SC1.2's same-words requirement). Exists because a run said NOTHING about notifications at
    /// startup: <c>grep -ci telegram .conductor/conductor.log</c> returned 0 on a live run, so an
    /// operator watching a silent chat could not tell "nothing happened" from "nothing can be
    /// delivered". The run log now answers that once, unasked.</summary>
    string? DeliveryBlocker { get; }

    /// <summary>K5.4: <paramref name="severity"/> is how a push that the owner must ACT on gets to
    /// buzz while a progress line does not. It is chosen by the caller — the one place that knows
    /// what happened — rather than sniffed out of the message text here.</summary>
    Task PushAsync(string message, Messaging.PushSeverity severity = Messaging.PushSeverity.Quiet,
        CancellationToken ct = default);
    Task PushWithKeyboardAsync(string message, IReadOnlyList<(string Text, string CallbackData)> buttons,
        CancellationToken ct = default);
    Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct = default);

    /// <summary>K5.3: the notification path can CARRY an evidence artifact. This announces them as
    /// text — K5.4 is what actually sends a photo or a document, and it replaces the body of this
    /// method rather than adding a second path. The owner's case is a screenshot nobody forwards.</summary>
    Task PushEvidenceAsync(IReadOnlyList<Evidence.EvidenceArtifact> artifacts, CancellationToken ct = default);
}

public sealed partial class TelegramService : IHostedService, ITelegramService, IReportsStartOutcome, IDisposable
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>SC1.3: config, token and API base are re-resolvable at runtime, not snapshots taken
    /// in the constructor. A token typed into the Face and a telegram block added by a plan edit both
    /// arrive AFTER this object exists; while these were readonly the only way to pick either up was
    /// to restart the engine, and no surface said so. Written only under <see cref="_gate"/>.</summary>
    private TelegramConfig? _cfg;
    private string? _token;
    internal PlanConfig _plan;
    internal readonly RunState _state;
    internal IProgressProvider _progress;
    internal readonly ILogger<TelegramService> _log;
    /// <summary>SC1.2: the ack is how <c>POST /telegram/test</c> can route through the REAL queue and
    /// still answer its HTTP caller — the send loop completes it with null on success or the error
    /// text on failure. Every ordinary push leaves it null and stays fire-and-forget.
    /// SC1.3: recreated on every start, because <see cref="StopAsync"/> completes the writer and a
    /// completed channel can never carry another message — a restart on a reloaded token would
    /// otherwise come up with a queue that silently drops everything.</summary>
    private Channel<OutboundMessage> _sendQueue;
    private readonly HttpClient _http;
    private CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private Task? _sendTask;
    private int _offset;
    internal bool _started;
    /// <summary>Serialises start / stop / reload against each other: a reload arriving from an HTTP
    /// thread while the run loop's plan swap is mid-restart would otherwise interleave two stops and
    /// two starts and leave orphaned loops behind.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
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
    private string _apiBase;

    public TelegramService(
        PlanConfig plan,
        RunState state,
        ILogger<TelegramService> logger,
        IRunStore? store = null)
    {
        _state = state;
        _log = logger;
        _store = store;
        AdoptPlan(plan);

        _sendQueue = NewSendQueue();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(65) };
    }

    private static Channel<OutboundMessage> NewSendQueue() =>
        Channel.CreateUnbounded<OutboundMessage>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>SC1.3: take everything this service derives from the plan, from THIS plan — used by
    /// the constructor and again by every reload, so there is one derivation and the two cannot
    /// drift. The token is re-resolved here too: it lives outside the plan (env var or secrets file)
    /// and can appear at any moment, which is exactly the case that used to need a restart.</summary>
    [MemberNotNull(nameof(_plan), nameof(_progress), nameof(_apiBase))]
    private void AdoptPlan(PlanConfig plan)
    {
        _plan = plan;
        _progress = ProgressProviderFactory.Create(plan);
        _cfg = plan.Telegram;
        _token = ResolveToken(plan);

        var root = string.IsNullOrWhiteSpace(_cfg?.ApiBaseUrl) ? DefaultApiRoot : _cfg!.ApiBaseUrl!.Trim();
        _apiBase = root.TrimEnd('/') + "/bot";
    }

    /// <summary>Env var wins (unchanged, existing behavior); falls back to the M8.2 local secrets
    /// file (SecretsStore) so the token can also be typed into the Face's guided setup instead of
    /// set as an environment variable.</summary>
    internal static string? ResolveToken(PlanConfig plan)
    {
        var fromEnv = Environment.GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN")?.Trim();
        if (fromEnv is { Length: > 0 }) return fromEnv;
        return SecretsStore.TryReadTelegramToken(plan.StateDir);
    }

    internal bool IsConfigured => _cfg != null && _token != null;

    /// <inheritdoc />
    /// <remarks>Derived, never stored, and read from the service's OWN block rather than the plan's —
    /// after a live reload only one of those describes what the next push will do. Read outside
    /// <c>_gate</c> like <c>GET /telegram/status</c> does: this answers one log line and one HTTP
    /// field, and taking the gate for either would let a wedged start block a status read.</remarks>
    public string? DeliveryBlocker => TelegramReadiness.MissingHalf(
        hasBlock: _cfg is not null, hasToken: IsConfigured,
        allowedChatIds: _cfg?.AllowedChatIds.Count ?? 0, started: _started);

    /// <summary>SF0.1 / bug 2: whether <see cref="StartAsync"/> actually started the loops. It very
    /// often does not — no telegram block is the ordinary case — and until this existed the host
    /// announced <c>Run services started: TelegramService</c> either way, because it named every
    /// service it had called StartAsync on rather than every service that had started.</summary>
    public bool IsStarted => _started;

    /// <summary>The reason the loops are not running, in <see cref="TelegramReadiness"/>' words;
    /// null once started.</summary>
    public string? NotStartedReason => _started ? null : DeliveryBlocker;

    /// <summary>SC1.3: the telegram block this service is actually running on, which after a live
    /// reload is not necessarily the one any other holder of a plan reference has. Status must report
    /// what the service will do, not what the plan file says it should.</summary>
    internal TelegramConfig? LiveConfig => _cfg;

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


    public void Dispose()
    {
        _cts.Dispose();
        _http.Dispose();
    }

    // Every real caller is `_ = Push…(…)` fire-and-forget, so an exception here would be an
    // unobserved task exception nobody ever sees. The queue is unbounded, so TryWrite never blocks
    // and never throws — it just returns false once StopAsync has closed the channel.
    public Task PushAsync(string message, PushSeverity severity = PushSeverity.Quiet,
        CancellationToken ct = default)
    {
        // Nothing to read the tracker for if the push cannot be delivered — this guard is repeated
        // inside EnqueueAsync, which is the one that actually decides.
        if (!_started || _cfg?.AllowedChatIds is not { Count: > 0 }) return Task.CompletedTask;

        // K5.2: where the run is, on every engine push — fifteen messages of the owner's own run
        // carried no checkpoint count, no stage progress and no ETA between them. Appended, never
        // substituted, and empty when there is no tracker to read.
        var progress = ProgressLine(null);
        return EnqueueAsync(progress.Length > 0 ? message + "\n" + EscapeHtml(progress) : message,
            null, severity, null, ct);
    }

    /// <summary>The one write path onto the send queue. <paramref name="sessionNumber"/> is the
    /// number the identity line will carry: the RECORD's for a session-end push, and the live
    /// counter for everything else (K5.2 — the number used to be printed twice, from two sources).</summary>
    /// <remarks>K5.4: <paramref name="severity"/> decides whether the push buzzes the owner's phone,
    /// and <paramref name="attachment"/> makes it a file rather than text. Both are properties of the
    /// EVENT, so they are chosen by the caller that knows what happened, not here.</remarks>
    private Task EnqueueAsync(string message, int? sessionNumber,
        PushSeverity severity = PushSeverity.Quiet, OutboundAttachment? attachment = null,
        CancellationToken ct = default)
    {
        // SC1.3: read the queue field ONCE — a reload can swap it between the check and the write,
        // and a push split across two queues would be delivered twice or not at all.
        var queue = _sendQueue;
        if (!_started || _cfg?.AllowedChatIds is not { Count: > 0 } ids) return Task.CompletedTask;
        foreach (var cid in ids)
            queue.Writer.TryWrite(new OutboundMessage(cid, message, null, null, sessionNumber, severity, attachment));
        return Task.CompletedTask;
    }

    public Task PushWithKeyboardAsync(string message,
        IReadOnlyList<(string Text, string CallbackData)> buttons, CancellationToken ct = default)
    {
        var queue = _sendQueue;
        if (!_started || _cfg is not { EnableTwoWay: true, AllowedChatIds.Count: > 0 } cfg) return Task.CompletedTask;
        var kb = BuildInlineKeyboard(buttons);
        foreach (var cid in cfg.AllowedChatIds)
            // A keyboard means the engine is ASKING the owner for something — that is the definition
            // of a push that should buzz.
            queue.Writer.TryWrite(new OutboundMessage(cid, message, kb, Severity: PushSeverity.Alert));
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

    /// <summary>Reads the queue it was STARTED with, not the current field: a reload swaps in a new
    /// queue, and a loop that followed the field would end up as a second reader on it.</summary>
    private async Task SendLoopAsync(Channel<OutboundMessage> queue, CancellationToken ct)
    {
        // ReadAllAsync completes normally once the writer is closed AND the backlog is drained —
        // that is what lets StopAsync flush the final session-end push instead of dropping it.
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(item, ct).ConfigureAwait(false);
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

    /// <summary>FU-OWNER-11 — the two facts a Telegram message cannot recover on its own: WHICH plan
    /// sent it and WHICH session it belongs to. One chat can receive two machines' runs, so an
    /// unattributed line is unreadable; and a message read hours later has no other way to be placed
    /// in the run's history. The observed failure was the mirror image — a hand-typed operator
    /// message was indistinguishable from an engine push, and quoted an engine version the run had
    /// already superseded.
    /// <para>Read off the LIVE plan and state rather than a constructor snapshot: a reload can rename
    /// the plan (SC1.3) and the session counter moves under every message.</para></summary>
    internal string IdentityLine => IdentityFor(null);

    /// <summary>K5.2: ONE source for the session number. A session-end push passes the record's own
    /// number, which is the truthful one for a message about that session; everything else falls
    /// back to the live counter. The body no longer prints a second copy that could disagree.</summary>
    internal string IdentityFor(int? sessionNumber)
    {
        var name = string.IsNullOrWhiteSpace(_plan.Name) ? "conductor" : _plan.Name.Trim();
        return FormattableString.Invariant(
            $"<i>{EscapeHtml(name)} · s{sessionNumber ?? _state.SessionCounter}</i>");
    }

    // K5.4: the wire path moved to TelegramService.Transport.cs — the identity stamp is still applied
    // at that single choke point (FU-OWNER-11), and it now also chunks at 4096, threads the run and
    // maps severity to disable_notification, for text and attachments alike.

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
            var cts = _cts;   // SC1.3: the token of the loop this handler is running on
            var path = Path.Combine(_plan.StateDir, "control.json");
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["command"] = action,
                ["issuedUtc"] = DateTime.UtcNow.ToString("O"),
                ["confirmed"] = confirmed,
            };
            if (intentId != null) payload["intentId"] = intentId;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOpts), cts.Token).ConfigureAwait(false);
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
