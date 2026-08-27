using System.Text;
using System.Text.RegularExpressions;

namespace Conductor.Core;

/// <summary>
/// Bug #81 — <c>.conductor/followups.md</c> read as a LEDGER: one entry per followup id, carrying the
/// newest verdict the file holds for it.
///
/// <para><b>What the file actually is, measured.</b> 95 rows matching an <c>FU-</c> id for 59
/// distinct id cells and fewer real ids than that: every era's closing pass appends a scoreboard
/// table (<c>| row | disposition | why |</c>) that restates rows the first table already declared,
/// composite cells name several ids at once (<c>FU-OWNER-10 · 11 · 13</c>,
/// <c>FU-OWNER-10, FU-OWNER-11, FU-OWNER-13</c>), and a cell can carry a parenthetical
/// (<c>FU-OWNER-14 (the reinstall)</c>). <see cref="FollowupParser.Read"/> is a faithful row reader
/// and hands all of that back as it is: five distinct "entries" for one id, each defaulting to OPEN
/// because a scoreboard table has no status column. Mirrored to GitHub that was one issue per
/// spelling, every one of them open, four of them saying CLOSED in their own title.</para>
///
/// <para><b>The rule.</b> An id's entry is its FIRST row's description (the rule it declared) with
/// its LATEST explicit verdict (the disposition the file most recently wrote). A row whose disposition
/// says nothing decisive ("unchanged", "re-homed") keeps the previous verdict; a row that says
/// CLOSED, RETIRED or DONE closes it; OPEN, HUMAN, PARTIAL or STILL OPEN keeps it open. Composite
/// cells apply to every id they name. Nothing here rewrites the file — the file is history and stays
/// history; this is the reading that makes it a ledger.</para>
/// </summary>
public static class FollowupLedger
{
    private static readonly Regex IdToken = new(
        @"^(?<id>FU-[A-Za-z0-9]+-[0-9]+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    private static readonly Regex NumberToken = new(
        @"^(?<n>[0-9]+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    private static readonly char[] CellSplit = ['·', ',', '/'];

    /// <summary>The ledger: one entry per id, in first-seen order.</summary>
    public static List<FollowupEntry> Read(string filePath)
    {
        if (!File.Exists(filePath)) return [];
        return Fold(FollowupParser.ReadRows(File.ReadAllLines(filePath, Encoding.UTF8)));
    }

    /// <summary>The fold itself, on rows already read — testable without a file.</summary>
    public static List<FollowupEntry> Fold(IEnumerable<FollowupParser.Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var order = new List<string>();
        var first = new Dictionary<string, FollowupParser.Row>(StringComparer.Ordinal);
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        var latest = new Dictionary<string, FollowupParser.Row>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var ids = SplitIds(row.IdCell);
            if (ids.Count == 0) continue;
            var verdict = row.StatusCell is { Length: > 0 } explicitStatus ? explicitStatus : Infer(row);
            foreach (var id in ids)
            {
                if (!first.ContainsKey(id)) { first[id] = row; order.Add(id); }
                latest[id] = row;
                if (verdict is not null) status[id] = verdict;
            }
        }

        return order.Select(id =>
        {
            var declared = first[id];
            var last = latest[id];
            return new FollowupEntry
            {
                Id = id,
                Item = declared.Item.Length > 0 ? declared.Item : id,
                // The newest row's detail is the newest word on it; the first row's when the newest
                // has none. A disposition cell ("CLOSED by SF7.1, unchanged") is that word too.
                Detail = last.Detail ?? (ReferenceEquals(last, declared) ? null : last.Item) ?? declared.Detail,
                OwningStage = last.Stage.Length > 0 ? last.Stage : declared.Stage,
                Status = status.TryGetValue(id, out var s) ? s : "OPEN",
            };
        }).ToList();
    }

    /// <summary>Every id a cell names: <c>FU-B4-1 · FU-F0-2</c> is two, <c>FU-OWNER-10 · 11 · 13</c>
    /// is three (the bare numbers inherit the prefix of the id before them), <c>FU-OWNER-14 (the
    /// reinstall)</c> is one. A cell with no id at all — "the 43 rows closed by the triage" — is none.</summary>
    public static List<string> SplitIds(string cell)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(cell)) return ids;
        string? prefix = null;
        foreach (var raw in cell.Split(CellSplit, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().Trim('*', '_', '`', ' ');
            if (IdToken.Match(token) is { Success: true } m)
            {
                var id = m.Groups["id"].Value;
                prefix = id[..(id.LastIndexOf('-') + 1)];
                if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
            }
            else if (prefix is not null && NumberToken.IsMatch(token))
            {
                var id = prefix + token;
                if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>The verdict a scoreboard row carries in its disposition cell, or null when the row
    /// decides nothing ("unchanged", "re-homed to K7.2"). Read from the cells after the id, first
    /// decisive word wins.</summary>
    public static string? Infer(FollowupParser.Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var cell in row.CellsAfterId)
        {
            var word = cell.TrimStart('*', '_', '`', ' ').TrimEnd();
            if (word.Length == 0) continue;
            if (Starts(word, "CLOSED") || Starts(word, "RETIRED") || Starts(word, "DONE") || Starts(word, "FIXED"))
                return "CLOSED";
            if (Starts(word, "OPEN") || Starts(word, "STILL OPEN") || Starts(word, "HUMAN") || Starts(word, "`HUMAN")
                || Starts(word, "PARTIAL") || Starts(word, "half closed"))
                return "OPEN";
        }
        return null;
    }

    private static bool Starts(string s, string word) => s.StartsWith(word, StringComparison.OrdinalIgnoreCase);
}
