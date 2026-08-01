using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Core.Watch;

/// <summary>One attempted delivery, in the words that go to stderr.</summary>
/// <param name="Target">What was written to — <c>webhook</c>, <c>telegram</c>. Never a URL: a webhook
/// URL routinely carries its own secret in the path, and a Telegram URL always carries the bot token.</param>
/// <param name="Delivered">True only if the far end accepted it.</param>
/// <param name="Detail">Status code, failure, or the reason nothing was attempted.</param>
public sealed record RemoteDelivery(string Target, bool Delivered, string Detail);

/// <summary>What the watch did about remote supervision on this wake.</summary>
/// <param name="Deliveries">One row per target attempted.</param>
/// <param name="Skipped">Null when a dispatch was attempted; otherwise why nothing left the box.</param>
public sealed record RemoteDispatch(IReadOnlyList<RemoteDelivery> Deliveries, string? Skipped)
{
    /// <summary>Nothing configured at all — the ordinary case, and the one that prints nothing.</summary>
    public static readonly RemoteDispatch None = new([], null);

    public bool Attempted => Deliveries.Count > 0;

    public bool AnyDelivered => Deliveries.Any(d => d.Delivered);
}

/// <summary>
/// SF5.3 — the wake leaves the machine.
///
/// <para>SF5.2's supervisor command assumes the babysitter is on the same box as the run. The owner's
/// actual supervisors are not: a phone, and a cloud Claude Code session with repo access. This sends the
/// SAME brief the local supervisor reads on stdin to a webhook, and a compact wake line to Telegram —
/// on the wake set only, never on the timeout heartbeat.</para>
///
/// <para>Two rules hold this honest, and both are the opposite of what a naive version would do:</para>
/// <list type="number">
///   <item>The remote goes out BEFORE the local supervisor command and does not care whether that command
///   is disabled, missing or rate limited. The hour in which a local babysitter has burnt its fuse is
///   exactly the hour a human off the box needs to hear about it.</item>
///   <item>A failed delivery is reported and swallowed. A parked run whose watch crashed because a
///   webhook was down is two outages, and the second one is ours.</item>
/// </list>
/// </summary>
public static partial class WatchRemote
{
    /// <summary>Telegram rejects anything past 4096 characters; a wake line that long is a bug anyway.</summary>
    public const int TelegramMaxChars = 3500;

    /// <summary>Deliver the wake off-box. <paramref name="notifyOverride"/> is the command line's
    /// <c>--notify</c>: like <c>--hook</c> it wins over the plan and is not bound by the plan's fuse,
    /// because an operator typing a URL at a live run is making a deliberate one-off decision.</summary>
    public static async Task<RemoteDispatch> DispatchAsync(
        PlanConfig plan,
        JsonObject brief,
        string briefJson,
        string? notifyOverride,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(brief);

        var cfg = plan.Supervisor?.Remote;
        var oneOff = !string.IsNullOrWhiteSpace(notifyOverride);

        if (cfg is null && !oneOff) return RemoteDispatch.None;
        if (!oneOff)
        {
            if (!cfg!.Enabled)
                return new RemoteDispatch([], "remote supervision disabled in the plan");
            if (string.IsNullOrWhiteSpace(cfg.WebhookUrl) && !cfg.Telegram)
                return new RemoteDispatch([], "remote block names no webhookUrl and no telegram");

            if (cfg.MaxPerHour > 0)
            {
                var recent = SupervisorPolicy.CountRecentFires(
                    plan.StateDir, TimeSpan.FromHours(1), nowUtc, SupervisorPolicy.RemoteFiresFile);
                if (recent >= cfg.MaxPerHour)
                    return new RemoteDispatch([], $"rate limited: {recent} remote dispatch(es) this hour, " +
                        $"cap {cfg.MaxPerHour} (raise supervisor.remote.maxPerHour)");
            }
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg?.TimeoutSeconds ?? 20, 1, 300));
        using var http = new HttpClient { Timeout = timeout };
        var rows = new List<RemoteDelivery>();

        if (oneOff)
            rows.Add(await PostBriefAsync(http, notifyOverride!, null, briefJson, ct).ConfigureAwait(false));
        else if (!string.IsNullOrWhiteSpace(cfg!.WebhookUrl))
            rows.Add(await PostBriefAsync(http, cfg.WebhookUrl!, cfg.Headers, briefJson, ct).ConfigureAwait(false));

        // --notify replaces the WHOLE block, phone included, for the same reason --hook replaces the
        // whole supervisor: an operator aiming one wake at one URL from a terminal has not asked to
        // also ring the owner, and a one-off that spams a phone stops being used.
        if (!oneOff && cfg is { Telegram: true })
            rows.AddRange(await SendTelegramAsync(http, plan, brief, ct).ConfigureAwait(false));

        // Stamped when the plan's block attempted something, delivered or not: a fuse that only counts
        // successes does not bound a webhook that is failing on every wake, which is when a run stuck on
        // one cause would otherwise hammer it all night.
        if (!oneOff && rows.Count > 0)
            SupervisorPolicy.RecordFire(plan.StateDir, nowUtc, SupervisorPolicy.RemoteFiresFile);

