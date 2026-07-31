namespace Conductor.Core.Http;

// M8.2: Telegram guided setup/status, surfaced to the Face so it can be configured entirely
// through the app instead of hand-editing plan.json/env vars. Token DTOs are in
// ControlPlaneDto.TelegramToken.cs (architecture ratchet: 3 types max per file).

// SC1.2: WillDeliver is the derived verdict the older fields could not express — Configured, Started
// and HasToken were each true on runs that delivered nothing, because delivery needs all of them AND
// a chat id to send to. WillDeliverReason carries doctor's own sentence for the missing half
// (Core.Integrations.TelegramReadiness owns both), so status, doctor and the startup log agree.
// ViaQueue answers the same question for the test button: did this go through the real send queue,
// or a parallel path that works when the feature does not?

public sealed record TelegramStatusDto(
    bool Configured, bool Started, bool HasToken, IReadOnlyList<string> AllowedChatIds,
    int PollIntervalSeconds, bool EnableTwoWay, string? BotUsername, string? LastError, string? LastPollUtc,
    bool WillDeliver, string? WillDeliverReason);

public sealed record TelegramTestResultDto(
    bool Ok, string? BotUsername, string? Error, bool ViaQueue, string? Detail);
