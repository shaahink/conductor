using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations;

public sealed class TgResponse
{
    public bool Ok { get; set; }
    public List<TgUpdate>? Result { get; set; }
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
