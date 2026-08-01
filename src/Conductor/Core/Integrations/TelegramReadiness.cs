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
    /// <summary>SC1.3: the one state a live reload genuinely cannot fix — this process has no
    /// Telegram service to reload, so nothing typed or edited here can reach the current run. Said
    /// out loud rather than left to look like success, which is what the token endpoint's
    /// unconditional "saved" used to do even when the save could never take effect.</summary>
    public const string RestartRequired =
        "no Telegram service exists in this engine process — the saved settings take effect on the next `conductor run`";
    /// <summary>FU-OWNER-13: the window between a saved plan edit and the run loop's next session
    /// boundary. The live <see cref="PlanConfig"/> is deliberately not mutated on the HTTP path, so
    /// during that window every reader of it — <c>GET /telegram/status</c>, <c>POST /telegram/token</c>
    /// — sees no telegram block and used to answer <see cref="NoBlock"/>: "add a telegram block to
    /// the plan", advising the edit that had just been accepted seconds earlier, and instructing a
    /// no-op. The behaviour was right and the sentence was wrong. This is the sentence for that
    /// window. It is the SC1.3 failure one layer out — a saved thing reporting as if nothing were
    /// saved — so it is said in the same shape as the rest of this class.</summary>
    public const string ReloadQueued =
        "a plan reload is queued — the telegram block you saved starts at the next session boundary";

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

/// <summary>SC1.3: what a live reload did to the running service. <paramref name="Changed"/> is
/// whether anything it runs on actually differs now; <paramref name="Started"/> and
/// <paramref name="WillDeliver"/> are the state AFTER the reload, so a caller never has to guess
/// whether "saved" also meant "working"; <paramref name="Message"/> is one sentence a surface can
/// print verbatim, drawn from <see cref="TelegramReadiness"/> so it matches doctor's words.</summary>
public sealed record TelegramReloadOutcome(bool Changed, bool Started, bool WillDeliver, string Message);

/// <summary>SC1.2: what <c>POST /telegram/test</c> actually did, not just whether it returned.
/// <paramref name="ViaQueue"/> is the part that matters: true means the message travelled the same
/// send queue a real run push travels, so a green result is evidence about the feature. False means
/// the test proved something weaker than it looks, and <paramref name="Detail"/> says what.</summary>
public sealed record TelegramTestOutcome(
    bool Ok, string? BotUsername, string? Error, bool ViaQueue, string? Detail);
