namespace Conductor.Core.Courier;

/// <summary>PK3.1 / D5 - one send, on protocol 3's wire. What <c>conductor say</c>, a session and a
/// watcher all hand the courier instead of dialling the messenger with a token of their own.
///
/// <para>It is NOT <see cref="CourierPush"/> grown a few fields, and the difference is who composed
/// the body. A push is the run's own message - it carries a severity, a session number and a stamp
/// the daemon prepends. A send is somebody's exact bytes: the text goes out as written, and the
/// stamp here is the ledger's note of who sent it, never a line added to the message. A dry run that
/// prints the bytes has to be printing what the chat receives.</para>
///
/// <para>Files travel as PATHS, for the reason the push's attachment does: the courier is on this
/// machine by construction, so it opens the file itself.</para></summary>
/// <param name="Chat">A chat id, or the name of a profile this courier lists exactly one chat under
/// (<c>admin</c>, <c>observer</c>). The answer's <see cref="CourierAck.ChatId"/> says which id it became.</param>
/// <param name="Text">The body - or, when files ride along, their caption.</param>
/// <param name="Photos">Files to render inline. Two or more go as one media group.</param>
/// <param name="Documents">Files to attach as they are. Two or more go as one media group; a group
/// is photos or documents, never both, because the messenger refuses the mix.</param>
/// <param name="ReplyTo">The message id this answers, in the same chat.</param>
/// <param name="ParseMode">HTML (the default), MarkdownV2, or none for plain text.</param>
/// <param name="Silent">Deliver without buzzing the phone.</param>
/// <param name="Buttons">Offered actions, as text and callback token.</param>
/// <param name="Stamp">Who composed this - a repo, a stage, a checkpoint - for the ledger. Not sent.</param>
/// <param name="Origin">Which process sent it - a run, <c>say</c>, a watcher - for the ledger and the log.</param>
/// <param name="Protocol">What the sender speaks. A newer one is refused by name.</param>
public sealed record CourierSend(
    string Chat,
    string? Text = null,
    IReadOnlyList<string>? Photos = null,
    IReadOnlyList<string>? Documents = null,
    long? ReplyTo = null,
    string? ParseMode = null,
    bool Silent = false,
    IReadOnlyList<CourierButton>? Buttons = null,
    string? Stamp = null,
    string? Origin = null,
    int Protocol = CourierProtocol.Version)
{
    /// <summary>Every file on the send, photos first, in the order the group will show them.</summary>
    public IReadOnlyList<string> Files => [.. Photos ?? [], .. Documents ?? []];
}

/// <summary>PK3.1 / D5 - a reaction on a message this machine can name.</summary>
/// <param name="Chat">A chat id or a profile name, as on <see cref="CourierSend"/>.</param>
/// <param name="MessageId">The message to react to.</param>
/// <param name="Emoji">The reaction. The messenger accepts a fixed set and refuses the rest by name.</param>
/// <param name="Origin">Which process asked, for the log.</param>
/// <param name="Protocol">What the sender speaks.</param>
public sealed record CourierReact(string Chat, long MessageId, string Emoji, string? Origin = null,
    int Protocol = CourierProtocol.Version);

/// <summary>PK3.1 / D5 - takes one message back. Recorded in the ledger as well as done, so the
/// ledger never lists an id as live that somebody deleted through this courier.</summary>
/// <param name="Chat">A chat id or a profile name, as on <see cref="CourierSend"/>.</param>
/// <param name="MessageId">The message to delete.</param>
/// <param name="Origin">Which process asked, for the ledger and the log.</param>
/// <param name="Protocol">What the sender speaks.</param>
public sealed record CourierDelete(string Chat, long MessageId, string? Origin = null,
    int Protocol = CourierProtocol.Version);
