using Conductor.Core.Inbox;

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
/// <para>DV3.1 acknowledged it, DV3.2 gave it a durable home, DV3.3 reads the audio out, and DV3.4
/// decides WHICH project's home it belongs in — see <c>RemoteSurface.Routing.cs</c>. The one
/// invariant across all four: a note that arrives is never silently dropped. It is filed, or parked,
/// or refused by name, and the sender is told which.</para></summary>
public sealed partial class RemoteSurface
{
    /// <summary>What became of one note: where it was routed, whether it landed, and — when it could
    /// not — where it was parked instead.</summary>
    private sealed record FiledNote(bool Filed, bool Duplicate, NoteRoute Route, string? Parked);

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
        var outcome = FileNote(note);

        var ack = InboundAck.For(note);
        if (ack.Length == 0) return;
        if (outcome.Duplicate) return;   // a duplicate delivery (findings §6.2) - already answered once

        // DV3.4: a note that could not be filed is still a note. It is parked where nothing deletes
        // it, and the sender is told what was wrong with the destination rather than nothing at all.
        if (!outcome.Filed)
        {
            await ReplyAsync(note.ChatId,
                ack + "\n" + InboundAck.Parked(outcome.Route.Refusal, outcome.Parked), null, ct)
                .ConfigureAwait(false);
            return;
        }

        // DV3.4: which project took it, and which rung of the ladder decided. The owner's only chance
        // to catch a wrong route is being told what it was.
        if (_notes is not null) ack += "\n" + InboundAck.FiledAgainst(outcome.Route.Describe());

        // DV3.3: the transcription verdict that is known INSTANTLY - there is no command - rides the
        // receipt. Only the case that has to wait for a GPU is answered in a second message.
        var willTranscribe = Transcribable(note) && (_transcriber?.Configured ?? false);
        if (Transcribable(note))
            ack += "\n" + (willTranscribe ? InboundAck.Transcribing() : InboundAck.NotTranscribed());

        // DV4.4 / findings §1.7: the acknowledgement is where promotion lives, and the ONLY place it
        // lives. "The bot's acknowledgement should carry the buttons that promote a note to the other
        // two tiers, so promotion is one tap and never an accident" — one tap, and one tier: the
        // inject rung is not offered here and no code path from a note reaches it.
        await ReplyAsync(note.ChatId, ack, [NotePromoter.Button(outcome.Route.Project?.Slug, NoteId(note))], ct)
            .ConfigureAwait(false);

        if (willTranscribe) await TranscribeAsync(note, outcome, ct).ConfigureAwait(false);
    }

    /// <summary>DV4.4 — one press of the promote button, on the path where a run is live.
    ///
    /// <para>The stage handed to the promoter is this run's CURRENT stage, so the row opens its fix
    /// lane at the next confirmation of the stage the owner was watching when they pressed it. The
    /// courier's press has no run and writes <c>next</c> instead; both end in the same row.</para></summary>
    private async Task PromoteAsync(string chatId, string data, CancellationToken ct)
    {
        if (!NotePromoter.TryParse(data, out var slug, out var noteId)) return;

        if (slug is { Length: > 0 } wanted && _notes is not null)
        {
            var match = _notes.Projects.Resolve(wanted);
            if (match.Project is not { } project)
            {
                await ReplyAsync(chatId,
                    "Cannot promote: <code>" + MessageComposer.EscapeHtml(wanted)
                    + "</code> is not a project this machine serves any more.", null, ct).ConfigureAwait(false);
                return;
            }

            await ReplyAsync(chatId,
                NotePromoter.Promote(project.Inbox(), noteId, _state.CurrentStage).Message, null, ct)
                .ConfigureAwait(false);
            return;
        }

        await ReplyAsync(chatId, NotePromoter.Promote(_inbox, noteId, _state.CurrentStage).Message, null, ct)
            .ConfigureAwait(false);
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
    /// <para>Nothing here can throw: <see cref="ITranscriber"/> promises it, and the reply is sent
    /// for all four outcomes. A transcript failure costs the transcript.</para></summary>
    private async Task TranscribeAsync(InboundNote note, FiledNote outcome, CancellationToken ct)
    {
        if (_transcriber is null) return;

        // The audio may have MOVED: a note routed to another project takes its media with it
        // (DV3.4), so the path to read is the one the store now holds, not the one it arrived at.
        var store = StoreFor(outcome.Route);
        var stored = store?.All().FirstOrDefault(n => n.Id == NoteId(note));
        var audio = stored?.MediaPath is { Length: > 0 } rel && store is not null
            ? Path.Combine(store.Dir, rel.Replace('/', Path.DirectorySeparatorChar))
            : note.Media?.LocalPath;

        if (audio is not { Length: > 0 }) return;

        var result = await _transcriber.TranscribeAsync(audio, ct).ConfigureAwait(false);

        if (!result.HasWords || result.Transcript is not { } transcript)
        {
            await ReplyAsync(note.ChatId, InboundAck.TranscriptFailed(result.Detail), null, ct)
                .ConfigureAwait(false);
            return;
        }

        var floor = _transcriber.ConfidenceFloor;
        var attached = store?.AttachTranscript(NoteId(note), transcript, floor);

        // What the sender is shown is what the STORE holds where there is a store: the same marked
        // text, so "that is not what I said" is a conversation about one string and not two.
        var marked = attached?.Text ?? transcript.Marked(floor);
        await ReplyAsync(note.ChatId,
            InboundAck.Transcribed(marked, transcript.ConfidenceLine(floor)), null, ct).ConfigureAwait(false);
    }

    /// <summary>The id a note is filed under: the delivery's own id where the channel gave us one,
    /// the message id otherwise. One place, because filing it under one id and transcribing it under
    /// another would attach a transcript to nothing.</summary>
    private static long NoteId(InboundNote note) =>
        note.UpdateId != 0 ? note.UpdateId : note.MessageId;

    /// <summary>Writes the note to the project's inbox — the project DV3.4's ladder chose, which is
    /// this run's own inbox when nothing said otherwise.
    ///
    /// <para>Three outcomes and each is answered differently upstream: filed, a duplicate delivery
    /// (silent, because it was answered the first time), or nowhere to file — which parks it rather
    /// than dropping it.</para></summary>
    private FiledNote FileNote(InboundNote note)
    {
        var route = RouteOf(note);
        var store = StoreFor(route);

        if (store is null)
        {
            var parked = _parked?.Park(Record(note, note.Media?.LocalPath),
                route.Refusal ?? "no project could be resolved for this chat", note.Media?.LocalPath);
            return new FiledNote(false, false, route, parked);
        }

        // The media travels with the note. A file downloaded into THIS run's inbox and then routed
        // elsewhere would otherwise leave a transcript pointing at another project's directory.
        var media = store.AdoptMedia(note.Media?.LocalPath);
        var filed = store.Append(Record(note, media));
        return new FiledNote(filed, !filed, route, null);
    }

    /// <summary>The note as the store holds it. <paramref name="mediaPath"/> is already relative to
    /// the store that will hold it, or absolute when it lives outside one.</summary>
    private static InboxNote Record(InboundNote note, string? mediaPath) => new(
        Id: NoteId(note),
        ReceivedUtc: DateTime.UtcNow,
        ChatId: note.ChatId,
        Kind: note.Media?.Kind.ToString().ToLowerInvariant() ?? InboxNote.TextKind,
        Text: note.Text,
        MediaPath: mediaPath,
        TranscriptPath: null,
        ReplyToMessageId: note.ReplyToMessageId,
        ReplyToText: note.ReplyToText,
        MessageThreadId: note.MessageThreadId);
}
