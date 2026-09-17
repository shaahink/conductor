namespace Conductor.Core.Inbox;

/// <summary>PK5.1 / D9 - a note found across every inbox a machine serves, by the id it was filed under
/// or by the message it was.
///
/// <para>Two askers, one search: the courier's <c>/note</c> finds the note a reply points at, and
/// <c>conductor say --reply-to</c> turns a note id into the message id Telegram answers. Both search
/// every project rather than one route, because a chat's route can move after a note is filed and the
/// note does not move with it.</para>
///
/// <para>What is found is for ADDRESSING - which message to answer, in which chat. It never decides
/// what anybody may do (findings F-OBS-4).</para></summary>
public static class NoteLookup
{
    /// <summary>The note filed under <paramref name="noteId"/>, and the project holding it.</summary>
    public static (InboxNote? Note, ProjectRef? Project) ById(IEnumerable<ProjectRef> projects, long noteId)
    {
        ArgumentNullException.ThrowIfNull(projects);
        foreach (var project in projects.Where(p => p.Present))
        {
            if (project.Inbox().Find(noteId) is { } note) return (note, project);
        }

        return (null, null);
    }

    /// <summary>The note message <paramref name="messageId"/> of <paramref name="chatId"/> became.</summary>
    public static (InboxNote? Note, ProjectRef? Project) ByMessage(IEnumerable<ProjectRef> projects, string chatId, long messageId)
    {
        ArgumentNullException.ThrowIfNull(projects);
        foreach (var project in projects.Where(p => p.Present))
        {
            if (project.Inbox().FindByMessage(chatId, messageId) is { } note) return (note, project);
        }

        return (null, null);
    }

    /// <summary>The newest note <paramref name="chatId"/> filed anywhere on this machine.</summary>
    public static (InboxNote? Note, ProjectRef? Project) NewestFrom(IEnumerable<ProjectRef> projects, string chatId)
    {
        ArgumentNullException.ThrowIfNull(projects);
        (InboxNote? Note, ProjectRef? Project) best = (null, null);
        foreach (var project in projects.Where(p => p.Present))
        {
            var newest = project.Inbox().All().LastOrDefault(n => string.Equals(n.ChatId, chatId, StringComparison.Ordinal));
            if (newest is not null && (best.Note is null || newest.ReceivedUtc > best.Note.ReceivedUtc))
                best = (newest, project);
        }

        return best;
    }
}
