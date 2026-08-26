using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Courier;

/// <summary>One delivery the courier has to deal with, already off the wire.
///
/// <para><see cref="InboundNote"/> is reused rather than mirrored, and that is the point KS11.1's
/// seam was built for: its own doc comment says a courier would be "a second producer of the same
/// record rather than a second copy of this logic". Everything DV3.4 routes on — the push this
/// answers, the forum topic it arrived in — is already a field on it.</para></summary>
/// <param name="UpdateId">The delivery's own id. What the durable offset advances past and what the
/// note is filed under, so a replay is a rename that fails rather than a second note.</param>
/// <param name="Note">The message, with its media already fetched or already refused by name. Null
/// when there is nothing here for a courier.</param>
/// <param name="Profile">What the sending chat is allowed to do. Resolved by the source, because the
/// source is what knows which chats this machine listed. Null means the chat is not listed at all.</param>
/// <param name="Command">The text of a slash command, without the slash, or null when this is a
/// note. A command and a note are different things: one steers the courier, the other is filed.</param>
public sealed record CourierDelivery(long UpdateId, InboundNote? Note, ChatProfile? Profile,
    string? Command = null)
{
    /// <summary>An update the courier will not act on — an unlisted chat, or a kind it has no use
    /// for. It is still a DELIVERY, and that is the point: the daemon has to advance its offset past
    /// it or the same message is fetched again every four seconds forever. Nobody is answered, which
    /// is deliberate — a bot that argues with a stranger has told them it exists.</summary>
    public static CourierDelivery Ignored(long updateId) => new(updateId, null, null);

    /// <summary>Whether there is anything here to file or answer.</summary>
    public bool Actionable => Note is not null && Profile is not null;
}

/// <summary>DV4.1 — where the courier's messages come from, and where its answers go.
///
/// <para>A seam, not a convenience. The courier's whole correctness claim is about ORDERING — that
/// the offset is written after the work, that a kill in between replays exactly one update, that the
/// replay files nothing twice — and none of that is testable against a real Bot API. Behind this
/// interface a test drives the same daemon with a stub that replays on demand; in front of it,
/// <c>TelegramCourierSource</c> is the only thing in the courier that knows what Telegram is.</para>
///
/// <para>It is deliberately NOT <c>IMessageChannel</c>. That seam is about a run pushing outward —
/// a queue, a fan-out, a flush at shutdown. This one is a poll and a reply, and conflating the two
/// would give the courier a send queue it has no run to flush.</para></summary>
public interface ICourierSource
{
    /// <summary>What to call this source in a status line — the bot's own name where the source can
    /// get one. Never the token.</summary>
    string Describe { get; }

    /// <summary>Everything from <paramref name="offset"/> onward that this source understands.
    ///
    /// <para>Asking with an offset is also the ACKNOWLEDGEMENT of everything below it, which is why
    /// the daemon only ever passes an offset it has already written to disk.</para></summary>
    Task<IReadOnlyList<CourierDelivery>> FetchAsync(long offset, CancellationToken ct);

    /// <summary>Answers one chat. Every path through the daemon ends in one of these or in a
    /// deliberate, documented silence — an unlisted chat, and a replay that was answered the first
    /// time.</summary>
    Task ReplyAsync(string chatId, string text, long? threadId, CancellationToken ct);

    /// <summary>DV4.3 — delivers one push handed over by a live run across the loopback seam, and
    /// returns null when it went out or the reason it did not.
    ///
    /// <para>It is on THIS interface and not <c>IMessageChannel</c>, and the distinction is the one
    /// the type remarks draw: a channel owns a queue and a shutdown flush because it belongs to a
    /// run that ends, while the daemon has neither and must not grow them — the run already queued
    /// this message and is waiting on the answer. A reason rather than a bool because the run prints
    /// it: DV1.1's rule is that a channel which cannot deliver says why, and the new hop is exactly
    /// where that could have been lost.</para></summary>
    Task<string?> SendAsync(CourierPush push, CancellationToken ct);
}

/// <summary>Somebody else is already consuming this source's messages.
///
/// <para>Findings §6.9's transition, in one type. Telegram allows exactly one <c>getUpdates</c>
/// consumer per bot token: the day the courier takes the token, any plan whose messenger block still
/// polls in-run fights it, and the two steal each other's updates. The daemon has to back off and
/// say so rather than treat it as one more transport hiccup — but it must do that WITHOUT knowing
/// which messenger imposed the rule, which is why the source translates its own 409 into this
/// instead of letting an adapter exception cross the seam.</para></summary>
public sealed class CourierConflictException : InvalidOperationException
{
    public CourierConflictException() { }
    public CourierConflictException(string message) : base(message) { }
    public CourierConflictException(string message, Exception innerException) : base(message, innerException) { }
}
