namespace Conductor.Core.Http;

// M8.2: POST /telegram/token — saves a bot token to the local secrets store (SecretsStore), never
// to the versioned plan file. Split from ControlPlaneDto.Telegram.cs (architecture ratchet: 3
// types max per file).

public sealed record TelegramSetTokenRequestDto(string Token);

// SC1.3: Ok means the token was saved; WillDeliver means the running engine can now actually notify
// somebody with it. They are different questions, and collapsing them into one green tick is how a
// saved token on a service that could never load it read as success.

public sealed record TelegramSetTokenResultDto(bool Ok, string? Message, bool WillDeliver);
