using System.Globalization;
using System.Text;
using Conductor.Core.Store;

namespace Conductor.Core;

/// <summary>
/// DV6.1's one line — how much the ledger is carrying, and how old the oldest of it is — as a fact
/// rather than as a paragraph of one message.
///
/// <para><b>Why it moved out of the digest.</b> DV6.3's board page needed the same sentence, and the
/// alternative was a second count of the same two ledgers: this repo has been bitten by exactly that
/// (a card's age was derived three ways before SF3.2 folded it once, and the three did not agree).
/// The digest still asks for it in the same words; there is now one place that answers.</para>
///
/// <para><b>The age is a BUG age, and says so.</b> followups.md carries no dates at all, so a
/// combined "oldest" would average a measurement with a guess.</para>
///
/// <para><b>An empty ledger renders NOTHING.</b> A surface that says "0 open bugs" every day teaches
/// its reader to skip the line that will one day say eleven.</para>
/// </summary>
public static class LedgerSummary
{
    /// <returns>The line, or "" when there is no store, no ledger, or nothing open.</returns>
    public static string Line(IRunStore? store, string stateDir)
    {
        if (store is null) return "";

        List<BugRow> openBugs;
        try { openBugs = [.. store.QueryBugLedger().Select(b => b.Bug).Where(b => string.Equals(b.Status, "open", StringComparison.OrdinalIgnoreCase))]; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return ""; }
        catch (InvalidOperationException) { return ""; }

        var followups = 0;
        try
        {
            var path = Path.Combine(stateDir, "followups.md");
            if (File.Exists(path)) followups = FollowupParser.Read(path).Count(FollowupParser.IsOpen);
        }
        catch (IOException) { /* best-effort: this line is advisory, and a locked file is not news */ }

        if (openBugs.Count == 0 && followups == 0) return "";

        var line = new StringBuilder("ledger: ")
            .Append(Count(openBugs.Count, "open bug"))
            .Append(" · ")
            .Append(Count(followups, "open followup"));
        if (OldestDays(openBugs) is { } days) line.Append(" · oldest bug ").Append(Count(days, "day"));
        return line.ToString();
    }

    /// <summary>Whole days since the oldest open bug was filed, or null when nothing is open or
    /// nothing carries a readable date. Floored: a bug filed 47 hours ago is one day old, because
    /// rounding up would make a bug filed this morning "1 day" and cost the line its meaning.</summary>
    private static int? OldestDays(IReadOnlyList<BugRow> open)
    {
        DateTime? oldest = null;
        foreach (var bug in open)
        {
            // SQLite's datetime('now') writes "2026-08-26 09:12:33" with no zone marker, and it is
            // UTC. Parsed as universal on purpose: read as local time, every age here would be wrong
            // by the operator's offset, which is invisible until someone runs this in Tehran.
            if (!DateTime.TryParse(bug.CreatedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var filed)) continue;
            if (oldest is null || filed < oldest) oldest = filed;
        }
        if (oldest is null) return null;
        var days = (int)(DateTime.UtcNow - oldest.Value).TotalDays;
        return days < 0 ? 0 : days;
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");
}
