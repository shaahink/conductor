namespace Conductor.Core.Integrations.Messaging;

/// <summary>DV3.1 — the surface's inbound half: what happens when the thing that arrived is not
/// text.
///
/// <para>Its own partial because <see cref="RemoteSurface"/> is a command surface and this is not
/// a command. Nothing here routes, arms, confirms or steers: a note is a RECORD (findings §1.7),
/// and the deliberate distance between "the owner said something" and "the engine did something"
/// is the safety property this whole strand rests on. A transcript that could pause a run is a
/// misheard word away from pausing a run.</para>
///
/// <para>What DV3.1 does with a note is acknowledge it — by name, saying what arrived and whether
/// the bytes made it. DV3.2 gives it a durable home under <c>.conductor/inbox</c> and this method
/// is where that write goes; the acknowledgement is already written to say it.</para></summary>
public sealed partial class RemoteSurface
{
    /// <summary>One inbound note, acknowledged. The channel has already fetched (or refused) the
    /// media, so this method never touches the network and never blocks on one.</summary>
    /// <param name="profile">The sending chat's profile. Filing is admin-only — see
    /// <see cref="ChatProfiles.MayFile"/>, which is also what stopped the bytes being fetched.</param>
    public async Task HandleNoteAsync(InboundNote note, ChatProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);

        if (!ChatProfiles.MayFile(profile))
        {
            // Said out loud rather than ignored: an observer who sends a voice note into silence
            // cannot tell "not allowed" from "broken", and will send it again.
            await ReplyAsync(note.ChatId, InboundAck.NotYours(profile), null, ct).ConfigureAwait(false);
            return;
        }

        // Filed BEFORE it is acknowledged, so the acknowledgement is never a claim the disk did not
        // back: if the write throws, the sender hears nothing rather than "kept" about a note that
        // is not.
        var filed = FileNote(note);

        var ack = InboundAck.For(note);
        if (ack.Length == 0) return;
        if (!filed) return;   // a duplicate delivery (findings §6.2) - already answered once

        await ReplyAsync(note.ChatId, ack, null, ct).ConfigureAwait(false);
    }

    /// <summary>Writes the note to the project's inbox. False when it was ALREADY there — the
    /// ordinary outcome of a courier replaying updates after a restart, and the reason the sender is
    /// not acknowledged twice for one voice note.</summary>
    private bool FileNote(InboundNote note)
    {
        if (_inbox is null) return true;

        var media = note.Media;
        return _inbox.Append(new Inbox.InboxNote(
            Id: note.UpdateId != 0 ? note.UpdateId : note.MessageId,
            ReceivedUtc: DateTime.UtcNow,
            ChatId: note.ChatId,
            Kind: media?.Kind.ToString().ToLowerInvariant() ?? Inbox.InboxNote.TextKind,
            Text: note.Text,
            MediaPath: Beside(media?.LocalPath),
            TranscriptPath: null,
            ReplyToMessageId: note.ReplyToMessageId,
            ReplyToText: note.ReplyToText,
            MessageThreadId: note.MessageThreadId));
    }

    /// <summary>The media path as the note records it: relative to the inbox when it lives there,
    /// which is what makes "media beside transcript" true of a directory somebody has MOVED, and
    /// absolute otherwise rather than silently wrong.</summary>
    private string? Beside(string? localPath)
    {
        if (localPath is not { Length: > 0 } path || _inbox is null) return localPath;
        var root = _inbox.Dir;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 .Replace(Path.DirectorySeparatorChar, '/')
            : path;
    }
}
