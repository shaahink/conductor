using Conductor.Core.Inbox;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>DV3.4 / findings §1.5 — which project a note is about, and what the sender is told about
/// the answer.
///
/// <para>Its own partial for the reason DV3.1's is: routing is not a command. The whole ladder —
/// reply to a push, this topic's project, this chat's project, the run that received it — happens
/// without the owner typing anything, and the ONE thing that makes that safe rather than spooky is
/// that every acknowledgement says where the note went and which rung decided it.</para>
///
/// <para>Nothing here can lose a note. A project that cannot be filed against parks the note in the
/// machine-level dead-letter box instead (§6.10), and the sender is told by name — refused, parked,
/// or filed, but never silently dropped.</para></summary>
public sealed partial class RemoteSurface
{
    /// <summary>Where this note belongs. With no router configured the answer is the run that
    /// received it, which is DV3.2's behaviour unchanged.</summary>
    private NoteRoute RouteOf(InboundNote note) =>
        _notes?.Route(note.ChatId, note.MessageThreadId, note.ReplyToText)
        ?? new NoteRoute(null, RouteReason.LocalRun, null);

    /// <summary>The inbox a route points at: the routed project's, or this run's own when routing is
    /// not configured or chose the local run anyway.</summary>
    private InboxStore? StoreFor(NoteRoute route)
    {
        if (route.Project is not { } project) return _notes is null ? _inbox : null;
        if (_inbox is { } local
            && string.Equals(Path.GetFullPath(local.Dir),
                             Path.GetFullPath(project.Inbox().Dir), StringComparison.OrdinalIgnoreCase))
            return local;   // the same directory: no move, no copy
        return project.Inbox();
    }

    /// <summary>DV3.4 / §1.5 (2) and (3) — <c>/project</c>: show the selection, or set it.
    ///
    /// <para>Setting it in a TOPIC sets the topic's, not the chat's, so a group with one topic per
    /// project routes without anybody typing again. An unknown name is refused with what this machine
    /// actually has — the <c>GithubConfig.Board</c> rule a third time — because a selection that
    /// silently did not take is how notes end up in the wrong project for a week.</para></summary>
    private async Task SelectProjectAsync(string chatId, long? threadId, string wanted, CancellationToken ct)
    {
        if (_notes is null)
        {
            await ReplyAsync(chatId,
                "This bot is bound to one run and does not route notes between projects.", null, ct)
                .ConfigureAwait(false);
            return;
        }

        if (wanted.Trim().Length == 0)
        {
            var current = _notes.Routes.Current(chatId, threadId);
            var resolved = current is { Length: > 0 } ? _notes.Projects.Resolve(current).Project : null;
            var line = resolved is { } p
                ? $"Notes here are filed against <b>{MessageComposer.EscapeHtml(p.Name)}</b>"
                  + (threadId is null ? " (this chat)." : " (this topic).")
                : _notes.Projects.Local is { } local
                    ? $"No project is selected, so notes are filed against the run on this machine, "
                      + $"<b>{MessageComposer.EscapeHtml(local.Name)}</b>."
                    : "No project is selected.";

            await ReplyAsync(chatId,
                line + "\nThis machine has: " + MessageComposer.EscapeHtml(_notes.Projects.Listed())
                     + "\nSet it with <code>/project &lt;name&gt;</code>.", null, ct).ConfigureAwait(false);
            return;
        }

        var match = _notes.Projects.Resolve(wanted);
        if (match.Project is not { } chosen)
        {
            await ReplyAsync(chatId, MessageComposer.EscapeHtml(match.Refusal ?? "No such project."),
                null, ct).ConfigureAwait(false);
            return;
        }

        _notes.Routes.Set(chatId, threadId, chosen.Slug);
        await ReplyAsync(chatId,
            $"Notes {(threadId is null ? "in this chat" : "in this topic")} now file against "
            + $"<b>{MessageComposer.EscapeHtml(chosen.Name)}</b> "
            + $"<i>({MessageComposer.EscapeHtml(chosen.RepoLeaf)})</i>. It stays until you change it."
            + (chosen.Present ? "" : "\n⚠️ That checkout is not on this disk right now; notes will be parked until it is back."),
            null, ct).ConfigureAwait(false);
    }
}
