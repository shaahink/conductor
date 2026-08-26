using System.Globalization;

using Conductor.Core.Inbox;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Courier;

/// <summary>What one poll did. Returned rather than logged so a test can state it, and so
/// <c>courier status</c> has something to print that is not a guess.</summary>
/// <param name="Received">Deliveries the source handed over.</param>
/// <param name="Filed">Notes that landed in a project inbox for the first time.</param>
/// <param name="Duplicates">Deliveries already filed under this update id — the ordinary outcome of
/// a restart, and the number that proves the exactly-once claim rather than asserting it.</param>
/// <param name="Parked">Notes with nowhere to go, kept in the dead-letter box.</param>
public sealed record CourierTick(int Received, int Filed, int Duplicates, int Parked);

/// <summary>DV4.1 / findings §1.4-B — the courier: one bot, always awake, outliving the run.
///
/// <para>The ask this answers is §1.2's: feedback should be possible when you HAVE it, not when a
/// run happens to be up. Inside a run the poll loop dies with the run, so a voice note sent at
/// midnight to a machine with nothing running was never fetched at all. This daemon owns the token
/// instead, polls whether or not anything is running, and files each note into the project it is
/// about.</para>
///
/// <para><b>What it cannot do, stated plainly (§6.3).</b> Telegram holds an undelivered update for
/// 24 hours and no longer. A note sent to a laptop that sleeps all weekend is gone by Monday — not
/// dropped by conductor, never handed over by Telegram. The courier narrows the gap from "no run
/// live" to "machine on"; it cannot do better from this machine, and pretending otherwise is how a
/// person comes to trust it with something they said once.</para>
///
/// <para><b>The exactly-once argument.</b> Three things, in this order: the offset is durable
/// (<see cref="CourierOffset"/>), it is written AFTER the delivery is handled, and a note is filed
/// under its update id so <see cref="InboxStore.Append"/>'s refusal to overwrite is the dedup. Kill
/// the process between receive and acknowledge and the offset still points AT the update in flight,
/// so the restart re-receives it — and the file it would write is already there. One note.</para>
/// </summary>
public sealed class CourierDaemon
{
    private readonly ICourierSource _source;
    private readonly CourierSettings _settings;
    private readonly CourierOffset _offset;
    private readonly NoteRouter _router;
    private readonly DeadLetterBox _parked;
    private readonly Action<string> _log;

    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one. A rig
    /// passes its own, which is what keeps a test off the operator's real inbox.</param>
    public CourierDaemon(ICourierSource source, CourierSettings settings, string? stateHomeRoot = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        _source = source;
        _settings = settings;
        _log = log ?? (_ => { });

        var root = string.IsNullOrWhiteSpace(stateHomeRoot) ? Store.StateHome.Root : stateHomeRoot;
        _offset = new CourierOffset(root);
        _parked = new DeadLetterBox(root);

        // The explicit allowlist, and no local run: a courier has no project of its own, so the
        // bottom rung of DV3.4's ladder ("the run that received it") simply is not there. A note that
        // names nothing routable is parked, which is the honest answer — a machine-level daemon
        // guessing at a default project is how notes end up in the wrong inbox for a week.
        _router = new NoteRouter(
            new ProjectDirectory(root, local: null, only: settings.Allowed()),
            new ChatRoutes(root));
    }

    /// <summary>Where the offset lives, for a status line and for a test that wants to corrupt it.</summary>
    public CourierOffset Offset => _offset;

    /// <summary>Poll until told to stop. Every exception is caught and logged: a courier that exits
    /// on a transport hiccup is a courier that stops answering the phone, which is the one failure
    /// this component may not have.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
        var conflicts = 0;

        _log($"courier polling {_source.Describe}; {_offset.Describe()}; "
           + $"{_settings.Projects.Count.ToString(CultureInfo.InvariantCulture)} project(s) allowed, "
           + $"{_settings.Chats.Count.ToString(CultureInfo.InvariantCulture)} chat(s) listed");

