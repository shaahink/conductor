using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// SF7.1 part 2 — the closure ledger. The era's contract with <c>.conductor/followups.md</c> is that
/// no row's state is unstated and no row is ever silently dropped. That contract was being kept by
/// hand, and by hand it failed twice: three rows carried the status <c>CLOSED (bFU-OWNER-NN)</c> — a
/// token that is not a commit, not a stage and not a bug id, so the rows read as closed to a skimmer
/// and as nothing at all to anyone checking — and <c>FU-F1-06</c> rode a whole stage on a premise
/// that was never true.
/// <para>These tests make both failures mechanical instead of a re-read.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    private static string Followups() => Doc(".conductor", "followups.md");

    [GeneratedRegex(@"FU-[A-Z0-9]+-\d+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex FollowupId();

    /// <summary>Every followup that appears as a TABLE ROW must say what became of it. Prose mentions
    /// are exempt — a row is the ledger, a sentence is commentary.</summary>
    [Fact]
    public void EveryFollowupRowStatesADisposition()
    {
        string[] dispositions =
            ["CLOSED", "OPEN", "HUMAN", "PARTIAL", "RETIRED", "obsolete", "re-homed", "closed", "open"];

        var undecided = Followups().Split('\n')
            .Where(l => l.StartsWith('|') && FollowupId().IsMatch(l))
            .Where(l => !dispositions.Any(d => l.Contains(d, StringComparison.Ordinal)))
            .Select(l => FollowupId().Match(l).Value)
            .ToList();

        Assert.True(undecided.Count == 0,
            $"{undecided.Count} followup row(s) state no disposition: {string.Join(", ", undecided)}. " +
            "Close it with the commit that closed it, or name a living owner — never leave it blank.");
    }

    /// <summary>The exact defect this checkpoint found. A closure has to name something a reader can
    /// go and check: a stage, a commit, or a file. <c>CLOSED (bFU-OWNER-10)</c> names the row itself,
    /// which is circular — it says "this is closed because it is closed".</summary>
    [Fact]
    public void NoFollowupIsClosedByATokenThatNamesNothing()
    {
        var circular = Followups().Split('\n')
            .Where(l => Regex.IsMatch(l, @"CLOSED\s*\(b?FU-[A-Z0-9]+-\d+\)",
                RegexOptions.None, TimeSpan.FromSeconds(5)))
            .ToList();

        Assert.True(circular.Count == 0,
            $"{circular.Count} row(s) are 'closed' by a token that is not a commit, a stage or a file " +
            "— it is the row's own id. Name the evidence a reader can verify.");
    }

    /// <summary>Every bug the closure ledger lists must carry an owner cell. The seven this run leaves
    /// open ride <c>run.db</c> into the next era (SF0.4 made bugs outlive the run that filed them), so
    /// this table is a completeness record rather than the source — but a bug listed with an empty
    /// owner is exactly the homeless row the ledger exists to prevent.</summary>
    [Fact]
    public void EveryBugInTheClosureLedgerNamesAnOwner()
    {
        var doc = Followups();
        var start = doc.IndexOf("### Bugs — the seven this run leaves open", StringComparison.Ordinal);
        Assert.True(start > 0, ".conductor/followups.md has lost the SF7.1 bug closure ledger");
        var section = doc[start..];

        var rows = section.Split('\n')
            .Where(l => Regex.IsMatch(l, @"^\|\s*#\d+\s*\|", RegexOptions.None, TimeSpan.FromSeconds(5)))
            .ToList();

        Assert.True(rows.Count >= 7,
            $"the SF7.1 bug ledger lists {rows.Count} bug rows; this run left seven open and each must appear");

        foreach (var row in rows)
        {
            var cells = row.Split('|', StringSplitOptions.None);
            var owner = cells.Length >= 4 ? cells[3].Trim() : "";
            Assert.False(owner.Length == 0,
                $"a bug row in the closure ledger has no owner: {row.Trim()}");
        }
    }

    /// <summary>
    /// FU-F1-06 was the one row the SF7.1 era could not close, and this test was its converse pin: a
    /// claim that something was MISSING, checked against the tree. It said that no status-only writer
    /// for <c>runs.status</c> existed — only <c>RecordRunEnd</c>, which stamps <c>ended_utc</c> and is
    /// therefore wrong for a resumable state — and that the day someone added one, the row was owed a
    /// closure.
    /// <para>KS0.2 added it, so the pin turns over rather than being deleted: the writer must exist,
    /// it must be called by the engine (a writer nothing calls closes nothing), and the ledger must
    /// say so. Same shape, same scan, opposite direction — which is the only honest way to retire a
    /// pin that has just been satisfied.</para>
    /// </summary>
    [Fact]
    public void TheOneRowLeftOpenClosedAndTheLedgerSaysSo()
    {
        // K7.1: widened from src/Conductor to src/. K2.1 moved the store into Conductor.Core, which is
        // a SIBLING of src/Conductor and not a child of it, so this scan had quietly stopped covering
        // the only assembly that could ever contain the writer - the pin was green on half a tree.
        // The Backlog half of these tests took the same correction at K2.1; this half was missed.
        var src = Path.Combine(RepoRoot(), "src");
        var writers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("UpdateRunStatus", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Contains("SqliteRunStore.Sessions.cs", writers);
        Assert.Contains("IRunStore.cs", writers);
        Assert.True(writers.Contains("RunContext.cs"),
            "UpdateRunStatus exists but the engine never calls it, so a parked run still reads " +
            "running - FU-F1-06 is not actually closed. The call belongs on the path every park " +
            "already takes (RunContext.Save), not at each transition.");

        Assert.Contains("FU-F1-06 closed at KS0.2", Followups(), StringComparison.Ordinal);
        Assert.DoesNotContain("FU-F1-06 does not close", Followups(), StringComparison.Ordinal);
    }
}
