using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>
/// DV2.3 — the inbound long-poll, and bug #38: what happens when this engine is not the only one
/// holding the bot token.
///
/// <para>Telegram allows exactly ONE <c>getUpdates</c> consumer per token. A second poller does not
/// get an error it can ignore: the API terminates the other request with <c>409 Conflict</c>, and
/// the two engines then take turns stealing each other's updates. Measured on this machine while
/// two conductors ran at once — inbound Telegram control was dead for the live run and NOTHING in
/// the log named the cause. <c>EnsureSuccessStatusCode</c> turned Telegram's own explanation into
/// "Response status code does not indicate success: 409 (Conflict)", the loop logged that at
/// warning level every poll interval, and the message that would have solved it in one read
/// ("make sure that only one bot instance is running") was in the response body, discarded.</para>
///
/// <para>Extracted to its own partial because <c>TelegramService.cs</c> was three lines under the
/// 500-line architecture ceiling; the next addition to it had to be a split, not an append.</para>
/// </summary>
public sealed partial class TelegramService
{
    /// <summary>How many consecutive 409s we are in. Reset by any successful poll, so a conflict
    /// that clears when the other engine stops costs nothing afterwards.</summary>
    private int _conflictStreak;

    /// <summary>The longest a conflict backs off for. A minute is long enough to stop two engines
    /// thrashing and short enough that the survivor picks inbound control straight back up when the
    /// other one exits.</summary>
    private static readonly TimeSpan MaxConflictBackoff = TimeSpan.FromMinutes(1);

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_cfg!.PollIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            var wait = interval;
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                await _surface.MaybeSendDailyDigestAsync(ct).ConfigureAwait(false);
                _lastPollUtc = DateTime.UtcNow;
                _lastError = null;
                _conflictStreak = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (TelegramConflictException ex)
            {
                wait = ConflictBackoff(++_conflictStreak);
                _lastError = ex.Message;
                // Loud ONCE, then quiet. A conflict can last as long as the other engine does, and an
                // error line every four seconds is how a diagnosis becomes wallpaper — but saying it
                // only at debug from the start is how the first one gets missed.
                if (_conflictStreak == 1)
                    _log.LogError("Telegram getUpdates conflict: {Reason} Backing off {Seconds}s.",
                        ex.Message, wait.TotalSeconds);
                else
                    _log.LogDebug("Telegram getUpdates still conflicted (poll {Streak}); backing off {Seconds}s.",
                        _conflictStreak, wait.TotalSeconds);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log.LogWarning(ex, "Telegram poll error");
            }
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Linear, capped, and deterministic — five seconds per consecutive conflict up to a
    /// minute. Deterministic on purpose: a test can state the delay, and an operator watching the
    /// log can tell a backoff from a hang.</summary>
    internal static TimeSpan ConflictBackoff(int streak)
    {
        var seconds = Math.Min(MaxConflictBackoff.TotalSeconds, 5.0 * Math.Max(1, streak));
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var url = $"{_apiBase}{_token}/getUpdates?offset={_offset}&timeout=30";
        var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);

        // #38: checked BEFORE EnsureSuccessStatusCode, because the body is where Telegram explains
        // itself and EnsureSuccessStatusCode throws that body away.
        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new TelegramConflictException(await ConflictReasonAsync(resp, ct).ConfigureAwait(false));

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TgResponse>(JsonOpts, ct).ConfigureAwait(false);
        if (body is not { Ok: true, Result: { Count: > 0 } updates }) return;

        foreach (var upd in updates)
        {
            _offset = upd.UpdateId + 1;
            await HandleUpdateAsync(upd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Telegram's own words about the conflict, plus what they mean for a conductor: the
    /// other consumer is another process holding this same token, and the fix is to stop it or give
    /// this run a token of its own. Never empty — a 409 with an unreadable body still says what a
    /// 409 means.</summary>
    private static async Task<string> ConflictReasonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var described = "";
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<TgResponse>(JsonOpts, ct).ConfigureAwait(false);
            if (body?.Description is { Length: > 0 } d) described = d;
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            // an unreadable body changes nothing about what a 409 means
        }

        return (described.Length > 0 ? described + " — " : "409 Conflict — ")
             + "another process is polling getUpdates with this same bot token. Telegram allows exactly one "
             + "consumer per token, so the two are stealing each other's updates and inbound control is "
             + "unreliable for both. Stop the other conductor, or give this run its own bot token.";
    }
}

/// <summary>A <c>getUpdates</c> 409. Its own type so the poll loop can back off and name the cause
/// instead of treating "somebody else has this token" as one more transport hiccup.</summary>
public sealed class TelegramConflictException : InvalidOperationException
{
    public TelegramConflictException() { }
    public TelegramConflictException(string message) : base(message) { }
    public TelegramConflictException(string message, Exception innerException) : base(message, innerException) { }
}
