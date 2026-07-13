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
    private readonly Channel<(string ChatId, string Text, string? KeyboardJson)> _sendQueue;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private Task? _sendTask;
    private int _offset;
    internal bool _started;
    internal readonly IRunStore? _store;
    internal DateTime _lastDigestUtc = DateTime.UtcNow;
    internal readonly Dictionary<string, bool> _pendingInjections = new(StringComparer.Ordinal);

    private const string ApiBase = "https://api.telegram.org/bot";

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
        _token = ResolveToken();
        _store = store;

        _sendQueue = Channel.CreateUnbounded<(string, string, string?)>(
            new UnboundedChannelOptions { SingleReader = true });

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(65) };
    }

    private static string? ResolveToken()
    {
        var t = Environment.GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN")?.Trim();
        return t is { Length: > 0 } ? t : null;
    }

    private bool IsConfigured => _cfg != null && _token != null;

    public Task StartAsync(CancellationToken ct)
    {
        if (!IsConfigured) return Task.CompletedTask;
        _started = true;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token), CancellationToken.None);
        _log.LogInformation("Telegram bot started (poll interval {Interval}s)", _cfg!.PollIntervalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _started = false;
        var poll = _pollTask;
        var send = _sendTask;
        if (poll != null || send != null)
        {
            var tasks = new List<Task>(2);
            if (poll != null) tasks.Add(poll);
            if (send != null) tasks.Add(send);
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _http.Dispose();
    }

    public async Task PushAsync(string message, CancellationToken ct = default)
    {
        if (!_started || _cfg?.AllowedChatIds is not { Count: > 0 } ids) return;
        foreach (var cid in ids)
            await _sendQueue.Writer.WriteAsync((cid, message, null), ct).ConfigureAwait(false);
    }

    public async Task PushWithKeyboardAsync(string message,
        IReadOnlyList<(string Text, string CallbackData)> buttons, CancellationToken ct = default)
    {
        if (!_started || _cfg is not { EnableTwoWay: true, AllowedChatIds.Count: > 0 }) return;
        var kb = BuildInlineKeyboard(buttons);
        foreach (var cid in _cfg.AllowedChatIds)
            await _sendQueue.Writer.WriteAsync((cid, message, kb), ct).ConfigureAwait(false);
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
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Telegram poll error"); }
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var url = $"{ApiBase}{_token}/getUpdates?offset={_offset}&timeout=30";
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var item = await _sendQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
                await SendAsync(item.ChatId, item.Text, ct, item.KeyboardJson)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Telegram send error"); }
        }
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
        var resp = await _http.PostAsync($"{ApiBase}{_token}/sendMessage", content, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private async Task AnswerCallbackAsync(string callbackQueryId, CancellationToken ct)
    {
        try
        {
            var url = $"{ApiBase}{_token}/answerCallbackQuery?callback_query_id={Uri.EscapeDataString(callbackQueryId)}";
            await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogWarning(ex, "answerCallbackQuery failed"); }
    }

    internal void WriteControlFile(string action, bool confirmed = false, string? intentId = null)
    {
#pragma warning disable MA0045
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
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOpts));
#pragma warning restore MA0045
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