        return new RemoteDispatch(rows, null);
    }

    /// <summary>The wake as a phone-sized message: what fired, where, what it is costing, what to do.
    /// Deliberately not the whole brief — a person reading this on a lock screen needs the decision, and
    /// the brief is waiting in the webhook body for whatever picks it up.</summary>
    public static string TelegramText(JsonObject brief)
    {
        ArgumentNullException.ThrowIfNull(brief);

        string S(string key) => brief[key]?.ToString() ?? "";

        var sb = new StringBuilder();
        sb.Append("conductor wake: ").Append(S("reason")).Append('\n');
        sb.Append(S("plan"));
        var stage = S("stage");
        if (stage.Length > 0) sb.Append("  stage ").Append(stage);
        var attempt = S("attempt");
        if (attempt.Length > 0 && !string.Equals(attempt, "0", StringComparison.Ordinal))
            sb.Append(" (attempt ").Append(attempt).Append(')');
        sb.Append('\n');

        var spend = S("spendUsd");
        var cap = S("costCapUsd");
        if (spend.Length > 0)
        {
            sb.Append("spend $").Append(spend);
            if (cap.Length > 0) sb.Append(" of $").Append(cap);
        }

        var checkpoints = S("checkpoints");
        if (checkpoints.Length > 0) sb.Append("   checkpoints ").Append(checkpoints);
        sb.Append('\n');

        var detail = S("detail");
        if (detail.Length > 0) sb.Append('\n').Append(detail).Append('\n');

        if (brief["suggest"] is JsonArray suggest && suggest.Count > 0)
            sb.Append("\nnext: ").Append(string.Join("  |  ", suggest.Select(v => v?.ToString() ?? "")));

        var text = sb.ToString();
        return text.Length <= TelegramMaxChars ? text : text[..TelegramMaxChars];
    }

    /// <summary>Expand <c>${NAME}</c> and <c>%NAME%</c> from the environment so a plan can name a
    /// credential without containing one. Returns null when the variable is not set — the caller drops
    /// the header and says so, because posting the literal <c>${TOKEN}</c> earns a 401 whose cause is
    /// invisible from the far end.</summary>
    public static string? ExpandEnv(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = value;

        foreach (var (token, name) in FindRefs(value))
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(v)) return null;
            result = result.Replace(token, v, StringComparison.Ordinal);
        }

        return result;
    }

    private static IEnumerable<(string Token, string Name)> FindRefs(string value)
    {
        foreach (System.Text.RegularExpressions.Match m in EnvRef().Matches(value))
            yield return (m.Value, m.Groups["brace"].Success ? m.Groups["brace"].Value : m.Groups["pct"].Value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\$\{(?<brace>[A-Za-z_][A-Za-z0-9_]*)\}|%(?<pct>[A-Za-z_][A-Za-z0-9_]*)%",
        System.Text.RegularExpressions.RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex EnvRef();

    private static async Task<RemoteDelivery> PostBriefAsync(
        HttpClient http, string url, Dictionary<string, string>? headers, string briefJson, CancellationToken ct)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "(unparseable url)";
        if (u is null) return new RemoteDelivery("webhook", false, $"not a URL: {url}");

        var dropped = new List<string>();
        var sw = Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(briefJson, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, u) { Content = content };
            if (headers != null)
            {
                foreach (var (k, v) in headers)
                {
                    var expanded = ExpandEnv(v);
                    if (expanded is null) { dropped.Add(k); continue; }
                    req.Headers.TryAddWithoutValidation(k, expanded);
                }
            }

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var note = dropped.Count == 0 ? "" :
                $" [dropped header(s) {string.Join(", ", dropped)}: env var not set]";
            return new RemoteDelivery("webhook", resp.IsSuccessStatusCode,
                $"{host} {(int)resp.StatusCode} in {sw.Elapsed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s{note}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new RemoteDelivery("webhook", false, $"{host} failed after " +
                $"{sw.Elapsed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s — {ex.Message}");
        }
    }

    private static async Task<List<RemoteDelivery>> SendTelegramAsync(
        HttpClient http, PlanConfig plan, JsonObject brief, CancellationToken ct)
    {
        var rows = new List<RemoteDelivery>();
        var cfg = plan.Telegram;
        if (cfg is null)
            return [new RemoteDelivery("telegram", false, "no telegram block in the plan")];

        var token = TelegramService.ResolveToken(plan);
        if (string.IsNullOrWhiteSpace(token))
            return [new RemoteDelivery("telegram", false, "no bot token (CONDUCTOR_TELEGRAM_TOKEN or the secrets file)")];
        if (cfg.AllowedChatIds.Count == 0)
            return [new RemoteDelivery("telegram", false, "telegram.allowedChatIds is empty")];

        var root = string.IsNullOrWhiteSpace(cfg.ApiBaseUrl)
            ? TelegramService.DefaultApiRoot : cfg.ApiBaseUrl!.Trim();
        var url = root.TrimEnd('/') + "/bot" + token + "/sendMessage";
        var text = TelegramText(brief);

        foreach (var chat in cfg.AllowedChatIds)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var payload = JsonSerializer.Serialize(new { chat_id = chat, text, disable_web_page_preview = true });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync(new Uri(url), content, ct).ConfigureAwait(false);
                rows.Add(new RemoteDelivery("telegram", resp.IsSuccessStatusCode,
                    $"chat {chat} {(int)resp.StatusCode} in {sw.Elapsed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                rows.Add(new RemoteDelivery("telegram", false, $"chat {chat} failed — {ex.Message}"));
            }
        }

        return rows;
    }
}
