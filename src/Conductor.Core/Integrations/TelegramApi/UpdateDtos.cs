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
}
