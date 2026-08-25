using System.Globalization;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>DV3.1 — what the bot says back when something other than text arrives.
///
/// <para>Its own class rather than a method on <see cref="MessageComposer"/> for two reasons: the
/// composer is a run-scoped object (it reads the plan, the tracker and the session counter) and an
/// acknowledgement must be sayable when there is no run at all, which is the whole point of the
/// era; and every sentence here is pure text-in-text-out, so a test can pin the exact wording
/// without an HTTP listener.</para>
///
/// <para>Every sentence names the thing it is about. A bot that answers "couldn't do that" is the
/// silent-drop failure (findings §1.2 gap 2) with an apology stapled to it.</para></summary>
public static class InboundAck
{
    /// <summary>The shape every refusal takes, with no idea which messenger imposed it: the file by
    /// name, then why, then the promise that the rest of the message survived. WHAT the limit is and
    /// WHOSE it is belong to the adapter (KS11.1 rule one) — see <c>TelegramLimits</c>, which is
    /// where the 20 MB download ceiling and its sentence live.</summary>
    public static string Refused(string fileName, string why) =>
        $"⚠️ <b>{MessageComposer.EscapeHtml(fileName)}</b> {why} "
        + "Your message was kept; the file was not.";

    /// <summary>What the sender hears when a file DID arrive. Names the kind, because "received"
    /// with no noun is how a photo silently becomes a document in someone's head.</summary>
    public static string Received(InboundMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var what = media.Kind switch
        {
            InboundMediaKind.Voice => "Voice note",
            InboundMediaKind.Audio => "Audio",
            InboundMediaKind.Document => "Document",
            _ => "Photo",
        };
        var duration = media.DurationSeconds > 0
            ? ", " + media.DurationSeconds.ToString(CultureInfo.InvariantCulture) + "s"
            : "";
        return $"📥 {what} received — <b>{MessageComposer.EscapeHtml(media.FileName)}</b>"
             + $" ({Size(media.SizeBytes)}{duration}).";
    }

    /// <summary>The one line the whole acknowledgement comes down to, media and caption together.
    /// Empty when there is nothing to acknowledge, so the caller can stay silent rather than send a
    /// blank message.</summary>
    public static string For(InboundNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Media is not { } media) return "";

        var head = media.Refusal ?? Received(media);
        return note.Text.Length > 0
            ? head + "\n<i>" + MessageComposer.EscapeHtml(Clip(note.Text, 200)) + "</i>"
            : head;
    }

    /// <summary>DV3.3 — what the sender hears the moment the audio lands and BEFORE the words come
    /// back. Transcription is minutes of GPU time for a long note; silence for those minutes is
    /// indistinguishable from a bot that dropped it.</summary>
    public static string Transcribing() =>
        "🎧 Transcribing it locally — the words follow in a moment.";

    /// <summary>DV3.3 / findings §1.6 — audio kept, words not taken. Names the key to set, because
    /// "not transcribed" without the fix is a dead end, and the whole reason this sentence exists is
    /// that a silently-untranscribed voice note is the §1.2 gap-2 failure with a shrug attached.
    ///
    /// <para>The config key is spelled out. A sentence that says "transcription is not configured"
    /// and leaves the reader to find the key has told them nothing they did not know.</para></summary>
    public static string NotTranscribed() =>
        "📝 <b>Not transcribed</b> — no transcribe command is configured "
        + "(<code>courier.transcribe.command</code>, or the "
        + "<code>" + Models.TranscribeConfig.CommandEnvVar + "</code> environment variable). "
        + "The audio is kept in the project's inbox: "
        + "<code>conductor inbox transcribe --all</code> reads it out once one is set.";

    /// <summary>DV3.3 — a command ran and did not deliver. Same promise as the unset case: the audio
    /// survives, and the sentence names what went wrong rather than apologising in general.</summary>
    public static string TranscriptFailed(string? detail) =>
        "📝 <b>Not transcribed</b> — "
        + (detail is { Length: > 0 } d ? MessageComposer.EscapeHtml(d) : "the transcribe command failed")
        + ". The audio is kept in the project's inbox.";

    /// <summary>DV3.3 — the words, with how sure the transcriber was and the doubtful stretches
    /// marked exactly as they are marked in the stored note. The sender is the ONE person who can
    /// correct a misheard word, so they are shown the marks rather than a clean-looking lie.</summary>
    public static string Transcribed(string markedText, string confidenceLine) =>
        "📝 <b>Transcript</b> (" + MessageComposer.EscapeHtml(confidenceLine) + "):\n<i>"
        + MessageComposer.EscapeHtml(Clip(markedText, 900)) + "</i>";

    /// <summary>DV3.4 / findings §1.5 — which project took the note, and which rung of the routing
    /// ladder decided. Said every time, because routing that happens without the owner typing
    /// anything is only safe if it is visible: a reply-to-the-wrong-push is a one-word correction if
    /// they are told, and a note lost in another project's inbox if they are not.</summary>
    public static string FiledAgainst(string where) =>
        "📁 Filed against <b>" + MessageComposer.EscapeHtml(where) + "</b>.";

    /// <summary>DV3.4 / findings §6.10 — the note could not be filed anywhere, so it was PARKED.
    /// Never "sorry, something went wrong": the reason names the destination, and the path says where
    /// the note is sitting so it can be moved by hand when the project comes back.</summary>
    public static string Parked(string? why, string? path) =>
        "📮 <b>Kept, not filed</b> — "
        + MessageComposer.EscapeHtml(why ?? "no project could be resolved for this chat")
        + (path is { Length: > 0 }
            ? "\nIt is parked at <code>" + MessageComposer.EscapeHtml(path) + "</code> and nothing deletes it."
            : "");

    /// <summary>KS11.2 / findings §1.8 — only an admin chat may file. An observer is read-only and
    /// hears exactly that, by name, instead of being ignored.</summary>
    public static string NotYours(ChatProfile profile) =>
        $"This chat is <b>{profile.ToString().ToLowerInvariant()}</b> and may read the run, not file "
        + "against it. A voice note or a file has to come from an admin chat.";

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");

    /// <summary>Same three brackets <see cref="MessageComposer"/> uses for an evidence artifact, so
    /// two sizes in the same chat are never written two ways.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };
}
