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

// SC1.3: RestartRequired is the one thing a live surface cannot fix by asking again — this engine
// process holds no Telegram service, so nothing saved here reaches the current run however valid it
// is. Everywhere else it is false, because a token or a telegram block saved against a live service
// now takes effect without a restart.

public sealed record TelegramStatusDto(
    bool Configured, bool Started, bool HasToken, IReadOnlyList<string> AllowedChatIds,
    int PollIntervalSeconds, bool EnableTwoWay, string? BotUsername, string? LastError, string? LastPollUtc,
    bool WillDeliver, string? WillDeliverReason, bool RestartRequired,
    // FU-OWNER-13: true while a plan edit this control plane accepted is still waiting for the run
    // loop's next session boundary. The Face shows *waiting*, not *unconfigured* — the two look
    // identical in every other field of this payload, which is exactly how a just-saved telegram
    // block read as "not configured".
    bool ReloadPending = false);

public sealed record TelegramTestResultDto(
    bool Ok, string? BotUsername, string? Error, bool ViaQueue, string? Detail);
