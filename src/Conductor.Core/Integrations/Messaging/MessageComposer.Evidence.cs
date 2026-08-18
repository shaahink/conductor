using System.Globalization;
using System.Text;

using Conductor.Core.Evidence;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>What one <c>/evidence</c> answer IS: a body to send, and — when the artifact resolved to
/// a file — the file to send it with.
///
/// <para>A record rather than two out-parameters because the two halves are not independent: a pull
/// that produced an attachment carries the caption as its text, and a pull that could not produce
/// one carries the reason. <paramref name="CostsBudget"/> is what the per-chat rate limit charges
/// for: an upload costs bytes off the engine's machine, a "no such checkpoint" line does not, and
/// charging for the latter would let a typo lock a reader out of the former.</para></summary>
/// <param name="Text">The body, HTML, already escaped.</param>
/// <param name="Attachment">The file to upload, or null when the answer is text.</param>
/// <param name="CostsBudget">Whether this answer should consume one of the chat's pulls.</param>
public sealed record EvidenceAnswer(string Text, OutboundAttachment? Attachment = null, bool CostsBudget = false);

/// <summary>KS11.4 / CHAPAR CH-6 — depth on demand.
///
/// <para>The complaint this exists for is "it only sends part of the evidence". That complaint is
/// four constants: <see cref="EvidenceFilesPerPush"/> (artifact five of a batch is announced, never
/// sent), <c>EvidenceLinesPerPush</c> (the overflow list stops at eight), and the two that bound a
/// session-end push's prose. Raising them trades truncation for noise in every chat on every push,
/// which is why CH-6 answers with a second tier instead: the push carries the headline, and a reader
/// who wants the artifact ASKS for it.</para>
///
/// <para>The argument is a checkpoint id and only ever a checkpoint id. Nothing here takes a path
/// from a reader — the path comes from the tracker row the engine itself wrote — so the surface
/// cannot be walked into a file the run never claimed as evidence.</para></summary>
public sealed partial class MessageComposer
{
    /// <summary>What <c>/evidence</c> answers: every checkpoint that has an artifact, with what the
    /// artifact is and how big it is.
    ///
    /// <para>Deliberately unbounded. <c>EvidenceLinesPerPush</c> exists because a PUSH nobody asked
    /// for must not be forty lines long; a reader who typed <c>/evidence</c> asked for the list, and
    /// the transport already splits a long body into valid chunks rather than dropping it.</para></summary>
    public string EvidenceListText()
    {
        TrackerSnapshot track;
        try { track = _progress.Read(_plan, CancellationToken.None); }
        catch (IOException) { track = new TrackerSnapshot(); }
        catch (InvalidOperationException) { track = new TrackerSnapshot(); }

        var rows = track.Checkpoints.Where(c => EvidencePaths(c.Evidence).Count > 0).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{EscapeHtml(PlanName())} — evidence</b>");
        if (rows.Count == 0)
        {
            sb.AppendLine();
            sb.Append("No checkpoint has recorded an artifact yet. When one does, it is pushed here as it lands "
                + "and stays askable by id.");
            return sb.ToString();
        }

        foreach (var row in rows)
        {
            sb.AppendLine();
            sb.AppendLine($"<b>{EscapeHtml(row.Id)}</b> — {EscapeHtml(Clip(row.Title, EvidenceTitleMaxChars))}");
            foreach (var path in EvidencePaths(row.Evidence))
                sb.AppendLine("  " + ArtifactLine(path));
        }

        sb.AppendLine();
        sb.Append($"Ask <code>/evidence {EscapeHtml(rows[^1].Id)}</code> for the artifact itself.");
        return sb.ToString();
    }

    /// <summary>One artifact as the list shows it: what it is called, and whether the engine can
    /// still reach it. A row whose file has been moved or was never committed is the interesting
    /// case, and a list that hid it would send the reader to ask for something that cannot arrive.</summary>
    private string ArtifactLine(string path)
    {
        var resolved = ResolveArtifact(path);
        if (resolved == null) return $"<code>{EscapeHtml(path)}</code> — not on this machine";

        var bytes = FileBytes(resolved);
        return bytes < 0
            ? $"<code>{EscapeHtml(path)}</code> — unreadable"
            : $"<code>{EscapeHtml(path)}</code> ({Size(bytes)})";
    }

