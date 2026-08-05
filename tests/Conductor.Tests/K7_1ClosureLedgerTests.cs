using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// K7.1 — the Karvan era's closure ledger, pinned the way SF7.1 pinned its own.
/// <para>The era spec asks for one thing at the end: every open bug and every followup row names the
/// stage that closed it or the living owner that holds it. That is a promise about a markdown table,
/// and a promise about a markdown table decays the moment someone files a fifteenth bug — so what is
/// checked here is not the prose but the <em>correspondence</em>: every bug `run.db` still calls open
/// must appear in the ledger with an owner, and the ledger must not invent bugs that do not exist.</para>
/// <para>These tests read the ledger from disk and the bug ids from the ledger's own table. They
/// deliberately do NOT open `run.db`: the database moved to a machine-level home at K3.1 and the
/// repo-local copy is half-migrated (bugs #28/#29), so a test that opened it would be testing the
/// migration, not the ledger. The list of open ids is therefore pinned as data below, and the day it
/// diverges from `conductor bug list` the next closure ledger is what corrects it.</para>
/// </summary>
public sealed partial class K7_1ClosureLedgerTests
{
    private const string LedgerHeading = "## K7.1 closure ledger";
    private const string BugTableHeading = "### Bugs — the fourteen this era leaves open";

    /// <summary>Every bug id that `conductor bug list` reported open on 2026-08-05, the day the era
    /// closed. Seven ride in from the two Sarban runs, six were filed by this run's own stages, and
    /// #32 was filed by this checkpoint.</summary>
    private static readonly int[] OpenAtEraEnd =
        [15, 16, 17, 18, 19, 20, 21, 23, 24, 27, 28, 29, 31, 32];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Ledger()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), ".conductor", "followups.md"));
        var start = doc.IndexOf(LedgerHeading, StringComparison.Ordinal);
        Assert.True(start > 0,
            $".conductor/followups.md has lost the '{LedgerHeading}' section - it is the Karvan era's " +
            "reconciliation and K7.1's evidence that nothing was left homeless.");
        return doc[start..];
    }

    [GeneratedRegex(@"^\|\s*#(?<id>\d+)\s*\|", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BugRow();

    private static Dictionary<int, string> BugRowsWithOwners()
    {
        var section = Ledger();
        var start = section.IndexOf(BugTableHeading, StringComparison.Ordinal);
        Assert.True(start > 0, $"the K7.1 ledger has lost its bug table ('{BugTableHeading}')");

        var rows = new Dictionary<int, string>();
        foreach (var line in section[start..].Split('\n'))
        {
            var m = BugRow().Match(line);
            if (!m.Success) continue;
            var cells = line.Split('|', StringSplitOptions.None);
            // | #id | what | owner |  ->  cells[1]=id, cells[2]=what, cells[3]=owner
            rows[int.Parse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture)] =
                cells.Length >= 4 ? cells[3].Trim() : "";
        }
        return rows;
    }

    /// <summary>The load-bearing one. A bug that is open and absent from the ledger is precisely the
    /// homeless row the checkpoint exists to abolish, and it is the failure mode that is invisible by
    /// reading — the ledger looks complete right up until you count it.</summary>
    [Fact]
    public void EveryBugOpenAtEraEndAppearsInTheClosureLedger()
    {
        var rows = BugRowsWithOwners();
        var missing = OpenAtEraEnd.Where(id => !rows.ContainsKey(id)).ToList();

        Assert.True(missing.Count == 0,
            $"{missing.Count} bug(s) were open when the Karvan era closed but appear in no row of the " +
            $"K7.1 closure ledger: {string.Join(", ", missing.Select(i => "#" + i))}. Add the row with " +
            "an owner, or say in the ledger why the bug was closed.");
    }

    /// <summary>The converse. A ledger row for a bug nobody filed reads as diligence and is noise: it
    /// sends the next era looking for something that was never there.</summary>
    [Fact]
    public void TheLedgerInventsNoBugs()
    {
        var extra = BugRowsWithOwners().Keys.Where(id => !OpenAtEraEnd.Contains(id)).ToList();

        Assert.True(extra.Count == 0,
            $"the K7.1 closure ledger has row(s) for {string.Join(", ", extra.Select(i => "#" + i))}, " +
            "which were not open at era end. Either the bug list moved on and the ledger owes an " +
            "update, or the row is a typo.");
    }

    /// <summary>An owner cell is the whole point of the table. "next era", "the owner" and "K7.2" are
    /// all acceptable answers; blank is not, and neither is a cell that only repeats the bug.</summary>
    [Fact]
    public void EveryBugRowNamesSomethingThatCouldActOnIt()
    {
        string[] owners = ["next era", "owner", "K7.", "K6.", "lane"];

        var homeless = BugRowsWithOwners()
            .Where(kv => !owners.Any(o => kv.Value.Contains(o, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => $"#{kv.Key} -> '{kv.Value}'")
            .ToList();

        Assert.True(homeless.Count == 0,
            $"{homeless.Count} bug row(s) in the K7.1 ledger name no owner who could act: " +
            $"{string.Join("; ", homeless)}. Name a stage, a lane, or the owner.");
    }

    /// <summary>K7.1's other promise: the wrong claims were corrected where they were made. The four
    /// figures `conductor budget` overturned are the checkable instance of it — if the tuning doc
    /// stops carrying the corrected values, the correction was rounded away by a later edit.</summary>
    [Fact]
    public void TheTuningDocCarriesTheCorrectedNumbersAndNotTheOldOnes()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "dev", "TOKEN-BUDGET-TUNING.md"));

        // The measured replacements, all four of them.
        foreach (var corrected in new[] { "26.5M", "17.0M", "1.6×", "14.1M" })
            Assert.True(doc.Contains(corrected, StringComparison.Ordinal),
                $"docs/dev/TOKEN-BUDGET-TUNING.md no longer carries the measured value '{corrected}'. " +
                "K7.1 replaced four hand-derived figures with what conductor budget read from the " +
                "ledger; losing one of them restores the claim it corrected.");

        // And the old ones survive only struck through, as the record of what was wrong.
        foreach (var old in new[] { "58.1M", "14.7M", "4.0×" })
            Assert.True(doc.Contains("~~" + old, StringComparison.Ordinal) ||
                        doc.Contains(old + "~~", StringComparison.Ordinal) ||
                        doc.Contains("~~14.7M ($11.7)~~", StringComparison.Ordinal),
                $"'{old}' appears in TOKEN-BUDGET-TUNING.md without being struck through. A corrected " +
                "number is kept as a record of the mistake, not quietly restored as fact.");

        // The rule itself, which is the part a reader copies into their own plan.
        Assert.Contains("median closing session", doc, StringComparison.Ordinal);
    }
}
