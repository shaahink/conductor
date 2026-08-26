using System.Globalization;
using System.Text;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Inbox;

/// <summary>What became of one press of the promote button.</summary>
public enum PromotionResult
{
    /// <summary>A new followups.md row exists that did not exist before.</summary>
    Promoted = 0,

    /// <summary>This note already has a row. The button was pressed twice — which it will be, because
    /// a Telegram keyboard stays on the message forever and nothing about it says "already used".</summary>
    AlreadyPromoted = 1,

    /// <summary>The id on the button is not in that inbox. A note pruned after its acknowledgement,
    /// or a keyboard pressed against a project that has since been re-routed.</summary>
    NoteNotFound = 2,

    /// <summary>Nothing to promote INTO — no project resolved for this chat, or a surface with no
    /// inbox behind it at all.</summary>
    NoProject = 3,
}

/// <param name="RowId">The followups id, when there is one.</param>
/// <param name="Message">What the sender is told, HTML-escaped and ready to send.</param>
public sealed record PromotionOutcome(PromotionResult Result, string? RowId, string Message);

/// <summary>DV4.4 / findings §1.7 — the middle tier, and the ONLY way into it.
///
/// <para>The tier table has three rows and the distance between them is the safety property: a note
/// is a record, a followup is work, an injection steers the session running right now. Promotion
/// moves a note ONE rung, deliberately, by a button the owner pressed — and it stops there. Nothing
/// in this file, and nothing that calls it, can reach the third rung: §1.8's compound failure is a
/// misheard word plus an autonomous agent, and the mishearing is not the part we can fix.</para>
///
/// <para>Shared by both inbound paths on purpose. The in-run surface and the courier daemon file the
/// same <see cref="InboxNote"/> into the same <see cref="InboxStore"/>, so promoting from one and
/// promoting from the other must produce the same row — including the same idempotence key, so a
/// note filed by the courier and promoted by a live run is not promoted twice.</para></summary>
public static class NotePromoter
{
    /// <summary>The callback prefix. Parsed BEFORE the generic <c>action:intent:confirmed</c> split
    /// in <c>CommandRouter.RouteCallback</c>, which would otherwise read the note id as an intent.</summary>
    public const string CallbackPrefix = "promote:";

    /// <summary>Telegram's hard limit on <c>callback_data</c>. Not advisory: the Bot API rejects the
    /// whole sendMessage when a button exceeds it, so the ACK would fail — the note would be filed
    /// and the sender told nothing, which is the one failure this strand exists to remove.</summary>
    public const int CallbackLimit = 64;

    /// <summary>What the button says.</summary>
    public const string ButtonText = "📌 Promote to followup";

    /// <summary>The id series promoted rows are allocated from.</summary>
    public const string IdPrefix = "NOTE";

    /// <summary>The button to hang on an acknowledgement.</summary>
    /// <param name="slug">The project the note was filed against, so the press can find it again on a
    /// machine that serves several. Dropped when it would not fit — see <see cref="Callback"/>.</param>
    public static MessageButton Button(string? slug, long noteId) =>
        new(ButtonText, Callback(slug, noteId));

    /// <summary>The callback payload: <c>promote:slug:id</c>, or <c>promote:id</c> when the slug would
    /// push it past <see cref="CallbackLimit"/>.
    ///
    /// <para>The fallback is not a silent truncation — a truncated slug resolves to the WRONG project
    /// or to none, and both are worse than falling back to the chat's own route, which is how the note
    /// got where it is in the first place.</para></summary>
    public static string Callback(string? slug, long noteId)
    {
        var id = noteId.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(slug)) return CallbackPrefix + id;

        var full = CallbackPrefix + slug + ":" + id;
        return Encoding.UTF8.GetByteCount(full) <= CallbackLimit ? full : CallbackPrefix + id;
    }

    /// <summary>Reads a promote payload back. False for anything that is not one — including
    /// <c>promote:</c> with a non-numeric id, which is a payload this side never wrote.</summary>
    public static bool TryParse(string? data, out string? slug, out long noteId)
    {
        slug = null;
        noteId = 0;
        if (data is not { Length: > 0 } || !data.StartsWith(CallbackPrefix, StringComparison.Ordinal))
            return false;

        var body = data[CallbackPrefix.Length..];
        var cut = body.LastIndexOf(':');
        if (cut >= 0)
        {
            slug = body[..cut];
            body = body[(cut + 1)..];
            if (slug.Length == 0) slug = null;
        }

        return long.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out noteId);
    }

    /// <summary>Turns one filed note into one followups row.</summary>
    /// <param name="store">The inbox holding the note. Its state directory is where the row lands, so
    /// a note filed against project A can never open a lane in project B.</param>
    /// <param name="noteId">The id from the button.</param>
    /// <param name="stageId">The stage that should open the lane, or null when the caller has no run
    /// — the courier's case, which writes <see cref="FollowupWriter.UnclaimedStage"/> instead.</param>
    public static PromotionOutcome Promote(InboxStore? store, long noteId, string? stageId)
    {
        if (store is null)
            return new PromotionOutcome(PromotionResult.NoProject, null,
                "Nothing to promote into: this chat has no project. Choose one with <code>/project</code>.");

        var id = noteId.ToString(CultureInfo.InvariantCulture);
        var note = store.Find(noteId);
        if (note is null)
            return new PromotionOutcome(PromotionResult.NoteNotFound, null,
                $"Note #{id} is not in that inbox any more — nothing was promoted.");

        var path = Path.Combine(store.StateDir, "followups.md");
        var stage = string.IsNullOrWhiteSpace(stageId) ? FollowupWriter.UnclaimedStage : stageId;

        var row = FollowupWriter.Append(path, IdPrefix, ItemOf(note), DetailOf(note), stage, SourceKey(noteId));

        return row.Written
            ? new PromotionOutcome(PromotionResult.Promoted, row.Id,
                $"📋 Promoted to <code>{row.Id}</code> in <code>followups.md</code>, owned by "
                + $"<code>{MessageComposer.EscapeHtml(stage)}</code>. It opens a fix lane when that stage is confirmed.")
            : new PromotionOutcome(PromotionResult.AlreadyPromoted, row.Id,
                $"Already promoted — this note is <code>{row.Id}</code> in <code>followups.md</code>. Nothing was written twice.");
    }

    /// <summary>The literal that makes a promoted row findable again. Per note id, which is per
    /// inbox: the followups file it is written into belongs to the same project as the inbox.</summary>
    public static string SourceKey(long noteId) =>
        "inbox note #" + noteId.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the work IS, in one cell. The note's own words where it has any — a transcript,
    /// a caption, a typed sentence — and the kind where it has none, because "photo" is at least true
    /// and an empty item cell is a row nobody can act on.</summary>
    private static string ItemOf(InboxNote note) =>
        string.IsNullOrWhiteSpace(note.Text)
            ? "Promoted " + note.Kind + " note (no words — see the file)"
            : note.Text;

    /// <summary>Where it came from, so the lane's agent can read the whole note rather than the cell.
    /// The media path is relative to the inbox, which is what the store stores.</summary>
    private static string DetailOf(InboxNote note)
    {
        var detail = "promoted from the chat: " + note.Kind + " received "
                   + note.ReceivedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";

        if (note.MediaPath is { Length: > 0 } media) detail += "; media " + media;
        if (note.TranscriptPath is { Length: > 0 } transcript) detail += "; transcript " + transcript;
        return detail;
    }
}
