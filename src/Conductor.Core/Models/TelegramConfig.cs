using Conductor.Core.Integrations.Messaging;

namespace Conductor.Models;

/// <summary>Telegram bot config for AFK observability + two-way control (B6).
/// Bot token is read from the <c>CONDUCTOR_TELEGRAM_TOKEN</c> environment variable (never committed).</summary>
public sealed class TelegramConfig
{
    /// <summary>Allowed chat IDs; an empty list means no commands are accepted (push-only).
    /// Use numeric IDs (int64 strings) — get them from @userinfobot on Telegram.</summary>
    public List<string> AllowedChatIds { get; set; } = new();

    /// <summary>KS11.2 / CHAPAR CH-2 — the same chats, with what each one is FOR. Supersedes
    /// <see cref="AllowedChatIds"/> for any chat it names; ids present only in the old list are
    /// admin chats, which is what they have always been.
    ///
    /// <para>Empty (the default) is the old shape and behaves byte-identically. The point of the
    /// block is that a stakeholder can be put in a chat the bot serves WITHOUT being handed
    /// <c>/inject</c> and the control verbs — which was impossible while permission was one flat
    /// list.</para></summary>
    public List<TelegramChatEntry> Chats { get; set; } = new();

    /// <summary>Every chat this bot may talk to, paired with its profile name, old shape and new
    /// merged — the ONE place that resolution happens.
    ///
    /// <para>Order is: the <see cref="Chats"/> block as written, then any <see cref="AllowedChatIds"/>
    /// entry it did not name, as admin. So an old-shape plan yields exactly its own list in its own
    /// order, all admin, which is the back-compat pin.</para>
    ///
    /// <para>An unreadable profile string resolves to null rather than to a default; callers that
    /// run before plan validation (there should be none) get a chat with no profile rather than a
    /// chat quietly promoted to admin. <see cref="ProfileRefusal"/> is what stops it reaching here.</para></summary>
    public IEnumerable<(string ChatId, string? Profile)> ResolvedChats()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Chats)
        {
            var id = entry.ChatId?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            yield return (id, entry.Profile);
        }

        foreach (var id in AllowedChatIds)
        {
            var trimmed = id?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed)) continue;
            yield return (trimmed, ChatProfiles.AdminName);
        }
    }

    /// <summary>KS11.2: how many chats this bot actually serves — the number every readiness and
    /// status answer must use. Counting <see cref="AllowedChatIds"/> alone would report a plan that
    /// configures its chats the new way as "push-only to nobody" while it delivered perfectly.</summary>
    public int ChatCount => ResolvedChats().Count();

    /// <summary>The plan-load refusal, in the shape <c>GithubConfig.BoardRefusal</c> established: a
    /// named complaint about the actual string, or null when there is nothing wrong.
    ///
    /// <para>Never a default. A profile the engine cannot read is a chat whose permissions the plan
    /// author does not know, and the failure mode of guessing is handing an outsider the steering
    /// wheel — so the run refuses to start instead.</para></summary>
    public string? ProfileRefusal()
    {
        foreach (var entry in Chats)
        {
            if (string.IsNullOrWhiteSpace(entry.ChatId))
                return "telegram.chats has an entry with no chatId — every chat needs a numeric id "
                     + "(get one from @userinfobot).";

            if (entry.Profile != null && string.IsNullOrWhiteSpace(entry.Profile))
                return $"telegram.chats entry '{entry.ChatId}' has an empty profile. "
                     + $"it is '{string.Join("' or '", ChatProfiles.Names)}'.";

            if (ChatProfiles.TryParse(entry.Profile ?? ChatProfiles.AdminName) == null)
                return $"telegram.chats entry '{entry.ChatId}' has profile '{entry.Profile}', "
                     + $"which is not a profile. it is '{string.Join("' or '", ChatProfiles.Names)}'.";
        }

        return null;
    }

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

/// <summary>KS11.2 / CHAPAR CH-2 — one chat and what it is for.</summary>
public sealed class TelegramChatEntry
{
    /// <summary>The numeric chat id, as a string (they exceed int32, and a group's is negative).</summary>
    public string ChatId { get; set; } = "";

    /// <summary><c>admin</c> or <c>observer</c>. Omitted means admin — the profile every chat has
    /// had until now — but a string the engine cannot read is refused at plan load, never guessed.</summary>
    public string? Profile { get; set; }
}
