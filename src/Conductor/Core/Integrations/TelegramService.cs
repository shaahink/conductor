using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conductor.Core.Planning;
using Conductor.Core.Events;
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
    /// <summary>F8.2: Push a session-end summary with score, gates, and cost.</summary>
    Task PushSessionEndAsync(int sessionNumber, string stage, string outcome, string? gateSummary,
        string? resultSummary, decimal? costUsd, decimal? score, CancellationToken ct = default);
}

public sealed class TelegramService : IHostedService, ITelegramService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TelegramConfig? _cfg;
    private readonly string? _token;
    private readonly PlanConfig _plan;
    private readonly RunState _state;
    private readonly IProgressProvider _progress;
    private readonly ILogger<TelegramService> _log;
    private readonly Channel<(string ChatId, string Text, string? KeyboardJson)> _sendQueue;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private Task? _sendTask;
    private int _offset;
    private bool _started;
    private readonly RunDb? _runDb; // F8.2: optional run.db for richer /status and daily digest
    private DateTime _lastDigestUtc = DateTime.UtcNow; // F8.3: daily digest timer
    private readonly Dictionary<string, bool> _pendingInjections = new(StringComparer.Ordinal); // F8.3: reply-to-inject

    private const string ApiBase = "https://api.telegram.org/bot";

    public TelegramService(
        PlanConfig plan,
        RunState state,
        ILogger<TelegramService> logger,
        RunDb? runDb = null)
    {
        _plan = plan;
        _state = state;
        _progress = ProgressProviderFactory.Create(plan);
        _log = logger;
        _cfg = plan.Telegram;
        _token = ResolveToken();
        _runDb = runDb;

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

    // ──────────────────────────────── IHostedService ────────────────────────────────

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

    // ──────────────────────────────── ITelegramService ────────────────────────────────

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

    // ──────────────────────────────── poll loop ────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_cfg!.PollIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                // F8.3: daily digest — push a summary once per day (± polling jitter)
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

    // ──────────────────────────────── inbound handlers ────────────────────────────────

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

    private async Task HandleMessageAsync(string chatId, TgMessage msg, CancellationToken ct)
    {
        var text = (msg.Text ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return;

        // F8.3: reply-to-inject — if the user just tapped [Inject...] and is now replying
        if (_pendingInjections.TryGetValue(chatId, out var pending) && pending
            && !text.StartsWith('/'))
        {
            _pendingInjections.Remove(chatId);
            await HandleInjectAsync(chatId, text, ct).ConfigureAwait(false);
            return;
        }

        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            var status = BuildStatusText();
            await SendAsync(chatId, status, ct).ConfigureAwait(false);
        }
        else if (text.Equals("/tasks", StringComparison.OrdinalIgnoreCase))
        {
            var tasks = BuildTasksText();
            await SendAsync(chatId, tasks, ct).ConfigureAwait(false);
        }
        else if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(chatId, "Conductor bot is running. Use /status to see the current state.", ct)
                .ConfigureAwait(false);
        }
        else if (text.Equals("/daily", StringComparison.OrdinalIgnoreCase))
        {
            await SendDailyDigestAsync(chatId, ct).ConfigureAwait(false);
        }
        else if (text.StartsWith("/inject ", StringComparison.OrdinalIgnoreCase))
        {
            var instruction = text[8..].Trim();
            await HandleInjectAsync(chatId, instruction, ct).ConfigureAwait(false);
        }
        else if (text.Equals("/chat", StringComparison.OrdinalIgnoreCase))
        {
            var planName = _plan.PlanFilePath != null ? Path.GetFileName(_plan.PlanFilePath) : "conductor.plan.json";
            await SendAsync(chatId,
                $"Use `conductor chat \"your question\"` from the terminal to ask questions about this run.\n\nExample: `conductor chat -p {planName} \"how did session 9 die?\"`",
                ct).ConfigureAwait(false);
        }
        else if (_cfg?.EnableTwoWay == true && text.StartsWith('/'))
        {
            await HandleTwoWayCommandAsync(chatId, text, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleTwoWayCommandAsync(string chatId, string command, CancellationToken ct)
    {
        string? controlAction;
        bool destructive;
        (controlAction, destructive) = command.ToLowerInvariant() switch
        {
            "/pause" => ("pause", false),
            "/resume" => ("resume", false),
            "/approve" => ("approve", false),
            "/skip" => ("skip", true),
            "/abort" => ("abort", true),
            "/kill" => ("kill", true),
            _ => (null, false),
        };

        if (controlAction == null) return;

        if (destructive)
        {
            var intentId = Guid.NewGuid().ToString("N")[..8];
            var kb = BuildInlineKeyboard(
            [
                ($"Yes, {controlAction}", $"{controlAction}:{intentId}:confirmed"),
                ("Cancel", $"cancel:{intentId}"),
            ]);
            await SendAsync(chatId, $"Confirm {controlAction}? This cannot be undone.", ct, kb)
                .ConfigureAwait(false);
        }
        else
        {
            WriteControlFile(controlAction);
            await SendAsync(chatId, $"{controlAction} command sent to Conductor.", ct)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleCallbackAsync(TgCallbackQuery cb, CancellationToken ct)
    {
        var data = cb.Data ?? "";
        await AnswerCallbackAsync(cb.Id, ct).ConfigureAwait(false);

        if (data.StartsWith("cancel:", StringComparison.Ordinal))
        {
            if (cb.From != null)
                await SendAsync(cb.From.Id.ToString(CultureInfo.InvariantCulture), "Cancelled.", ct)
                    .ConfigureAwait(false);
            return;
        }

        if (data.StartsWith("inject:", StringComparison.Ordinal))
        {
            // Prompt the user to respond with the injection text
            if (cb.From != null)
            {
                var userId = cb.From.Id.ToString(CultureInfo.InvariantCulture);
                // Store the pending injection intent
                _pendingInjections[userId] = true;
                await SendAsync(userId, "Reply to this message with the text you want to inject into the next session.", ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (data.StartsWith("chat:", StringComparison.Ordinal))
        {
            if (cb.From != null)
            {
                var planName = _plan.PlanFilePath != null ? Path.GetFileName(_plan.PlanFilePath) : "conductor.plan.json";
                await SendAsync(cb.From.Id.ToString(CultureInfo.InvariantCulture),
                    $"Use `conductor chat -p {planName} \"your question\"` from the terminal.", ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        // Format: action:intentId:confirmed or action:intentId
        var parts = data.Split(':');
        if (parts.Length < 2) return;
        var action = parts[0];
        var confirmed = parts.Length > 2 && parts[2] == "confirmed";

        if (confirmed && cb.From != null)
        {
            WriteControlFile(action, confirmed: true, intentId: parts[1]);
            await SendAsync(cb.From.Id.ToString(CultureInfo.InvariantCulture),
                $"{action} confirmed and sent to Conductor.", ct).ConfigureAwait(false);
        }
    }

    // ──────────────────────────────── outbound ────────────────────────────────

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

    private async Task SendAsync(string chatId, string text, CancellationToken ct,
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
            // keyboardJson is already a JSON string — parse it so we can nest it as an object
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

    // ──────────────────────────────── status ────────────────────────────────

    // F8.2: session-end one-liner with score, gates, and cost.
    public async Task PushSessionEndAsync(int sessionNumber, string stage, string outcome, string? gateSummary,
        string? resultSummary, decimal? costUsd, decimal? score, CancellationToken ct = default)
    {
        if (!_started) return;

        var runCost = _state.TotalCostUsd > 0 ? $" | run: ${_state.TotalCostUsd:0.0000}" : "";
        var scoreStr = score.HasValue ? $" | score: {score:0}/100" : "";
        var sb = new StringBuilder();
        sb.AppendLine($"<b>s{sessionNumber} {outcome}</b> — {stage}");
        sb.AppendLine($"gates: {(string.IsNullOrWhiteSpace(gateSummary) ? "(not recorded)" : gateSummary)}");
        if (!string.IsNullOrWhiteSpace(resultSummary))
            sb.AppendLine($"result: {resultSummary}");
        sb.Append($"cost: ${costUsd ?? 0:0.0000}{runCost}{scoreStr}");

        await PushAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    // F8.3: handle /inject <text> from Telegram
    private async Task HandleInjectAsync(string chatId, string instruction, CancellationToken ct)
    {
        if (_runDb == null)
        {
            await SendAsync(chatId, "Cannot inject: run.db is not available.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var runId = _state.RunId ?? Guid.NewGuid().ToString("N");
            _runDb.WriteInjection(runId, "telegram", null, _state.CurrentStage, instruction);
            await SendAsync(chatId, $"Instruction injected for the next session: <i>{EscapeHtml(instruction)}</i>", ct)
                .ConfigureAwait(false);
            _log.LogInformation("Telegram /inject: {Instruction} (stage={Stage})", instruction, _state.CurrentStage);
        }
        catch (Exception ex)
        {
            await SendAsync(chatId, $"Failed to inject: {EscapeHtml(ex.Message)}", ct).ConfigureAwait(false);
        }
    }

    // F8.3: daily digest — check if 24h have passed and push a summary
    private async Task MaybeSendDailyDigestAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastDigestUtc < TimeSpan.FromHours(24) || _cfg?.AllowedChatIds is not { Count: > 0 } ids)
            return;

        _lastDigestUtc = DateTime.UtcNow;
        foreach (var cid in ids)
            await SendDailyDigestAsync(cid, ct).ConfigureAwait(false);
    }

    private async Task SendDailyDigestAsync(string chatId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor Daily Digest — {_plan.Name}</b>");
        sb.AppendLine($"Status: <b>{_state.Status}</b> | Stage: {_state.CurrentStage ?? "-"}");
        sb.AppendLine($"Sessions: {_state.SessionCounter} | Cost: ${_state.TotalCostUsd:0.0000}");

        if (_runDb != null)
        {
            try
            {
                var rows = _runDb.Query(
                    "SELECT stage_id, outcome, count(*) as cnt FROM sessions GROUP BY stage_id, outcome ORDER BY stage_id");
                if (rows.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("<b>Session outcomes by stage:</b>");
                    foreach (var r in rows)
                    {
                        var s = r.GetValueOrDefault("stage_id")?.ToString() ?? "?";
                        var o = r.GetValueOrDefault("outcome")?.ToString() ?? "?";
                        var c = r.GetValueOrDefault("cnt")?.ToString() ?? "0";
                        sb.AppendLine($"  {s}: {o} ×{c}");
                    }
                }

                // Recent gate failures
                var gates = _runDb.Query(
                    "SELECT name, stage_id, tier FROM gates WHERE passed = 0 AND skipped = 0 ORDER BY id DESC LIMIT 5");
                if (gates.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("<b>Recent gate failures:</b>");
                    foreach (var g in gates)
                    {
                        var n = g.GetValueOrDefault("name")?.ToString() ?? "?";
                        var s = g.GetValueOrDefault("stage_id")?.ToString() ?? "?";
                        sb.AppendLine($"  FAIL: {n} ({s})");
                    }
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("All recent gates passed.");
                }
            }
#pragma warning disable CA1031
            catch { /* best-effort: digest is advisory */ }
#pragma warning restore CA1031
        }

        await SendAsync(chatId, sb.ToString().TrimEnd(), ct).ConfigureAwait(false);
    }

    // F8.3: enhanced /status with run.db data when available
    private string BuildStatusText()
    {
        TrackerSnapshot track;
        try { track = _progress.Read(_plan, CancellationToken.None); }
        catch (IOException) { track = new TrackerSnapshot(); }
        catch (InvalidOperationException) { track = new TrackerSnapshot(); }

        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor — {_plan.Name}</b>");
        sb.AppendLine();
        sb.AppendLine($"Status: <b>{_state.Status}</b>");
        sb.AppendLine($"Stage: {_state.CurrentStage ?? "-"}  |  attempts used: {_state.AttemptsThisStage}");
        sb.AppendLine($"Checkpoints: {track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} done");
        sb.AppendLine($"Sessions: {_state.SessionCounter}  |  Cost: ${_state.TotalCostUsd:0.0000}");

        if (_state.AttentionReason != null)
            sb.AppendLine($"\n{_state.AttentionReason}");

        if (_state.CurrentStage != null)
        {
            var rows = track.ForStage(_state.CurrentStage).ToList();
            if (rows.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"<b>{_state.CurrentStage} checkpoints:</b>");
                foreach (var r in rows.Take(10))
                {
                    var icon = r.IsDone ? "DONE" : r.IsInProgress ? "ACTV" : r.IsBlocked ? "BLKD" : "TODO";
                    sb.AppendLine($"  [{icon}] {r.Id}: {r.Title}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>B9.5 — builds an HTML-formatted task graph view for Telegram (/tasks),
    /// reading <c>events.jsonl</c> and folding through <see cref="TaskGraph"/>.</summary>
    private string BuildTasksText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor — {_plan.Name}</b>");
        sb.AppendLine($"<b>Task Graph</b>");
        sb.AppendLine();

        var eventsPath = Path.Combine(_plan.StateDir, "events.jsonl");
        if (!File.Exists(eventsPath))
        {
            sb.AppendLine("(no events recorded yet — the task graph populates as the run emits events)");
            return sb.ToString().TrimEnd();
        }

        var graph = new TaskGraph();
        graph.Fold(EventLog.ReadAll(eventsPath));

        if (graph.Count == 0)
        {
            sb.AppendLine("(no tasks recorded yet)");
            return sb.ToString().TrimEnd();
        }

        var checkpoints = graph.Tasks
            .GroupBy(t => t.CheckpointId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var ck in checkpoints)
        {
            sb.AppendLine($"<b>{ck.Key}:</b>");
            foreach (var task in ck.OrderBy(t => t.Order))
            {
                var icon = task.Status switch
                {
                    "done" => " DONE ",
                    "in_progress" => "▶ACTV ",
                    "skipped" => " SKIP ",
                    _ => "      ",
                };
                var src = task.Source.Length > 0 ? $" ({task.Source})" : "";
                sb.AppendLine($"  [{icon}] {task.Title}{src}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ──────────────────────────────── control.json ────────────────────────────────

    private void WriteControlFile(string action, bool confirmed = false, string? intentId = null)
    {
#pragma warning disable MA0045 // sync control file write — called from Telegram polling loop
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

    // ──────────────────────────────── auth / keyboard ────────────────────────────────

    private bool IsAllowed(string? chatId)
    {
        if (chatId == null || _cfg?.AllowedChatIds is not { Count: > 0 } ids) return false;
        return ids.Contains(chatId, StringComparer.Ordinal);
    }

    private static string BuildInlineKeyboard(IReadOnlyList<(string Text, string CallbackData)> buttons)
    {
        var elements = new List<Dictionary<string, string>>(buttons.Count);
        foreach (var (text, data) in buttons)
            elements.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = text,
                ["callback_data"] = data,
            });

        var kb = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["inline_keyboard"] = new[] { elements },
        };

        return JsonSerializer.Serialize(kb, JsonOpts);
    }

    private static string EscapeHtml(string s)
    {
        return s.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}

// ──────────────────────────────── Telegram API DTOs ────────────────────────────────

public sealed class TgResponse
{
    public bool Ok { get; set; }
    public List<TgUpdate>? Result { get; set; }
}

public sealed class TgUpdate
{
    [JsonPropertyName("update_id")] public int UpdateId { get; set; }
    public TgMessage? Message { get; set; }
    [JsonPropertyName("callback_query")] public TgCallbackQuery? CallbackQuery { get; set; }
}

public sealed class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    public string? Text { get; set; }
    public TgChat? Chat { get; set; }
}

public sealed class TgChat
{
    public long Id { get; set; }
    public string? Type { get; set; }
}

public sealed class TgCallbackQuery
{
    public string Id { get; set; } = "";
    public TgUser? From { get; set; }
    public TgMessage? Message { get; set; }
    public string? Data { get; set; }
}

public sealed class TgUser
{
    public long Id { get; set; }
    public string? Username { get; set; }
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
