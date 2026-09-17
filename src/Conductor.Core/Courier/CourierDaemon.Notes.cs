using System.Globalization;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Courier;

/// <summary>PK5.1 / D9 - how the courier answers a filed note: with a reaction on the note itself, and
/// with words only when somebody asks for them.
///
/// <para>The room asked for the "Note received" posts to go (findings F-COUR-6), and its own rule is
/// that a reaction IS the acknowledgement. So a filed note is answered by <see cref="InboundAck.Reaction"/>
/// on its own message id and nothing else. What the old post also carried - which project took the
/// note, and the promote button - moves to the reply to <c>/note</c>, which the sender sends as a reply
/// to their note (or bare, for the newest one this chat filed).</para>
///
/// <para>A refusal is not an acknowledgement, and stays a message: a file too big to fetch, a note that
/// could only be parked, an observer that may not file. Each of those is something the sender has to
/// act on, and a reaction cannot say what.</para></summary>
public sealed partial class CourierDaemon
{
    /// <summary>The command a sender asks with. Without the slash, as the source hands it over.</summary>
    internal const string NoteVerb = "note";

    /// <summary>A filed note, answered. The reaction lands on the note's own message; a refused
    /// reaction is logged and NOT turned into a message - a message is what D9 took away.</summary>
    private async Task AcknowledgeAsync(InboundNote note, ProjectRef project, CancellationToken ct)
    {
        if (note.Media?.Refusal is { Length: > 0 } refusal)
            await ReplyAsync(note, refusal, ct).ConfigureAwait(false);

        var why = await _source.ReactAsync(note.ChatId, note.MessageId, InboundAck.Reaction, ct).ConfigureAwait(false);
        if (why is not null)
            _log($"courier: note {NoteId(note).ToString(CultureInfo.InvariantCulture)} is filed against "
               + $"{project.Name}, but the reaction on message {note.MessageId.ToString(CultureInfo.InvariantCulture)} "
               + $"was refused: {why}");
    }

    /// <summary><c>/note</c> - which note, where it went and who sent it, with the promote button. As a
    /// reply to a note it answers for that note; bare, for the newest note this chat filed.</summary>
    private async Task DescribeNoteAsync(InboundNote ask, CancellationToken ct)
    {
        var projects = _router.Projects.All();
        var (found, project) = ask.ReplyToMessageId is { } replied
            ? NoteLookup.ByMessage(projects, ask.ChatId, replied)
            : NoteLookup.NewestFrom(projects, ask.ChatId);
        if (found is null || project is null)
        {
            await ReplyAsync(ask, ask.ReplyToMessageId is null
                ? "No note from this chat is filed on this machine."
                : "That message is not a note this courier filed. <code>/note</code> answers as a reply to the note itself.",
                ct).ConfigureAwait(false);
            return;
        }

        var head = "📝 Note <code>" + found.Id.ToString(CultureInfo.InvariantCulture) + "</code>"
                 + (found.MessageId is { } message ? " · message " + message.ToString(CultureInfo.InvariantCulture) : "")
                 + (found.Sender is { } sender ? " · from " + MessageComposer.EscapeHtml(sender) : "");
        var text = found.Text.Trim();
        var body = text.Length == 0 ? "" : "\n<i>" + MessageComposer.EscapeHtml(text.Length <= 200 ? text : text[..199] + "…") + "</i>";

        await ReplyAsync(ask, head + "\n" + InboundAck.FiledAgainst(project.Name) + body, ct,
            [new CourierButton(NotePromoter.ButtonText, NotePromoter.Callback(project.Slug, found.Id))])
            .ConfigureAwait(false);
    }
}
