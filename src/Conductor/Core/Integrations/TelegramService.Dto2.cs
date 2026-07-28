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
}
