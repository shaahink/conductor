namespace Conductor.Core.Integrations.Messaging;

/// <summary>DV3.1 — which kind of thing arrived. Read off the property the file came under on the
/// wire, never guessed from a MIME type: Telegram itself distinguishes a VOICE note (recorded in
/// the client, Opus, the payload this whole era exists for) from an AUDIO file (a track the sender
/// already had), and the two want different handling downstream.</summary>
public enum InboundMediaKind
{
    Voice,
    Audio,
    Document,
    Photo,
}

/// <summary>One file that came in with a message, after the adapter has tried to fetch it.
///
/// <para><see cref="LocalPath"/> and <see cref="Refusal"/> are the two halves of one answer and
/// exactly one of them is set: the bytes are on disk, or there is a sentence saying why they are
/// not. Neither is null-because-nothing-happened — a file that could not be fetched still arrives
/// here carrying its reason, because a bot that loses a message silently is the failure mode
/// findings §1.2 gap 2 describes.</para></summary>
/// <param name="Kind">Which property of the message it arrived under.</param>
/// <param name="FileId">The Bot API handle. Good for an hour on <c>getFile</c>, and the only way
/// back to the bytes.</param>
/// <param name="FileName">What to call it in a sentence to a human — the sender's own name for a
/// document, or a generated one for kinds that do not carry a name.</param>
/// <param name="SizeBytes">As declared on the wire, or as measured after download. 0 when the wire
/// declared nothing and the fetch never happened.</param>
/// <param name="DurationSeconds">Voice and audio only; 0 elsewhere.</param>
/// <param name="LocalPath">Where the bytes are, or null.</param>
/// <param name="Refusal">Why there are no bytes, in words that can be said to the sender, or
/// null.</param>
public sealed record InboundMedia(
    InboundMediaKind Kind,
    string FileId,
    string FileName,
    string? MimeType,
    long SizeBytes,
    int DurationSeconds,
    string? LocalPath,
    string? Refusal)
{
    /// <summary>Whether the bytes are actually on this machine.</summary>
    public bool Downloaded => LocalPath is { Length: > 0 };
}

/// <summary>DV3.1 — an inbound message as the SURFACE sees it: no Bot API types, no HTTP, nothing
/// that knows which messenger it came from. <c>TelegramService</c> builds one of these and the seam
/// takes it from there, which is what makes the courier (findings §1.4-B) a second producer of the
/// same record rather than a second copy of this logic.
///
/// <para>DV3.2 gives this a durable home; DV3.3 fills a transcript in beside the audio; DV3.4 turns
/// <see cref="ReplyToMessageId"/> and <see cref="MessageThreadId"/> into a project. Here it is only
/// received, acknowledged, and — when it is too big to fetch — refused by name.</para></summary>
/// <param name="Text">The message text, or the media's caption. May be empty.</param>
/// <param name="ReplyToMessageId">The message this one answers — for a reply to a conductor push,
/// the push's own id, which is how DV3.4 will find the project without a command.</param>
/// <param name="ReplyToText">What that message said, kept because the identity stamp inside it is
/// what names the run.</param>
/// <param name="UpdateId">DV3.2 — the CHANNEL's own update id, which is the inbox's dedup key: a
/// courier restart replays every update the messenger still holds (findings §6.2), and without this
/// the same voice note files twice. Distinct from <c>MessageId</c>, which identifies the message in
/// its chat rather than the delivery.</param>
public sealed record InboundNote(
    string ChatId,
    long MessageId,
    string Text,
    InboundMedia? Media,
    long? ReplyToMessageId,
    string? ReplyToText,
    long? MessageThreadId,
    long UpdateId = 0);
