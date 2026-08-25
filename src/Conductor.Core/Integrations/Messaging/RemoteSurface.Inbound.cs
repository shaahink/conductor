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

        // DV3.3: the transcription verdict that is known INSTANTLY - there is no command - rides the
        // receipt. Only the case that has to wait for a GPU is answered in a second message.
        var willTranscribe = Transcribable(note) && (_transcriber?.Configured ?? false);
        if (Transcribable(note))
            ack += "\n" + (willTranscribe ? InboundAck.Transcribing() : InboundAck.NotTranscribed());

        await ReplyAsync(note.ChatId, ack, null, ct).ConfigureAwait(false);

        if (willTranscribe) await TranscribeAsync(note, ct).ConfigureAwait(false);
    }

    /// <summary>Audio that a transcript would be ABOUT. A photo has no words in it and a document is
    /// whatever the sender had lying around; voice and audio are the two kinds this era exists for,
    /// and running a speech model over a PDF would be a slow way to produce nothing.</summary>
    private static bool Transcribable(InboundNote note) =>
        note.Media is { Downloaded: true, Kind: InboundMediaKind.Voice or InboundMediaKind.Audio };

    /// <summary>DV3.3 / findings §1.6 — the words, after the note is already safe on disk.
    ///
    /// <para>Order matters and it is the opposite of the obvious one. The note is FILED FIRST,
    /// untranscribed, and the transcript is attached to it afterwards: transcription takes minutes,
    /// it runs an external process, and a machine that dies in the middle of it must lose the
    /// transcript rather than the message. The untranscribed note with its audio beside it is a
    /// supported, documented state — so the failure mode of every path through here is a state the
    /// system already handles.</para>
    ///
    /// <para>Nothing here can throw: <see cref="Inbox.ITranscriber"/> promises it, and the reply is
    /// sent for all four outcomes. A transcript failure costs the transcript.</para></summary>
    private async Task TranscribeAsync(InboundNote note, CancellationToken ct)
    {
        if (_transcriber is null || note.Media?.LocalPath is not { Length: > 0 } audio) return;

        var outcome = await _transcriber.TranscribeAsync(audio, ct).ConfigureAwait(false);

        if (!outcome.HasWords || outcome.Transcript is not { } transcript)
        {
            await ReplyAsync(note.ChatId, InboundAck.TranscriptFailed(outcome.Detail), null, ct)
                .ConfigureAwait(false);
            return;
        }

        var floor = _transcriber.ConfidenceFloor;
        var stored = _inbox?.AttachTranscript(NoteId(note), transcript, floor);

        // What the sender is shown is what the STORE holds where there is a store: the same marked
        // text, so "that is not what I said" is a conversation about one string and not two.
        var marked = stored?.Text ?? transcript.Marked(floor);
        await ReplyAsync(note.ChatId,
            InboundAck.Transcribed(marked, transcript.ConfidenceLine(floor)), null, ct).ConfigureAwait(false);
    }

    /// <summary>The id a note is filed under: the delivery's own id where the channel gave us one,
    /// the message id otherwise. One place, because filing it under one id and transcribing it under
    /// another would attach a transcript to nothing.</summary>
    private static long NoteId(InboundNote note) =>
        note.UpdateId != 0 ? note.UpdateId : note.MessageId;

    /// <summary>Writes the note to the project's inbox. False when it was ALREADY there — the
    /// ordinary outcome of a courier replaying updates after a restart, and the reason the sender is
    /// not acknowledged twice for one voice note.</summary>
    private bool FileNote(InboundNote note)
    {
        if (_inbox is null) return true;

        var media = note.Media;
        return _inbox.Append(new Inbox.InboxNote(
            Id: NoteId(note),
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
