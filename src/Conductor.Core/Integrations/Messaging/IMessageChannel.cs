namespace Conductor.Core.Integrations.Messaging;

/// <summary>KS11.1 / CHAPAR CH-1 — the transport seam. Everything above this line (composition,
/// profiles, evidence browsing) is defined without knowing which messenger it is talking to;
/// everything below it is one messenger's wire protocol.
///
/// <para>Two verbs, not one, because the engine genuinely has two delivery paths and collapsing
/// them would not be behaviour-preserving: a PUSH fans out to every configured chat through an
/// ordered queue that survives a shutdown flush, and a REPLY answers one chat immediately because
/// something in that chat just asked. A reply routed through the push queue would arrive after the
/// backlog; a push sent directly would lose its ordering and its shutdown flush.</para>
///
/// <para>No second channel is built this era (CH-1 is explicit about it). The seam is proven by a
/// fake channel in tests — building Slack to demonstrate an interface is how scope dies.</para></summary>
public interface IMessageChannel
{
    /// <summary>What this channel is, for logs and for a test that wants to say which one it drove.</summary>
    string Name { get; }

    /// <summary>Whether this channel's loops are RUNNING — i.e. whether a message handed over now
    /// would go anywhere. False is the ordinary case (no messenger configured on the plan), and the
    /// surface asks before it composes rather than building a body nobody will read.</summary>
    bool IsLive { get; }

    /// <summary>Whether this channel accepts control verbs at all. Distinct from a chat's profile
    /// (CH-2): the profile says what THIS READER may ask for, this says whether the channel is wired
    /// for two-way traffic in the first place.</summary>
    bool AllowsControl { get; }

    /// <summary>The chats a push fans out to, each with the profile that decides what it may see and
    /// ask. Empty when nothing is configured.</summary>
    IReadOnlyList<ChatTarget> Targets { get; }

    /// <summary>Queue a push. Fire-and-forget by contract: every caller in the engine is
    /// <c>_ = Push…(…)</c>, so this must never throw and never block.</summary>
    Task EnqueueAsync(OutboundMessage message, CancellationToken ct);

    /// <summary>Deliver one message to one chat, now — a command answer or a digest.</summary>
    Task SendAsync(OutboundMessage message, CancellationToken ct);
}