    /// <summary>What <c>/evidence &lt;id&gt;</c> answers, one message per artifact the row names.
    ///
    /// <para>A file that resolves is UPLOADED — CH-6's document upload, or a photo when the artifact
    /// is visual, which the transport decides from the same rule the push path uses. Everything else
    /// is text: an id nobody claimed, a path the engine can no longer reach, an artifact past the
    /// size cap. Each answer says which of those happened, because "nothing arrived" is the failure
    /// this whole checkpoint exists to end.</para></summary>
    public IReadOnlyList<EvidenceAnswer> EvidenceFor(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        id = id.Trim();

        TrackerSnapshot track;
        try { track = _progress.Read(_plan, CancellationToken.None); }
        catch (IOException) { return [Unknown(id)]; }
        catch (InvalidOperationException) { return [Unknown(id)]; }

        var row = track.ById(id);
        if (row == null) return [Unknown(id)];

        var paths = EvidencePaths(row.Evidence);
        if (paths.Count == 0)
            return
            [
                new EvidenceAnswer($"<b>{EscapeHtml(row.Id)}</b> has no artifact recorded yet — it is "
                    + $"{EscapeHtml(row.Status)}. <code>/evidence</code> lists the ones that do."),
            ];

        return paths.Select(p => AnswerFor(row.Id, row.Title, p)).ToList();
    }

    private EvidenceAnswer AnswerFor(string checkpointId, string title, string path)
    {
        var header = $"<b>{EscapeHtml(checkpointId)}</b> — {EscapeHtml(Clip(title, EvidenceTitleMaxChars))}";
        var resolved = ResolveArtifact(path);
        if (resolved == null)
            return new EvidenceAnswer($"{header}\n<code>{EscapeHtml(path)}</code>\n"
                + "<i>not sent — the path the checkpoint claimed does not resolve to a file on this machine</i>");

        var bytes = FileBytes(resolved);
        if (bytes < 0)
            return new EvidenceAnswer($"{header}\n<code>{EscapeHtml(path)}</code>\n"
                + "<i>not sent — the file is not readable from the engine</i>");

        if (bytes > EvidencePullMaxBytes)
            return new EvidenceAnswer($"{header}\n<code>{EscapeHtml(path)}</code> ({Size(bytes)})\n"
                + $"<i>not sent — over the {Size(EvidencePullMaxBytes)} limit for a pulled artifact. "
                + "It is on the engine's machine at the path above.</i>");

        var caption = $"{header}\n<code>{EscapeHtml(path)}</code> ({Size(bytes)})";
        return new EvidenceAnswer(caption,
            new OutboundAttachment(resolved, EvidenceKinds.IsVisual(KindOf(path)), caption),
            CostsBudget: true);
    }

    private static EvidenceAnswer Unknown(string id) =>
        new($"No checkpoint <b>{EscapeHtml(id)}</b> in this run's tracker. "
            + "<code>/evidence</code> lists the ones that have an artifact.");

    /// <summary>The evidence CELL of a tracker row, as the paths it actually names. A claim can carry
    /// more than one artifact (K5.1's result contract says comma-separated), and an unclaimed row
    /// carries the table's own placeholder — <c>-</c> — which is not a path.</summary>
    private static IReadOnlyList<string> EvidencePaths(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return [];
        return cell.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(p => p is not ("-" or "—" or "n/a" or "N/A" or "TBD"))
                   .ToList();
    }

    /// <summary>K5.3's kind vocabulary applied to a path the tracker wrote. The push path is handed a
    /// registered artifact that already knows its kind; a pulled one is a filename, so the extension
    /// is all there is — and the only decision it feeds is photo-or-document.</summary>
    private static string KindOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => EvidenceKinds.Image,
            _ => "text",
        };
    }

    private static long FileBytes(string fullPath)
    {
        try { return new FileInfo(fullPath).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    /// <summary>The seam's own ceiling on a pulled artifact, in the seam rather than in the adapter:
    /// a channel refuses what it cannot carry (Telegram's own document limit is five times this),
    /// but "how much of the engine's disk may a chat message pull over the network" is a policy of
    /// the surface, not of one messenger's wire.</summary>
    public const long EvidencePullMaxBytes = 10L * 1024 * 1024;

    /// <summary>A checkpoint title is a paragraph in this repo's own tracker. The pull is about the
    /// artifact; the title is there to say which checkpoint it belongs to.</summary>
    private const int EvidenceTitleMaxChars = 120;

    /// <summary>How many artifacts one chat may pull per <see cref="EvidencePullWindow"/>. An upload
    /// is bytes off the engine's machine on a run that is doing something else, and a chat that can
    /// ask for one can ask for a hundred.</summary>
    public const int EvidencePullsPerWindow = 8;

    public static readonly TimeSpan EvidencePullWindow = TimeSpan.FromMinutes(10);

    /// <summary>The refusal, which has to say when the reader may ask again — a rate limit that
    /// answers "no" without answering "when" reads as the feature being broken.</summary>
    public static string PullBudgetRefusal(TimeSpan retryAfter)
    {
        var wait = retryAfter < TimeSpan.FromMinutes(1)
            ? $"{Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s"
            : $"{((int)Math.Ceiling(retryAfter.TotalMinutes)).ToString(CultureInfo.InvariantCulture)}m";
        return $"This chat has pulled {EvidencePullsPerWindow.ToString(CultureInfo.InvariantCulture)} artifacts in "
            + $"the last {((int)EvidencePullWindow.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minutes — "
            + $"ask again in {wait}. <code>/evidence</code> still lists them.";
    }
}
