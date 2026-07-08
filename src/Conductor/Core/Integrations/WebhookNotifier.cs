using System.Text;
using System.Text.Json;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>Dispatches notification payloads to configured webhook URLs (generic, Discord, Slack).
/// Fire-and-forget — a failed webhook never blocks the conductor loop.</summary>
public sealed class WebhookNotifier : IDisposable
{
    private readonly PlanConfig _plan;
    private readonly ILogger<WebhookNotifier> _log;
    private readonly HttpClient _http;

    public WebhookNotifier(PlanConfig plan, ILogger<WebhookNotifier> logger)
    {
        _plan = plan;
        _log = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void Dispose() => _http.Dispose();

    public void FireAsync(string message)
    {
        var n = _plan.Notify;
        if (n == null) return;

        if (n.Webhook is { Url: { Length: > 0 } url })
            _ = PostJsonAsync(url, message, n.Webhook.Headers, "webhook");

        if (n.Discord is { Url: { Length: > 0 } discordUrl })
            _ = PostDiscordAsync(discordUrl, message);

        if (n.Slack is { Url: { Length: > 0 } slackUrl })
            _ = PostSlackAsync(slackUrl, message);
    }

    private async Task PostJsonAsync(string url, string message,
        Dictionary<string, string>? headers, string label)
    {
        try
        {
            var payload = new { text = message, timestamp = DateTime.UtcNow.ToString("O") };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (headers != null)
                foreach (var (k, v) in headers)
                    req.Headers.TryAddWithoutValidation(k, v);

            var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("{Label} notify returned {StatusCode}", label, (int)resp.StatusCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "{Label} notify failed", label); }
    }

    private async Task PostDiscordAsync(string url, string message)
    {
        try
        {
            var payload = new
            {
                content = message,
                username = $"Conductor — {_plan.Name}",
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Discord notify returned {StatusCode}", (int)resp.StatusCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Discord notify failed"); }
    }

    private async Task PostSlackAsync(string url, string message)
    {
        try
        {
            var payload = new { text = message };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Slack notify returned {StatusCode}", (int)resp.StatusCode);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Slack notify failed"); }
    }
}
