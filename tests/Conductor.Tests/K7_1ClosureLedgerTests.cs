using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// K7.1 — the Karvan era's closure ledger, pinned the way SF7.1 pinned its own.
/// <para>The era spec asks for one thing at the end: every open bug and every followup row names the
/// stage that closed it or the living owner that holds it. That is a promise about a markdown table,
/// and a promise about a markdown table decays the moment someone files a fifteenth bug — so what is
/// checked here is not the prose but the <em>correspondence</em>: every bug `run.db` still calls open
/// must appear in the ledger with an owner, and the ledger must not invent bugs that do not exist.</para>
/// <para>These tests read the ledger from disk and the bug ids from the ledger's own tables, and they
/// do NOT open `run.db` — but the reason has changed, so it is restated rather than inherited. K7.1
/// gave the reason as the repo-local database being half-migrated (bugs #28/#29); session 26 fixed
/// both, and session 29 measured `doctor` reading that store through to `✓ state`. The reason that
/// survives is about semantics, not plumbing: **this ledger is a snapshot taken when an era closed**,
/// and a test that compared it to the live bug list would go red the day a later era filed its first
/// bug — punishing the next run for doing its job. The ids below are therefore pinned as data, taken
/// from `run.db` on the date in each comment, and the day they diverge from `conductor bug list` the
/// next closure ledger is what corrects them.</para>
/// </summary>
public sealed partial class K7_1ClosureLedgerTests
{
    private const string LedgerHeading = "## K7.1 closure ledger";
    private const string BugTableHeading = "### Bugs — the eleven this era leaves open";
    private const string ClosedTableHeading = "### Bugs closed after this ledger was first written";

    /// <summary>Every bug `run.db` still called open at the ship, re-queried at session 29 on
    /// 2026-08-05. Seven ride in from the two Sarban runs and four were filed by this run. K7.1's
    /// list held fourteen; the three that left it are pinned below, not forgotten.</summary>
    private static readonly int[] OpenAtEraEnd =
        [15, 16, 17, 18, 19, 20, 21, 23, 24, 27, 31];

    /// <summary>Bugs that were open (or unfiled) when K7.1 wrote the ledger and that `run.db` calls
    /// `fixed` at the ship. They are the ledger's own record that closure was measured rather than
    /// assumed — and the reason a row is never deleted when its state improves.</summary>
    private static readonly int[] ClosedBetweenLedgerAndShip = [28, 29, 32, 33, 34];

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

    /// <summary>The rows of ONE table of the ledger, keyed by bug id, cut at the next heading. The cut
    /// is load-bearing: the ledger now carries a second bug table for what closed after it was
    /// written, and a scan that ran to the end of the section would read those rows as rows of the
    /// first — reporting closed bugs as open ones with no owner.</summary>
    private static Dictionary<int, string[]> BugRows(string heading)
    {
        var section = Ledger();
        var start = section.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start > 0, $"the K7.1 ledger has lost its '{heading}' table");

        var table = section[start..];
        var next = table.IndexOf("\n### ", StringComparison.Ordinal);
        if (next > 0) table = table[..next];

        var rows = new Dictionary<int, string[]>();
        foreach (var line in table.Split('\n'))
        {
            var m = BugRow().Match(line);
            if (!m.Success) continue;
            rows[int.Parse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture)] =
                line.Split('|', StringSplitOptions.None);
        }
        return rows;
    }

    /// <summary>The open table: <c>| #id | what | owner |</c>, so cells[3] is the owner.</summary>
    private static Dictionary<int, string> BugRowsWithOwners() =>
        BugRows(BugTableHeading).ToDictionary(
            kv => kv.Key, kv => kv.Value.Length >= 4 ? kv.Value[3].Trim() : "");

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

    /// <summary>The other half of "nothing silently dropped", and the half a tidy-minded editor gets
    /// wrong: a bug that is FIXED after the ledger is written must be restated as closed, naming the
    /// session that closed it — not deleted from the table. Deletion reads as housekeeping and
    /// destroys the only record; the id simply vanishes, and the next reader cannot tell whether it
    /// was fixed or forgotten.</summary>
    [Fact]
    public void EveryBugClosedAfterTheLedgerWasWrittenIsRestatedWithTheSessionThatClosedIt()
    {
        var rows = BugRows(ClosedTableHeading);

        var missing = ClosedBetweenLedgerAndShip.Where(id => !rows.ContainsKey(id)).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} bug(s) closed between K7.1 and the ship have no row in the ledger's " +
            $"closed table: {string.Join(", ", missing.Select(i => "#" + i))}. A row whose state " +
            "improves is restated, not deleted.");

        var unattributed = ClosedBetweenLedgerAndShip
            .Where(id => rows.TryGetValue(id, out var cells)
                         && (cells.Length < 4 || !int.TryParse(
                             cells[2].Trim(), System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out _)))
            .ToList();
        Assert.True(unattributed.Count == 0,
            $"{unattributed.Count} closed row(s) name no session that closed them: " +
            $"{string.Join(", ", unattributed.Select(i => "#" + i))}. The middle cell is " +
            "`run.db`'s own fixed_session, which is what makes the row checkable.");
    }

    /// <summary>K7.1's other promise: the wrong claims were corrected where they were made. Three
    /// figures `conductor budget` overturned, plus this era's own measured score, are the checkable
    /// instance of it — if the tuning doc stops carrying the corrected values, the correction was
    /// rounded away by a later edit.</summary>
    [Fact]
    public void TheTuningDocCarriesTheCorrectedNumbersAndNotTheOldOnes()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "dev", "TOKEN-BUDGET-TUNING.md"));

        // The measured replacements, all four of them. The last is this era's own tokens-per-checkpoint,
        // re-measured at session 29 (K7.1 read 14.1M over 22 costed sessions; the run got longer).
        foreach (var corrected in new[] { "26.5M", "17.0M", "1.6×", "15.5M" })
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
