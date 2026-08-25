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

        var ack = InboundAck.For(note);
        if (ack.Length == 0) return;

        await ReplyAsync(note.ChatId, ack, null, ct).ConfigureAwait(false);
    }
}
