namespace Conductor.Core.Integrations;

/// <summary>SC1.2: the single place that decides whether Telegram can actually deliver a push, and
/// what to say when it cannot. Three surfaces answer that question — <c>doctor</c> before a run,
/// <c>GET /telegram/status</c> during one, and <c>TelegramService.StartAsync</c>'s log line at
/// startup — and before this they each answered it in their own words, from their own conditions.
/// That is precisely how "configured" came to mean "working" everywhere while the feature was dead
/// (SC1.1): every surface was reporting a different, weaker half of the truth. Delivery needs ALL
/// of a telegram block, a bot token, at least one allowed chat id, and a started service; a surface
/// that checks fewer of those is not reporting delivery, it is reporting a prefix of it.</summary>
public static class TelegramReadiness
{
    public const string NoBlock =
        "not configured — optional; add a telegram block to the plan, or set it up from the Face's Telegram tab";
    public const string NoToken =
        "configured but no bot token — set CONDUCTOR_TELEGRAM_TOKEN, or save one from the Face's Telegram tab";
    public const string NoChatIds =
        "token present but no allowedChatIds — bot is push-only to nobody";
    public const string NotStarted =
        "configured, but the Telegram service is not running in this process — every push is dropped silently until it is started";

    /// <summary>The missing half, in doctor's own words, or <c>null</c> when Telegram will deliver.</summary>
    /// <param name="started">The one condition only a live process can answer: <c>null</c> from
    /// <c>doctor</c>, which runs outside the engine and must not claim to know.</param>
    public static string? MissingHalf(bool hasBlock, bool hasToken, int allowedChatIds, bool? started)
    {
        if (!hasBlock) return NoBlock;
        if (!hasToken) return NoToken;
        if (allowedChatIds == 0) return NoChatIds;
        if (started == false) return NotStarted;
        return null;
    }

    /// <summary>True only when every half is present. A caller that knows nothing about
    /// started-ness cannot get a true out of this — that is the point.</summary>
    public static bool WillDeliver(bool hasBlock, bool hasToken, int allowedChatIds, bool started) =>
        MissingHalf(hasBlock, hasToken, allowedChatIds, started) is null;
}

/// <summary>SC1.2: what <c>POST /telegram/test</c> actually did, not just whether it returned.
/// <paramref name="ViaQueue"/> is the part that matters: true means the message travelled the same
/// send queue a real run push travels, so a green result is evidence about the feature. False means
/// the test proved something weaker than it looks, and <paramref name="Detail"/> says what.</summary>
public sealed record TelegramTestOutcome(
    bool Ok, string? BotUsername, string? Error, bool ViaQueue, string? Detail);
