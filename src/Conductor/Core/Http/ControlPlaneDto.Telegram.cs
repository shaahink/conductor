namespace Conductor.Core.Http;

// M8.2: Telegram guided setup/status, surfaced to the Face so it can be configured entirely
// through the app instead of hand-editing plan.json/env vars. Token DTOs are in
// ControlPlaneDto.TelegramToken.cs (architecture ratchet: 3 types max per file).

public sealed record TelegramStatusDto(
    bool Configured, bool Started, bool HasToken, IReadOnlyList<string> AllowedChatIds,
    int PollIntervalSeconds, bool EnableTwoWay, string? BotUsername, string? LastError, string? LastPollUtc);

public sealed record TelegramTestResultDto(bool Ok, string? BotUsername, string? Error);
