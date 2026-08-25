using System.Text.Json.Serialization;

namespace Conductor.Core.Inbox;

/// <summary>DV3.2 — one note in a project's inbox, as it is written to disk and read back by a
/// session that may start three weeks later.
///
/// <para>This is the record half of findings §1.7's tier table: not a steering instruction, not a
/// followup row, just what the owner said about this project. It outlives the run that received it
/// — which is the whole point, since feedback arrives when the owner thinks of it and not when a
/// session boundary happens to be open.</para>
///
/// <para>The shape is deliberately flat and deliberately JSON: a note has to be readable by a
/// person with a text editor when something has gone wrong, and by an engine two versions newer
/// than the one that wrote it.</para></summary>
/// <param name="Id">The channel's own update id. This is the DEDUP KEY (findings §6.2): a courier
/// restart replays every update Telegram still holds, and without this the same voice note files
/// twice, every time.</param>
/// <param name="ReceivedUtc">When this machine took delivery — not when it was spoken, which the
/// channel does not reliably say.</param>
/// <param name="Kind">voice, audio, document, photo, or text.</param>
/// <param name="Text">What was said or captioned, verbatim. Empty for a file with no caption and no
/// transcript yet.</param>
/// <param name="MediaPath">The media file beside this note, relative to the inbox directory, or
/// null. Kept even when a transcript exists, so a garbled transcription is always recoverable.</param>
/// <param name="TranscriptPath">DV3.3 — the transcript sidecar beside the audio, relative to the
/// inbox directory, or null when this note was never transcribed. Null with a
/// <paramref name="MediaPath"/> set is the UNTRANSCRIBED state findings §1.6 asks for: the audio is
/// kept, the words are not there, and the sender was told so.</param>
/// <param name="TranscriptConfidence">DV3.3 — the transcriber's mean confidence, 0..1, or null when
/// the command reported none. Stored so a reader can weigh a whole note at a glance; the doubtful
/// stretches inside <paramref name="Text"/> are marked individually.</param>
/// <param name="ReplyToMessageId">The push this note answers — DV3.4 routes on it.</param>
/// <param name="ReplyToText">What that push said, because its identity stamp is what names the
/// run.</param>
public sealed record InboxNote(
    long Id,
    DateTime ReceivedUtc,
    string ChatId,
    string Kind,
    string Text,
    string? MediaPath = null,
    string? TranscriptPath = null,
    long? ReplyToMessageId = null,
    string? ReplyToText = null,
    long? MessageThreadId = null,
    double? TranscriptConfidence = null)
{
    /// <summary>The kind string for a note that is words only.</summary>
    public const string TextKind = "text";

    /// <summary>Audio that has words attached to it. What DV3.3 produced, as opposed to what it
    /// filed when no command was configured.</summary>
    [JsonIgnore]
    public bool Transcribed => TranscriptPath is { Length: > 0 };

    /// <summary>Audio with no transcript: the file is on disk and nobody has read it out.</summary>
    [JsonIgnore]
    public bool Untranscribed => MediaPath is { Length: > 0 } && !Transcribed
        && (string.Equals(Kind, "voice", StringComparison.Ordinal)
            || string.Equals(Kind, "audio", StringComparison.Ordinal));

    /// <summary>A one-line summary for the index and for <c>conductor inbox list</c>. Newlines are
    /// flattened: the index is one JSON object per LINE, and a note with a paragraph in it must not
    /// be able to become two index entries.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var flat = Text.Replace("\r", " ", StringComparison.Ordinal)
                           .Replace("\n", " ", StringComparison.Ordinal).Trim();
            if (flat.Length == 0) flat = MediaPath is { Length: > 0 } m ? "(" + Kind + ": " + m + ")" : "(empty)";
            return flat.Length <= 120 ? flat : flat[..119] + "…";
        }
    }
}

/// <summary>DV3.2 / findings §6.6 — the read cursor. §1.7 said where a note LANDS and never said
/// when it stops being surfaced; without this the battery grows without bound for any long-lived
/// project, and a session three months in reads every note the owner ever left.
///
/// <para>A cursor, not a delete. Nothing is removed from the inbox by reading it — the only
/// deletion path is an explicit prune — so a mark that turns out to be wrong costs a re-read, not a
/// lost note.</para></summary>
/// <param name="SeenThroughId">Every note with an id at or below this has been carried into a
/// session prompt.</param>
/// <param name="SessionNumber">Which session took delivery. Recorded so "why did session 12 not see
/// that note" is answerable.</param>
public sealed record InboxCursor(long SeenThroughId, int SessionNumber, DateTime MarkedUtc)
{
    /// <summary>A project nobody has read yet: every note is unseen.</summary>
    public static InboxCursor Fresh => new(0, 0, DateTime.MinValue);
}
