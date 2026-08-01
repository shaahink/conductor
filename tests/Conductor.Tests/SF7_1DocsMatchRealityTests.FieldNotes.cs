using System.Text.RegularExpressions;
using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// SF7.1, last part — the three <c>docs/dev/FIELD-NOTES-*.md</c> logs carry a closure ledger saying,
/// per finding, which stage fixed it and with which commit. The era spec's Appendix B index was
/// written BEFORE the work and was wrong in three places, so the ledger was measured from the commit
/// bodies instead — several of which cite their finding number outright ("devcontext #10 and #11",
/// "sk #3 verbatim").
/// <para>A ledger of shas rots in two directions and both are silent: a finding gets added above and
/// no row follows it, or a row cites a sha that is not in this history (a typo, or a rebase). These
/// tests make the ledger mechanical — every numbered finding has exactly one row, and every sha the
/// ledger cites resolves to a real commit.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    /// <summary>File name to the number of NUMBERED findings each log carries. The "What worked"
    /// section at the foot of two of these files is numbered too and is not a finding, which is why
    /// the expected count is stated rather than derived from the last heading number.</summary>
    private static readonly (string File, int Findings)[] FieldNotes =
    [
        ("FIELD-NOTES-2026-07-29-devcontext.md", 20),
        ("FIELD-NOTES-2026-07-29-sk-platform.md", 7),
        ("FIELD-NOTES-2026-07-30-sk-fleet-round-four.md", 4),
    ];

    /// <summary>A closure-ledger table row: <c>| 12 | ... |</c>.</summary>
    [GeneratedRegex(@"^\|\s*(?<n>\d+)\s*\|", RegexOptions.Multiline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex LedgerRow();

    /// <summary>A 7-or-more-hex-digit sha in backticks, which is how the ledger cites commits.</summary>
    [GeneratedRegex(@"`(?<sha>[0-9a-f]{7,40})`", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex LedgerSha();

    private static string LedgerOf(string file)
    {
        var doc = Doc("docs", "dev", file);
        var start = doc.IndexOf("## Closure ledger", StringComparison.Ordinal);
        Assert.True(start > 0, $"docs/dev/{file} has lost its SF7.1 closure ledger");
        return doc[start..];
    }

    /// <summary>Every numbered finding in each log has exactly one ledger row, and the ledger has no
    /// row for a finding that does not exist. Add a finding without a row and this goes red.</summary>
    [Fact]
    public void EveryFieldNoteFindingHasExactlyOneClosureRow()
    {
        foreach (var (file, findings) in FieldNotes)
        {
            var expected = Enumerable.Range(1, findings).ToHashSet();

            var headings = Regex.Matches(Doc("docs", "dev", file), @"^## (?<n>\d+)\. ",
                    RegexOptions.Multiline, TimeSpan.FromSeconds(5))
                .Select(m => int.Parse(m.Groups["n"].Value))
                .Where(expected.Contains)
                .ToList();

            Assert.True(headings.Count == findings,
                $"docs/dev/{file} carries {headings.Count} numbered findings in 1..{findings}; " +
                $"the closure ledger is written for {findings}. Renumbering a finding orphans its row.");

            var rows = LedgerRow().Matches(LedgerOf(file))
                .Select(m => int.Parse(m.Groups["n"].Value))
                .ToList();

            var missing = expected.Except(rows).OrderBy(n => n).ToList();
            Assert.True(missing.Count == 0,
                $"docs/dev/{file}: finding(s) {string.Join(", ", missing)} have no closure-ledger row. " +
                "Say which stage closed it and with which commit, or say it is still open and name an owner.");

            var stray = rows.Where(n => !expected.Contains(n)).Distinct().OrderBy(n => n).ToList();
            Assert.True(stray.Count == 0,
                $"docs/dev/{file}: closure-ledger row(s) {string.Join(", ", stray)} cite a finding " +
                "number this log does not have.");

            var duplicated = rows.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicated.Count == 0,
                $"docs/dev/{file}: finding(s) {string.Join(", ", duplicated)} have more than one row — " +
                "a finding with two halves names both commits in ONE row.");
        }
    }

    /// <summary>Every commit the ledgers cite resolves in this repository. A sha that does not is
    /// worse than no citation: it reads as evidence and cannot be checked, which is the exact defect
    /// the followups ledger was fixed for (<c>CLOSED (bFU-OWNER-10)</c> named nothing either).</summary>
    [Fact]
    public void EveryCommitTheClosureLedgersCiteExists()
    {
        var root = RepoRoot();
        var head = ProcessRunner.Run("git", ["rev-parse", "--verify", "HEAD"], root,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(head.ExitCode == 0, "this test needs a git checkout; `git rev-parse HEAD` failed");

        var unresolvable = new List<string>();

        foreach (var (file, _) in FieldNotes)
        {
            foreach (var sha in LedgerSha().Matches(LedgerOf(file))
                         .Select(m => m.Groups["sha"].Value).Distinct())
            {
                var r = ProcessRunner.Run("git", ["cat-file", "-e", sha + "^{commit}"], root,
                    TimeSpan.FromSeconds(30), CancellationToken.None);
                if (r.ExitCode != 0) unresolvable.Add($"{file}:{sha}");
            }
        }

        Assert.True(unresolvable.Count == 0,
            $"{unresolvable.Count} commit(s) cited by a closure ledger do not exist in this history: " +
            string.Join(", ", unresolvable));
    }

    /// <summary>The one finding the ledger closes with a stated remainder. devcontext #19 made two
    /// suggestions; SC2.2 took the numbering one and deliberately left the attempt-budget one. A
    /// closure that quietly swallows half of what was asked for is the species of half-truth this
    /// era exists to kill, so the remainder is pinned in place rather than trusted to survive an
    /// edit.</summary>
    [Fact]
    public void TheOneHalfClosedFindingStillSaysWhatWasNotDone()
    {
        var ledger = LedgerOf("FIELD-NOTES-2026-07-29-devcontext.md");
        var row = ledger.Split('\n').Single(l => l.StartsWith("| 19 |", StringComparison.Ordinal));

        Assert.Contains("Remainder", row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not adopted", row, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Appendix B of the era spec is the index the ledger was measured against and found
    /// wrong. It now says so and points at the ledgers — if someone reverts it to a bare list, a
    /// reader is back to trusting the index that mapped sk-platform #3 to one of its two stages.</summary>
    [Fact]
    public void TheEraSpecIndexDefersToTheMeasuredLedger()
    {
        var spec = Doc("docs", "history", "CONDUCTOR-SARBAN.md");
        var start = spec.IndexOf("# Appendix B", StringComparison.Ordinal);
        Assert.True(start > 0, "the era spec has lost Appendix B");
        var appendix = spec[start..];

        Assert.Contains("Closure ledger", appendix, StringComparison.Ordinal);
        Assert.Contains("#3→SC4.2+SC4.3", appendix, StringComparison.Ordinal);
        Assert.Contains("#10→SC7.1+SC7.2", appendix, StringComparison.Ordinal);
    }
}
