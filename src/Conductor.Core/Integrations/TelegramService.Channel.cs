using Conductor.Core.Courier;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

namespace Conductor.Core.Integrations;

/// <summary>KS11.1 / CHAPAR CH-1 — <c>TelegramService</c> AS a channel. Everything the seam is
/// allowed to know about this class is on this page: who it can reach, whether it is live, and the
/// two ways a message leaves. The rest of the type is Bot API plumbing the seam never sees.</summary>
public sealed partial class TelegramService
{
    /// <summary>DV1.2 — the owner-queue push, straight through to the surface that knows which
    /// chats may act. It lives on this page rather than beside the other IRunNotifier delegates
    /// because TelegramService.cs stands at the 500-line architecture ceiling.</summary>
    public Task PushOwnerQueueAsync(IReadOnlyList<OwnerQueueItem> items, DateTime nowUtc,
        CancellationToken ct = default) => _surface.PushOwnerQueueAsync(items, nowUtc, ct);

    // ── IMessageChannel: the transport, which is all this class is now ──

    /// <summary>KS11.1: also the source an injection from this channel is attributed to in the
    /// store, which is why it is the wire name rather than a display name.</summary>
    public string Name => "telegram";

    /// <summary>The loops are running. Every push guard in the engine has always been this flag; a
    /// chat list with nothing in it makes the fan-out a no-op instead, exactly as before.</summary>
    public bool IsLive => _started;

    /// <summary>Two-way traffic needs a poll loop, and in courier mode there is not one: the daemon
    /// is the single consumer of the token's updates (findings 6.9), so a run that still claimed to
    /// accept control verbs would be advertising a surface nothing is reading.</summary>
    public bool AllowsControl => _courier is null && _cfg?.EnableTwoWay == true;

    /// <summary>KS11.2 / CH-2: every chat this bot serves and what each one is for, resolved from
    /// the plan's <c>chats</c> block merged over the old <c>allowedChatIds</c> list.
    ///
    /// <para>An old-shape plan yields its own ids in its own order, all admin — which is exactly
    /// what this property returned before the block existed. A profile string that got past plan
    /// validation unread would be a bug, and is treated as one: the chat is DROPPED rather than
    /// promoted, so the failure is a chat that hears nothing, not a chat that can steer.</para></summary>
    public IReadOnlyList<ChatTarget> Targets => ResolveTargets(_cfg);

    internal static IReadOnlyList<ChatTarget> ResolveTargets(TelegramConfig? cfg)
    {
        if (cfg == null) return [];
        var targets = new List<ChatTarget>();
        foreach (var (chatId, profileName) in cfg.ResolvedChats())
        {
            var profile = ChatProfiles.TryParse(profileName ?? ChatProfiles.AdminName);
            if (profile != null) targets.Add(new ChatTarget(chatId, profile.Value));
        }

        return targets;
    }

    /// <summary>The one write path onto the send queue. Every real caller is fire-and-forget, so
    /// this must never throw: the queue is unbounded, so TryWrite never blocks, and it returns false
    /// once StopAsync has closed the channel.</summary>
    Task IMessageChannel.EnqueueAsync(OutboundMessage message, CancellationToken ct)
    {
        // SC1.3: read the queue field ONCE — a reload can swap it between the check and the write,
        // and a push split across two queues would be delivered twice or not at all.
        var queue = _sendQueue;
        if (!_started) return Task.CompletedTask;

        // DV4.3: the one line that makes a live run push THROUGH the daemon. Null on a courier-less
        // machine, so the queue below is reached exactly as before.
        if (_courier is { } courier) return courier.EnqueueAsync(message, ct);

        queue.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    Task IMessageChannel.SendAsync(OutboundMessage message, CancellationToken ct) =>
        _courier is { } courier ? courier.SendAsync(message, ct) : SendAsync(message, ct);
}
