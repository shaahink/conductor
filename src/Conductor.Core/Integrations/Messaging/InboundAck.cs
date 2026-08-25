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
