using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Courier;

/// <summary>PK5.2 / D10 - how the courier routes a figure verb: <see cref="CourierFigures"/> reads, this
/// decides who may ask and which project they mean.
///
/// <para>Who may ask is <see cref="SurfaceCommands"/>' rule, not a new one: the figure verbs are Browse,
/// so every listed chat may ask them - the observer included - and an unlisted chat still gets
/// silence, because it never reaches here. Which project is the note's own routing: the push the ask
/// replies to names one, else the chat's <c>/project</c> selection, else the only project on the
/// machine. What is answered is read-only by construction (ADR-0005, ADR-0008): nothing on this path
/// holds a writable store, the state catalogue, or the inbox.</para></summary>
public sealed partial class CourierDaemon
{
    /// <summary>Answers <paramref name="command"/> when it is a figure verb this chat may ask; false
    /// leaves it to the note path.</summary>
    private async Task<bool> AnswerFigureAsync(InboundNote note, ChatProfile profile, string command, CancellationToken ct)
    {
        var cut = command.IndexOf(' ', StringComparison.Ordinal);
        var verb = (cut < 0 ? command : command[..cut]).Trim().ToLowerInvariant();
        if (!CourierFigures.Answers(verb)) return false;
        if (SurfaceCommands.Find("/" + verb) is not { } surface || !surface.AllowedFor(profile)) return false;

        var route = _router.Route(note.ChatId, note.MessageThreadId, note.ReplyToText);
        if (route.Project is not { } project)
        {
            await ReplyAsync(note,
                MessageComposer.EscapeHtml(route.Refusal ?? "No project is selected for this chat.")
                + "\nThis courier carries: " + MessageComposer.EscapeHtml(_router.Projects.Listed())
                + "\nChoose one with <code>/project &lt;name&gt;</code>, or ask as a reply to one of its pushes.",
                ct).ConfigureAwait(false);
            return true;
        }

        string text;
        try
        {
            text = CourierFigures.Answer(verb, cut < 0 ? "" : command[(cut + 1)..], project, _root, _log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            _log($"courier: /{verb} for {project.Name} could not be read: {ex.GetType().Name}: {ex.Message}");
            text = $"Could not read <b>{MessageComposer.EscapeHtml(project.Name)}</b>'s run to answer /{verb}: "
                 + MessageComposer.EscapeHtml(ex.Message);
        }

        await ReplyAsync(note, text, ct).ConfigureAwait(false);
        return true;
    }
}
