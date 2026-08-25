using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations;

public sealed class TgResponse
{
    public bool Ok { get; set; }
    public List<TgUpdate>? Result { get; set; }

    /// <summary>DV2.3, bug #38: the Bot API's own explanation on a failure — for a 409 that is
    /// "Conflict: terminated by other getUpdates request; make sure that only one bot instance is
    /// running", the single most useful sentence anyone could have read while two engines fought
    /// over one token. It was on the wire the whole time and nothing deserialised it.</summary>
    public string? Description { get; set; }

    [JsonPropertyName("error_code")] public int? ErrorCode { get; set; }
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

    /// <summary>DV3.1 — until now this class was <c>message_id</c>, <c>text</c>, <c>chat</c> and
    /// nothing else, so a voice note was not refused, it was INVISIBLE (findings §1.2 gap 2). Each
    /// field below is one kind of message the owner can already send from the phone today.</summary>
    public string? Caption { get; set; }

    public TgFileRef? Voice { get; set; }
    public TgFileRef? Audio { get; set; }
    public TgFileRef? Document { get; set; }

    /// <summary>A photo arrives as the SAME image in several sizes, smallest first. The largest is
    /// the one worth keeping — see <c>TelegramService.Inbound</c>.</summary>
    public List<TgFileRef>? Photo { get; set; }

    /// <summary>The headline routing mechanism (findings §1.5): reply to a checkpoint push with a
    /// voice note and the note belongs to that push's project, with no command typed.</summary>
    [JsonPropertyName("reply_to_message")] public TgMessage? ReplyToMessage { get; set; }

    /// <summary>Forum topic, in a supergroup only. One topic per project is the stakeholder-group
    /// answer to the same routing question.</summary>
    [JsonPropertyName("message_thread_id")] public long? MessageThreadId { get; set; }
}
