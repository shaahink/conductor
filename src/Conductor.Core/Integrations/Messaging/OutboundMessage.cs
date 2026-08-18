namespace Conductor.Core.Integrations.Messaging;

/// <summary>K5.4 — how loudly a push should land. Telegram's <c>disable_notification</c> is the only
/// lever between "buzzes the owner's phone" and "appears in the chat"; before this every push used
/// the same one, so a routine progress line woke the owner exactly as hard as a run that had parked
/// waiting for them. The mapping is deliberately coarse — anything the owner cannot act on is
/// <see cref="Quiet"/>.</summary>
public enum PushSeverity
{
    /// <summary>Delivered silently. Session ends that advanced, evidence, progress, digests.</summary>
    Quiet = 0,

    /// <summary>Buzzes. The run needs the owner, or the run is over — the two cases where a delayed
    /// read costs something.</summary>
    Alert = 1,
}

/// <summary>A file to send INSTEAD of a text message: K5.3 registered evidence artifacts and pushed
/// their paths, which is useless from a phone. <paramref name="AsPhoto"/> selects
/// <c>sendPhoto</c> (inline, viewable in the chat) over <c>sendDocument</c> (an attachment) — a
/// screenshot is the case the whole item exists for, and a screenshot sent as a document is a
/// download prompt.</summary>
/// <param name="Path">Absolute path to the artifact on the machine running the engine.</param>
/// <param name="AsPhoto">True for an image Telegram will render inline.</param>
/// <param name="Caption">Caption text, already HTML-escaped and already within
/// the sending channel's caption limit.</param>
public sealed record OutboundAttachment(string Path, bool AsPhoto, string Caption);

/// <summary>One item on the send queue. This was a five-field tuple repeated at every declaration
/// and every write site; K5.4 needs two more fields (severity and an attachment) and a sixth and
/// seventh tuple element is where that stops being readable.</summary>
/// <param name="ChatId">The chat this copy is addressed to — one item per allowed chat.</param>
/// <param name="Text">The message body, WITHOUT the identity stamp, which is applied on the wire.</param>
/// <param name="Buttons">The buttons to offer, or null. KS11.1: this was a pre-serialised
/// Telegram <c>inline_keyboard</c> payload built during composition, which put one channel's wire
/// format in the middle of the seam. The buttons are now carried as themselves and serialised by
/// whichever adapter is about to send them.</param>
/// <param name="Ack">SC1.2's completion source: the send loop reports back so a queue-routed test can
/// answer its HTTP caller. Null for every ordinary fire-and-forget push.</param>
/// <param name="SessionNumber">The number the identity line carries — the RECORD's for a session-end
/// push, the live counter for everything else (K5.2).</param>
/// <param name="Severity">Notify or silent.</param>
/// <param name="Attachment">A file to send instead of text, or null.</param>
/// <param name="StageId">K5.4: the stage this message is ABOUT, which is not always the stage the
/// run is on — a session-end push composed after the run has moved on would otherwise be stamped
/// with the wrong stage, or with none.</param>
public sealed record OutboundMessage(
    string ChatId,
    string Text,
    IReadOnlyList<MessageButton>? Buttons = null,
    TaskCompletionSource<string?>? Ack = null,
    int? SessionNumber = null,
    PushSeverity Severity = PushSeverity.Quiet,
    OutboundAttachment? Attachment = null,
    string? StageId = null);

/// <summary>One offered action: what it says, and the opaque token that comes back when it is
/// pressed. KS11.1 — composition names buttons; only the adapter knows what an inline keyboard
/// looks like on the wire.</summary>
public readonly record struct MessageButton(string Text, string CallbackData);