        while (!ct.IsCancellationRequested)
        {
            var wait = interval;
            try
            {
                var tick = await PollOnceAsync(ct).ConfigureAwait(false);
                conflicts = 0;
                if (tick.Received > 0) _log(Describe(tick));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (CourierConflictException ex)
            {
                // §6.9's transition, from the other side. A plan whose messenger block still polls is
                // fighting this daemon for the same token, and the courier says so ONCE rather than
                // every four seconds — the diagnosis has to be readable to be read.
                wait = ConflictBackoff(++conflicts);
                if (conflicts == 1)
                    _log("courier getUpdates conflict: " + ex.Message + " Backing off "
                       + wait.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log("courier poll error: " + ex.Message);
            }

            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Linear, capped, deterministic — five seconds per consecutive conflict up to a minute.
    /// The same numbers the in-run poll loop backs off with, deliberately: two processes fighting
    /// over one token should retreat at the same rate, or the faster one wins by accident.</summary>
    internal static TimeSpan ConflictBackoff(int streak) =>
        TimeSpan.FromSeconds(Math.Min(60.0, 5.0 * Math.Max(1, streak)));

    private static string Describe(CourierTick tick) =>
        $"courier tick: {tick.Received.ToString(CultureInfo.InvariantCulture)} received, "
      + $"{tick.Filed.ToString(CultureInfo.InvariantCulture)} filed, "
      + $"{tick.Duplicates.ToString(CultureInfo.InvariantCulture)} already filed, "
      + $"{tick.Parked.ToString(CultureInfo.InvariantCulture)} parked";

    /// <summary>One poll, one batch, and the offset advanced one delivery at a time.
    ///
    /// <para>Per delivery rather than per batch on purpose: a crash halfway through a batch of five
    /// should replay one update, not five. The write is the LAST thing that happens for a delivery,
    /// so the window in which a kill causes a replay is exactly the window in which the work was not
    /// finished.</para></summary>
    public async Task<CourierTick> PollOnceAsync(CancellationToken ct)
    {
        var batch = await _source.FetchAsync(_offset.Read(), ct).ConfigureAwait(false);
        int filed = 0, duplicates = 0, parked = 0;

        foreach (var delivery in batch)
        {
            switch (await HandleAsync(delivery, ct).ConfigureAwait(false))
            {
                case DeliveryOutcome.Filed: filed++; break;
                case DeliveryOutcome.Duplicate: duplicates++; break;
                case DeliveryOutcome.Parked: parked++; break;
                default: break;
            }

            _offset.Write(delivery.UpdateId + 1);
        }

        return new CourierTick(batch.Count, filed, duplicates, parked);
    }

    private enum DeliveryOutcome { Filed, Duplicate, Parked, Other }

    private async Task<DeliveryOutcome> HandleAsync(CourierDelivery delivery, CancellationToken ct)
    {
        // DV4.4 — a button press. Before the note branch because a press carries no note at all, and
        // its own admin gate: the surface's rule is that a callback is refused for every non-admin
        // profile by name, and a courier serving several chats from one bot must apply it too.
        if (delivery is { Callback: { } press, Profile: { } presser })
        {
            if (presser != ChatProfile.Admin)
            {
                await _source.ReplyAsync(press.ChatId,
                    "That button is not part of the observer surface.", press.ThreadId, ct).ConfigureAwait(false);
                return DeliveryOutcome.Other;
            }

            await PromoteAsync(press, ct).ConfigureAwait(false);
            return DeliveryOutcome.Other;
        }

        // Nothing here for a courier — an unlisted chat, or an update kind it has no use for. The
        // offset still advances past it in PollOnceAsync, which is what stops it being fetched again
        // on every poll for the next 24 hours.
        if (delivery is not { Note: { } note, Profile: { } profile }) return DeliveryOutcome.Other;

        if (!ChatProfiles.MayFile(profile))
        {
            // Said out loud rather than ignored: an observer who sends a voice note into silence
            // cannot tell "not allowed" from "broken", and will send it again.
            await ReplyAsync(note, InboundAck.NotYours(profile), ct).ConfigureAwait(false);
            return DeliveryOutcome.Other;
        }

        if (delivery.Command is { Length: > 0 } command)
        {
            await HandleCommandAsync(note, command, ct).ConfigureAwait(false);
            return DeliveryOutcome.Other;
        }

        var route = _router.Route(note.ChatId, note.MessageThreadId, note.ReplyToText);
        var ack = InboundAck.For(note);
        if (ack.Length == 0) ack = TextNoteAck(note);

        if (route.Project is not { } project)
        {
            var path = _parked.Park(Record(note, note.Media?.LocalPath),
                route.Refusal ?? "no project could be resolved for this chat", note.Media?.LocalPath);
            await ReplyAsync(note, ack + "\n" + InboundAck.Parked(route.Refusal, path), ct)
                .ConfigureAwait(false);
            return DeliveryOutcome.Parked;
        }

        var store = project.Inbox();
        var id = NoteId(note);

        // §6.2's replay, caught BEFORE anything is moved. Measured, not theorised: the first version
        // of this adopted the media first and let Append refuse, which files the note exactly once —
        // and leaves an orphan copy of the audio in the inbox that no note references and no prune
        // can remove, because prune deletes the files a note NAMES. Append's rename is still the
        // dedup; this is what stops the work in front of it running twice.
        if (store.Has(id))
        {
            _log($"courier: update {delivery.UpdateId.ToString(CultureInfo.InvariantCulture)} was "
               + $"already filed against {project.Name} — nothing written, nothing said");
            return DeliveryOutcome.Duplicate;
        }

        // The media travels with the note: it was downloaded into the courier's own staging directory
        // before anything knew which project this was about, and AdoptMedia moves it into the inbox
        // that ended up holding it. A transcript pointing at the courier's scratch space would be a
        // transcript pointing at a file the next prune deletes.
        var media = store.AdoptMedia(note.Media?.LocalPath);
        if (!store.Append(Record(note, media)))
        {
            // The narrow race the check above cannot close: another writer filed this id between the
            // two. The media this delivery adopted is now an orphan — and it STAYS one. Nothing in
            // this engine removes a file from an inbox except prune (DV3.3), and a duplicate copy of
            // an owner's voice note costs a few kilobytes where a second deleter costs the property
            // that makes the inbox safe to hold the only copy of something they said. The Has()
            // check above is what keeps this rare; prune cannot reap what no note names, so the log
            // names the file instead.
            // RemoteSurface.Inbound leaves the same orphan in the same race, for the same reason.
            _log($"courier: update {delivery.UpdateId.ToString(CultureInfo.InvariantCulture)} was "
               + $"filed by somebody else while this one was working — nothing written, nothing said"
               + (media is { Length: > 0 } && !Path.IsPathRooted(media)
                   ? $"; {media} stays in the inbox as an orphan no note names" : ""));
            return DeliveryOutcome.Duplicate;
        }

        await ReplyAsync(note, ack + "\n" + InboundAck.FiledAgainst(route.Describe()), ct,
            [new CourierButton(NotePromoter.ButtonText, NotePromoter.Callback(project.Slug, id))])
            .ConfigureAwait(false);
        return DeliveryOutcome.Filed;
    }

    /// <summary>DV4.4 — one press of the promote button, on the path where NO run is alive.
    ///
    /// <para>The daemon has no plan, no stage and no current run, so the row it writes is owned by
    /// <c>next</c>: the first stage that project confirms claims it. That is the whole reason the
    /// token exists — a note filed at midnight, promoted at midnight, and made into work by whichever
    /// stage happens to be running when the machine is next asked to do something.</para>
    ///
    /// <para>Note the rung it stops at. There is no branch here that writes an injection, and there
    /// is no injection API on anything this method can reach: §1.8's compound failure needs a path
    /// from a transcript to a running agent's prompt, and the courier is the component that would
    /// otherwise have one, because it is awake when nothing is watching.</para></summary>
    private async Task PromoteAsync(CourierCallback press, CancellationToken ct)
    {
        if (!NotePromoter.TryParse(press.Data, out var slug, out var noteId))
        {
            // A payload this side never wrote. Answered rather than ignored: the press already got
            // its answerCallbackQuery, and silence after that reads as a bot that broke.
            await _source.ReplyAsync(press.ChatId,
                "The courier does not know that button.", press.ThreadId, ct).ConfigureAwait(false);
            return;
        }

        var project = slug is { Length: > 0 }
            ? _router.Projects.Resolve(slug).Project
            : _router.Route(press.ChatId, press.ThreadId, null).Project;

        if (project is null)
        {
            await _source.ReplyAsync(press.ChatId,
                "Cannot promote: no project on this machine matches that note. "
                + "This machine has: " + MessageComposer.EscapeHtml(_router.Projects.Listed()),
                press.ThreadId, ct).ConfigureAwait(false);
            return;
        }

        var outcome = NotePromoter.Promote(project.Inbox(), noteId, stageId: null);
        _log($"courier: {outcome.Result} — note {noteId.ToString(CultureInfo.InvariantCulture)} "
           + $"of {project.Name}{(outcome.RowId is { } row ? " is " + row : "")}");

        await _source.ReplyAsync(press.ChatId, outcome.Message, press.ThreadId, ct).ConfigureAwait(false);
    }

    /// <summary>The acknowledgement for a note that is words only. <see cref="InboundAck.For"/>
    /// answers with an empty string for one — inside a run, typed text is a command and never a note
    /// — but to a courier a typed sentence is the same kind of thing as a spoken one, and silence
    /// after it would be the §1.2 gap-2 failure with the audio removed.</summary>
    private static string TextNoteAck(InboundNote note) =>
        "📥 Note received — <i>"
      + MessageComposer.EscapeHtml(note.Text.Length <= 200 ? note.Text : note.Text[..199] + "…")
      + "</i>";

    /// <summary>DV3.4's <c>/project</c>, at machine level. The courier has no local run, so without
    /// this a chat that has never been replied to by a push has no way to choose at all — and the
    /// selection it writes is the same <c>chat-routes.json</c> a live run reads, which is why that
    /// file was put at the state home rather than in a project.</summary>
    private async Task HandleCommandAsync(InboundNote note, string command, CancellationToken ct)
    {
        var cut = command.IndexOf(' ', StringComparison.Ordinal);
        var verb = (cut < 0 ? command : command[..cut]).Trim().ToLowerInvariant();
        var rest = cut < 0 ? "" : command[(cut + 1)..].Trim();

        if (!string.Equals(verb, "project", StringComparison.Ordinal))
        {
            await ReplyAsync(note,
                "The courier files notes; it does not steer runs. Send a voice note, a file or a "
                + "sentence and it is filed against a project. <code>/project</code> chooses which.",
                ct).ConfigureAwait(false);
            return;
        }

        if (rest.Length == 0)
        {
            var current = _router.Routes.Current(note.ChatId, note.MessageThreadId);
            var chosen = current is { Length: > 0 } ? _router.Projects.Resolve(current).Project : null;
            await ReplyAsync(note,
                (chosen is { } p
                    ? $"Notes here are filed against <b>{MessageComposer.EscapeHtml(p.Name)}</b>."
                    : "No project is selected for this chat.")
                + "\nThis courier carries: " + MessageComposer.EscapeHtml(_router.Projects.Listed())
                + "\nSet it with <code>/project &lt;name&gt;</code>.", ct).ConfigureAwait(false);
            return;
        }

        var match = _router.Projects.Resolve(rest);
        if (match.Project is not { } picked)
        {
            await ReplyAsync(note, MessageComposer.EscapeHtml(match.Refusal ?? "No such project."), ct)
                .ConfigureAwait(false);
            return;
        }

        _router.Routes.Set(note.ChatId, note.MessageThreadId, picked.Slug);
        await ReplyAsync(note,
            $"Notes {(note.MessageThreadId is null ? "in this chat" : "in this topic")} now file against "
            + $"<b>{MessageComposer.EscapeHtml(picked.Name)}</b> "
            + $"<i>({MessageComposer.EscapeHtml(picked.RepoLeaf)})</i>. It stays until you change it."
            + (picked.Present ? ""
                : "\n⚠️ That checkout is not on this disk right now; notes will be parked until it is back."),
            ct).ConfigureAwait(false);
    }

    private Task ReplyAsync(InboundNote note, string text, CancellationToken ct,
        IReadOnlyList<CourierButton>? buttons = null) =>
        _source.ReplyAsync(note.ChatId, text, note.MessageThreadId, ct, buttons);

    /// <summary>The note as the store holds it, filed under the DELIVERY's id. <c>RemoteSurface</c>
    /// makes the same record for the same reason: the id is the dedup key, so the two producers must
    /// agree on it or a replay through the other one would file a second copy.</summary>
    /// <summary>The id a note is filed under: the delivery's own id where there is one, the message
    /// id otherwise. <c>RemoteSurface</c> computes it the same way, and they must not diverge — the
    /// two producers agreeing on this key is what makes a note filed by one a duplicate to the
    /// other.</summary>
    private static long NoteId(InboundNote note) => note.UpdateId != 0 ? note.UpdateId : note.MessageId;

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
