using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations;

public sealed class TgChat
{
    public long Id { get; set; }
    public string? Type { get; set; }
}

public sealed class TgCallbackQuery
{
    public string Id { get; set; } = "";
    public TgUser? From { get; set; }
    public TgMessage? Message { get; set; }
    public string? Data { get; set; }
}

public sealed class TgUser
{
    public long Id { get; set; }
    public string? Username { get; set; }

    /// <summary>PK5.1 - the name a note is addressed back to. Telegram always sends a first name;
    /// the last name is optional.</summary>
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("last_name")] public string? LastName { get; set; }
}
