namespace Conductor.Core.Integrations;

/// <summary>No-op stub when Telegram is not configured. Its own file since K5.3, for the same reason
/// <see cref="SessionEndPush"/> is: the file it shared had four types and the ratchet allows three.</summary>
public sealed class NoOpTelegramService : ITelegramService
{
    /// <summary>This process holds no Telegram service at all, which is a state of its own and not
    /// "configured, not started" — <c>GET /telegram/status</c> reports it with the same constant.</summary>
    public string? DeliveryBlocker => TelegramReadiness.RestartRequired;

    public Task PushAsync(string message, Messaging.PushSeverity severity = Messaging.PushSeverity.Quiet,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task PushWithKeyboardAsync(string message,
        IReadOnlyList<(string Text, string CallbackData)> buttons, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushEvidenceAsync(IReadOnlyList<Evidence.EvidenceArtifact> artifacts,
        CancellationToken ct = default) => Task.CompletedTask;
}
