using System.Text;

namespace Conductor.Core;

/// <summary>A single tracked followup entry parsed from <c>.conductor/followups.md</c>.</summary>
public sealed class FollowupEntry
{
    public string Id { get; init; } = "";
    public string Item { get; init; } = "";
    public string? Detail { get; init; }
    public string OwningStage { get; init; } = "";
    public string Status { get; init; } = "OPEN";
}

/// <summary>
/// Parses and manages entries in <c>.conductor/followups.md</c> (B12.4).
/// The file uses pipe-table sections (variable column schemes) with <c>FU-*-*</c> ids.
/// </summary>
public static class FollowupParser
{
    /// <summary>
    /// Read all followup entries from a followups.md file. Returns entries in file order.
    /// Rows without a valid FU- id or without a status column are skipped.
    /// </summary>
    public static List<FollowupEntry> Read(string filePath)
    {
        var entries = new List<FollowupEntry>();
        if (!File.Exists(filePath)) return entries;

        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        int? idIdx = null, itemIdx = null, detailIdx = null, stageIdx = null, statusIdx = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith('|') || !line.EndsWith('|')) continue;

            var cells = SplitPipeRow(line);

            // Detect header rows: first cell (after trimming) is "id" (case-insensitive)
            if (cellEq(cells, 0, "id"))
            {
                (idIdx, itemIdx, detailIdx, stageIdx, statusIdx) = MapHeader(cells);
                continue;
            }

            // Data row — require at least an id column
            if (idIdx is not { } i || i >= cells.Length) continue;
            var id = cells[i];
            if (!id.StartsWith("FU-", StringComparison.Ordinal)) continue;

            var item = itemIdx is { } ii && ii < cells.Length ? cells[ii]
                : string.Join(" ", cells.Skip(1)); // fallback: rest of first non-id
            var detail = detailIdx is { } di && di < cells.Length ? NullIfEmpty(cells[di]) : null;
            var stage = stageIdx is { } si && si < cells.Length ? cells[si] : "";
            var status = statusIdx is { } st && st < cells.Length ? cells[st] : "OPEN";

            entries.Add(new FollowupEntry
            {
                Id = id,
                Item = item.Length > 0 ? item : id,
                Detail = detail,
                OwningStage = stage,
                Status = status,
            });
        }

        return entries;
    }

    /// <summary>
    /// Read only OPEN entries whose <see cref="FollowupEntry.OwningStage"/> matches the given
    /// stage id. Matching is case-insensitive substring — "B12 fix-lane" matches "B12".
    /// </summary>
    public static List<FollowupEntry> ReadOpenForStage(string filePath, string stageId)
    {
        return Read(filePath).Where(e =>
            e.Status.Equals("OPEN", StringComparison.OrdinalIgnoreCase) &&
            e.OwningStage.Contains(stageId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Update the status of a followup entry in the file by finding its row and replacing the
    /// status cell. Returns true if the row was found and updated.
    /// </summary>
    public static bool UpdateStatus(string filePath, string id, string newStatus, string? commitRef)
    {
        if (!File.Exists(filePath)) return false;

        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        var updated = false;
        var statusSuffix = commitRef != null ? $" ({commitRef})" : "";

        for (var j = 0; j < lines.Length; j++)
        {
            var line = lines[j];
            if (!line.Contains($"| {id} ", StringComparison.Ordinal) &&
                !line.Contains($"|{id}|", StringComparison.Ordinal)) continue;

            var cells = SplitPipeRow(line.Trim());
            var idIdx = FindIdIndex(line.Trim());
            if (idIdx < 0 || idIdx >= cells.Length || !cells[idIdx].Equals(id, StringComparison.Ordinal))
                continue;

            // Find the last cell before the trailing pipe, which is typically the status
            // For the standard 5-column format: | id | item | detail | stage | status |
            // We find the header mapping from a nearby header line
            var statusIdx = FindStatusIndexNear(lines, j);
            if (statusIdx < 0)
            {
                // Fallback: replace last non-empty cell
                statusIdx = cells.Length - 1;
                while (statusIdx >= 0 && string.IsNullOrWhiteSpace(cells[statusIdx]))
                    statusIdx--;
                if (statusIdx < 0) continue;
            }

            if (statusIdx >= cells.Length) continue;
            cells[statusIdx] = " " + newStatus + statusSuffix + " ";
            lines[j] = "|" + string.Join("|", cells) + "|";
            updated = true;
            break;
        }

        if (updated)
            File.WriteAllText(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);

        return updated;
    }

    // ---------------------------------------------------------------- internals

    private static string[] SplitPipeRow(string line)
    {
        // "| a | b | c |" → span [" a ", " b ", " c "]
        var trimmed = line.TrimStart('|').TrimEnd('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static bool cellEq(string[] cells, int idx, string expected)
    {
        return idx < cells.Length &&
               cells[idx].Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static (int? id, int? item, int? detail, int? stage, int? status) MapHeader(string[] cells)
    {
        int? id = null, item = null, detail = null, stage = null, status = null;
        for (var i = 0; i < cells.Length; i++)
        {
            var c = cells[i];
            if (c is "id") id = i;
            else if (c is "rule" or "item") item = i;
            else if (c is "detail" or "sites" or "why deferred" or "location") detail = i;
            else if (c is "owning stage" or "stage") stage = i;
            else if (c is "status") status = i;
        }
        // If no explicit item column, use the second column (often "item" or "rule")
        if (item == null && cells.Length > 1)
            item = 1;
        return (id, item, detail, stage, status);
    }

    private static int FindIdIndex(string line)
    {
        var cells = SplitPipeRow(line);
        for (var i = 0; i < cells.Length; i++)
            if (cells[i].StartsWith("FU-", StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static int FindStatusIndexNear(string[] allLines, int row)
    {
        // Search backward for a header line to get the status column mapping
        for (var j = row - 1; j >= 0 && j >= row - 10; j--)
        {
            var line = allLines[j].Trim();
            if (!line.StartsWith('|') || !line.EndsWith('|')) continue;
            var cells = SplitPipeRow(line);
            if (!cellEq(cells, 0, "id")) continue;
            var (_, _, _, _, status) = MapHeader(cells);
            if (status is { } s) return s;
        }
        return -1;
    }

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
