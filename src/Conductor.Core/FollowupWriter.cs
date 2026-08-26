using System.Globalization;
using System.Text;

namespace Conductor.Core;

/// <summary>What one call to <see cref="FollowupWriter.Append"/> did.</summary>
/// <param name="Id">The row's <c>FU-</c> id — the one just written, or the one already there.</param>
/// <param name="Written">Whether a new row was added. False means the source had already been
/// promoted and nothing was touched.</param>
public sealed record FollowupRow(string Id, bool Written);

/// <summary>DV4.4 — the only writer that ADDS a followups.md row from outside a verdict.
///
/// <para>Two writers existed before this one and neither could be reused: <c>ParseAuditFollowups</c>
/// rewrites the whole file from a handover, and <c>WriteVerifierFollowups</c> appends a block for one
/// stage's verifier. Both assume they are the only thing touching the file during a phase boundary.
/// A promotion arrives from a chat, at any moment, possibly with no run alive at all — so it needs an
/// append that is safe against a file it did not write and idempotent against a button that can be
/// pressed twice.</para>
///
/// <para>The idempotence key is carried IN the detail cell rather than in a sidecar: the file is the
/// record, a sidecar would drift from it the first time somebody edits a row by hand, and "has this
/// note already been promoted" has to be answerable from the file alone — the courier that answers it
/// may be on a machine where no run has ever opened.</para></summary>
public static class FollowupWriter
{
    /// <summary>Where promoted rows collect. Its own section so a promotion never lands inside a
    /// stage's audit ledger, whose headings other tests pin.</summary>
    public const string SectionHeading = "## Promoted from the inbox";

    /// <summary>The owning-stage token for a row that has no stage yet — written by the courier,
    /// which has no run and therefore no current stage. <see cref="FollowupParser.ReadOpenForStage"/>
    /// matches it for ANY stage, and <c>LaneCoordinator</c> rewrites it to the stage that claims it,
    /// so it opens exactly one lane rather than one per stage boundary forever.</summary>
    public const string UnclaimedStage = "next";

    private const string HeaderRow = "| id | item | detail | owning stage | status |";
    private const string RuleRow = "|----|------|--------|--------------|--------|";

    /// <summary>The longest an item cell may be. A transcript is a paragraph and a table cell is a
    /// line; the whole note is already on disk and the row's detail names where.</summary>
    private const int ItemLimit = 140;

    /// <summary>Appends one row, or reports the row that is already there.</summary>
    /// <param name="filePath">The project's <c>.conductor/followups.md</c>. Created if absent.</param>
    /// <param name="idPrefix">The middle segment of the allocated id — <c>FU-{prefix}-{n}</c>.</param>
    /// <param name="item">What the work is. Sanitised: a pipe in a table cell is a column break, and
    /// a transcript containing one would silently shift every cell after it.</param>
    /// <param name="detail">Where it came from. The <paramref name="sourceKey"/> is appended to it.</param>
    /// <param name="owningStage">The stage that will open a lane for it, or
    /// <see cref="UnclaimedStage"/>.</param>
    /// <param name="sourceKey">The literal that makes this row findable again — the whole of the
    /// idempotence guarantee.</param>
    public static FollowupRow Append(string filePath, string idPrefix, string item, string detail,
        string owningStage, string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(idPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var lines = File.Exists(filePath)
            ? File.ReadAllLines(filePath, Encoding.UTF8).ToList()
            : [];

        if (FindBySource(lines, sourceKey) is { } existing) return new FollowupRow(existing, false);

        var id = NextId(lines, idPrefix);
        var row = "| " + id
                + " | " + Cell(item, ItemLimit)
                + " | " + Cell(detail + " [" + sourceKey + "]", 400)
                + " | " + Cell(owningStage, 60)
                + " | OPEN |";

        Insert(lines, row);
        File.WriteAllText(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);
        return new FollowupRow(id, true);
    }

    /// <summary>The id of the row already carrying this source key, or null.</summary>
    public static string? FindBySource(string filePath, string sourceKey) =>
        File.Exists(filePath)
            ? FindBySource(File.ReadAllLines(filePath, Encoding.UTF8).ToList(), sourceKey)
            : null;

    private static string? FindBySource(List<string> lines, string sourceKey)
    {
        foreach (var line in lines)
        {
            if (!line.Contains("[" + sourceKey + "]", StringComparison.Ordinal)) continue;

            var cells = line.Trim().TrimStart('|').TrimEnd('|').Split('|');
            foreach (var cell in cells)
            {
                var value = cell.Trim();
                if (value.StartsWith("FU-", StringComparison.Ordinal)) return value;
            }
        }

        return null;
    }

    /// <summary>The next free number in the <c>FU-{prefix}-</c> series. Scoped to the series rather
    /// than to the file: the other writers own their own prefixes and allocating across all of them
    /// would make two writers racing for the same number instead of none.</summary>
    private static string NextId(List<string> lines, string idPrefix)
    {
        var stem = "FU-" + idPrefix + "-";
        var max = 0;

        foreach (var line in lines)
        {
            var at = line.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var digits = line[(at + stem.Length)..];
            var end = 0;
            while (end < digits.Length && char.IsAsciiDigit(digits[end])) end++;
            if (end == 0) continue;

            if (int.TryParse(digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        }

        return stem + (max + 1).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Puts the row at the end of the promoted section, creating the section when this is
    /// the first promotion this project has ever had.</summary>
    private static void Insert(List<string> lines, string row)
    {
        var heading = lines.FindIndex(l =>
            l.Trim().Equals(SectionHeading, StringComparison.OrdinalIgnoreCase));

        if (heading < 0)
        {
            // ABOVE the first existing section, not at the end of the file — and that placement is
            // load-bearing rather than cosmetic. VerdictEngine's audit writer appends its rows at
            // EOF under whatever header happens to be last, and its rows have four columns where
            // these have five: a promoted section left trailing would silently reinterpret every
            // audit row it caught, reading the stage cell as the detail and OPEN as the stage.
            var at = lines.FindIndex(l => l.TrimStart().StartsWith("##", StringComparison.Ordinal));
            if (at < 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                at = lines.Count;
            }

            lines.InsertRange(at, [SectionHeading, "", HeaderRow, RuleRow, row, ""]);
            return;
        }

        // The last table line of THIS section — stop at the next heading so a promotion never lands
        // in the section below it.
        var last = -1;
        for (var i = heading + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("##", StringComparison.Ordinal)) break;
            if (lines[i].TrimStart().StartsWith('|')) last = i;
        }

        if (last < 0)
        {
            lines.Insert(heading + 1, row);
            lines.Insert(heading + 1, RuleRow);
            lines.Insert(heading + 1, HeaderRow);
            lines.Insert(heading + 1, "");
            return;
        }

        lines.Insert(last + 1, row);
    }

    /// <summary>One table cell, from text that was never meant to be one.
    ///
    /// <para>A pipe is a column break and a newline ends the row: an unsanitised transcript with
    /// either in it does not fail loudly, it silently produces a row whose status cell holds a
    /// fragment of what somebody said.</para></summary>
    internal static string Cell(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return "-";

        var flat = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t') { flat.Append(' '); continue; }
            flat.Append(ch == '|' ? '/' : ch);
        }

        var value = string.Join(' ',
            flat.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

        if (value.Length == 0) return "-";
        return value.Length <= limit ? value : value[..(limit - 1)] + "…";
    }
}
