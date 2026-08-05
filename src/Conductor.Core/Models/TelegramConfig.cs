namespace Conductor.Models;

/// <summary>Telegram bot config for AFK observability + two-way control (B6).
/// Bot token is read from the <c>CONDUCTOR_TELEGRAM_TOKEN</c> environment variable (never committed).</summary>
public sealed class TelegramConfig
{
    /// <summary>Allowed chat IDs; an empty list means no commands are accepted (push-only).
    /// Use numeric IDs (int64 strings) — get them from @userinfobot on Telegram.</summary>
    public List<string> AllowedChatIds { get; set; } = new();

    /// <summary>How often to poll getUpdates when idle (seconds). Default 4.</summary>
    public int PollIntervalSeconds { get; set; } = 4;

    /// <summary>If true, write control.json on callback queries from allowed chats (B6.2).
    /// Default false until B6.2 lands.</summary>
    public bool EnableTwoWay { get; set; }

    /// <summary>SC1.1: root of the Bot API to talk to, without the trailing <c>/bot</c> segment.
    /// Null (the default) means Telegram's own <c>https://api.telegram.org</c>. Telegram publishes a
    /// self-hostable Bot API server, so this is a supported deployment knob — and it is also the seam
    /// that lets a test stand a stub in front of the service and assert what actually went on the
    /// wire, instead of asserting that a mock of our own code was called.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>K5.4: the forum topic every push of this run belongs to, when the chat is a forum
    /// supergroup. Null — the ordinary case — means the run threads itself instead, by replying to
    /// its own first message, which is the only way to group a run in a non-forum chat.</summary>
    public long? MessageThreadId { get; set; }
}
