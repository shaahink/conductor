using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Integrations;

/// <summary>KS11.1 / CHAPAR CH-1 — <c>TelegramService</c> AS a channel. Everything the seam is
/// allowed to know about this class is on this page: who it can reach, whether it is live, and the
/// two ways a message leaves. The rest of the type is Bot API plumbing the seam never sees.</summary>
public sealed partial class TelegramService
{
    // ── IMessageChannel: the transport, which is all this class is now ──

    /// <summary>KS11.1: also the source an injection from this channel is attributed to in the
    /// store, which is why it is the wire name rather than a display name.</summary>
    public string Name => "telegram";

    /// <summary>The loops are running. Every push guard in the engine has always been this flag; a
    /// chat list with nothing in it makes the fan-out a no-op instead, exactly as before.</summary>
    public bool IsLive => _started;

    public bool AllowsControl => _cfg?.EnableTwoWay == true;

    /// <summary>CH-2's profiles are not readable from a plan until KS11.2, so every configured chat
    /// is an admin chat — which is precisely what every configured chat is today.</summary>
    public IReadOnlyList<ChatTarget> Targets =>
        _cfg?.AllowedChatIds is { Count: > 0 } ids
            ? [.. ids.Select(id => new ChatTarget(id, ChatProfile.Admin))]
            : [];

    /// <summary>The one write path onto the send queue. Every real caller is fire-and-forget, so
    /// this must never throw: the queue is unbounded, so TryWrite never blocks, and it returns false
    /// once StopAsync has closed the channel.</summary>
    Task IMessageChannel.EnqueueAsync(OutboundMessage message, CancellationToken ct)
    {
        // SC1.3: read the queue field ONCE — a reload can swap it between the check and the write,
        // and a push split across two queues would be delivered twice or not at all.
        var queue = _sendQueue;
        if (!_started) return Task.CompletedTask;
        queue.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    Task IMessageChannel.SendAsync(OutboundMessage message, CancellationToken ct) => SendAsync(message, ct);
}
