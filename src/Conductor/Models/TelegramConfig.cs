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
}
