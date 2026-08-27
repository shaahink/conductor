using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// KS10.1 part 3 — the karvansara era's own doc claims, pinned to the artifacts they came from.
/// <para>The SF7.1 family's rule is that a doc claim is DERIVED from something checkable rather than
/// restated. The budget section is the sharpest case this repo has: <c>TOKEN-BUDGET-TUNING.md</c>
/// carries a prescribed ceiling and ratio that <c>karvansara-edge</c> compiles against, and the only
/// thing standing between that number and a typo has been a session's care. Here the doc is checked
/// against the raw <c>budget --json</c> output committed beside it, so a hand-edited figure — or a
/// re-measure whose evidence was never refreshed — is a red test.</para>
/// <para>The other two pins are about a doc pointing at a CLOSED era, which is the drift that
/// survives longest because nothing about it looks wrong.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    private const string BudgetEvidence = "ks10-1-budget-remeasure.json";
    private const string KarvansaraRunId = "9647f1b80d1841e9997a801562a267c7";

    private static string TokenBudgetTuning() => Doc("docs", "dev", "TOKEN-BUDGET-TUNING.md");

    /// <summary>The current-era section of TOKEN-BUDGET-TUNING must name the verb that produced its
    /// figures AND agree with that verb's committed output. Both halves matter: naming the command
    /// without the raw output is a claim, and raw output nobody compares against is an attachment.</summary>
    [Fact]
    public void TheCurrentEraBudgetSectionAgreesWithTheRawOutputItQuotes()
    {
        var doc = TokenBudgetTuning();
        var start = doc.IndexOf("## 10.", StringComparison.Ordinal);
        Assert.True(start > 0,
            "TOKEN-BUDGET-TUNING.md has lost its karvansara section (§10). The era's measured ceiling " +
            "and ratio are what the next plan compiles against - they do not live only in a commit message.");
        var section = doc[start..];

        Assert.Contains("budget --json", section, StringComparison.Ordinal);
        Assert.Contains("money --json", section, StringComparison.Ordinal);
        Assert.Contains(BudgetEvidence, section, StringComparison.Ordinal);

        var evidence = Path.Combine(RepoRoot(), ".conductor", "evidence", "KS10", BudgetEvidence);
        Assert.True(File.Exists(evidence),
            $"§10 cites {BudgetEvidence} and it is not in the tree. A figure whose source cannot be " +
            "opened is a typed number wearing a citation.");

        using var json = JsonDocument.Parse(File.ReadAllText(evidence));
        var run = json.RootElement.GetProperty("runs").EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("runId").GetString() == KarvansaraRunId);
        Assert.True(run.ValueKind == JsonValueKind.Object,
            $"{BudgetEvidence} holds no window for the karvansara run - it was re-measured against a " +
            "different store than the one the section describes.");

        var prescription = run.GetProperty("prescription");
        var cap = prescription.GetProperty("maxSessionTokens").GetInt64();
        var ratio = prescription.GetProperty("softBreakRatio").GetDouble();

        // The doc states the ceiling in millions and the ratio verbatim. Derive both expectations from
        // the JSON rather than restating them, so a fresh re-measure that moves either one lands here.
        var capM = (cap / 1_000_000L).ToString(CultureInfo.InvariantCulture) + "M";
        Assert.Contains(capM, section, StringComparison.Ordinal);
        Assert.Contains(ratio.ToString("0.00", CultureInfo.InvariantCulture), section, StringComparison.Ordinal);

        // And the plan doc the prescription overrode must carry the SAME pair, because that is the one
        // karvansara-edge is compiled from. KS10.1's rule: where they disagree, the prescription wins.
        //
        // Checking `plan.Contains("32M / 0.85")` is NOT enough and this test was written that way
        // first: the doc names the pair in its prose too, so a stale PRESCRIPTION passed while a
        // sentence elsewhere carried the right number. Seeded and confirmed. So the assertion is
        // scoped to the prescription bullet, and struck-through values are excluded on purpose - a
        // superseded number is meant to stay visible.
        var pair = $"{capM} / {ratio.ToString("0.00", CultureInfo.InvariantCulture)}";
        var plan = Doc("docs", "history", "KARVANSARA-PLAN-2026-08-13.md");
        var bulletStart = plan.IndexOf("- **Keep ", StringComparison.Ordinal);
        Assert.True(bulletStart > 0,
            "KARVANSARA-PLAN's budget prescription bullet ('- **Keep …') is gone. It is the line " +
            "karvansara-edge is compiled from; it does not get to disappear silently.");
        var bulletEnd = plan.IndexOf("\n- ", bulletStart + 3, StringComparison.Ordinal);
        var bullet = bulletEnd > 0 ? plan[bulletStart..bulletEnd] : plan[bulletStart..];

        var live = Regex.Replace(bullet, "~~.*?~~", "",
            RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));
        var stated = Regex.Matches(live, @"\*\*(?<pair>\d+M / 0\.\d\d)\*\*",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups["pair"].Value)
            .ToList();

        Assert.True(stated.Count > 0,
            $"the prescription bullet states no ceiling/ratio pair at all; `budget` prescribes {pair}");
        foreach (var s in stated)
        {
            Assert.True(string.Equals(s, pair, StringComparison.Ordinal),
                $"the plan doc prescribes {s} and `budget` measured {pair} ({BudgetEvidence}). " +
                "KS10.1's rule is that the prescription wins - correct the doc in place, striking " +
                "the old value rather than deleting it.");
        }
    }

    /// <summary>An era's brief: one word, then PLAN, then the date it was compiled. It lives in
    /// <c>docs/dev/</c> while its era is open and moves to <c>docs/history/</c> at the close.</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9]*-PLAN-\d{4}-\d{2}-\d{2}\.md$", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex EraBrief();

    /// <summary>The contributor index must not leave a closed era sitting in the "design authority
    /// for current work" row - the drift nothing looks wrong about: the link resolves, the file
    /// exists, and every word of it is about work that already shipped.
    /// <para>DV7.3 moved BOTH eras to <c>history/</c> and left no era open, so this was pinned to the
    /// sentence that says so - and at CH5.1 that pin asserted the OPPOSITE of its own intent, because
    /// Charkh had opened and the index correctly stopped saying "No era is open". A literal cannot
    /// carry a contract whose two halves are both legitimate.</para>
    /// <para>So it is a property now: <b>an era brief is a <c>docs/dev/</c> file named
    /// <c>ERA-PLAN-yyyy-MM-dd.md</c></b>, and its presence IS the fact. None there - the section must
    /// SAY there is no design authority. One or more - the section must name each of them and must
    /// not claim there is none. The doc-move act inside <c>release perform</c> is what carries a brief
    /// to <c>history/</c> at the close, so this test flips back on its own the moment it runs; nobody
    /// edits an assertion to close an era.</para>
    /// <para>Either way the old paths are asserted GONE, because a copy left behind at the old
    /// location is exactly the stale-but-resolving link this test exists to catch.</para></summary>
    [Fact]
    public void TheContributorIndexClaimsTheDesignAuthorityTheTreeActuallyHas()
    {
        var readme = Doc("docs", "dev", "README.md");
        var current = readme[readme.IndexOf("## Current work", StringComparison.Ordinal)..];
        current = current[..current.IndexOf("## Findings", StringComparison.Ordinal)];

        // Markdown wraps, so the claim is checked against the section flattened to one line.
        var flat = string.Join(' ', current.Split('\n', StringSplitOptions.TrimEntries));

        // The tree decides which of the two claims is the true one. The prefix is deliberately one
        // word: NEXT-ERA-VERIFIED-PLAN-2026-08-07.md is a research note, not an era's brief.
        var briefs = Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "dev"), "*.md")
            .Select(Path.GetFileName)
            .Where(f => f is not null && EraBrief().IsMatch(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (briefs.Count == 0)
        {
            Assert.Contains("No era is open", flat, StringComparison.Ordinal);
            Assert.Contains("nothing in this repo is the design authority for current work", flat,
                StringComparison.Ordinal);

            // The affirmative row is the thing that must not come back while no era is in flight.
            Assert.DoesNotContain("**The design authority for current work.**", flat, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("No era is open", flat, StringComparison.Ordinal);
            Assert.DoesNotContain("nothing in this repo is the design authority for current work", flat,
                StringComparison.Ordinal);
            Assert.Contains("the design authority for current work", flat, StringComparison.Ordinal);

            foreach (var brief in briefs)
            {
                Assert.True(flat.Contains(brief!, StringComparison.Ordinal),
                    $"docs/dev/{brief} is an open era's brief - it has not moved to history/ - and the " +
                    "Current work section never names it. A reader arriving at the contributor index is " +
                    "pointed at the wrong document, or at none.");
            }
        }

        Assert.Contains("../history/archive/trackers/KARVANSARA-CORE-TRACKER.md", current, StringComparison.Ordinal);

        // Every file the rows promise has to exist - a table row is a navigation contract.
        foreach (var relative in new[]
                 {
                     Path.Combine("docs", "history", "KARVANSARA-PLAN-2026-08-13.md"),
                     Path.Combine("docs", "history", "NEXT-ERA-FINDINGS-2026-08-23.md"),
                     Path.Combine("docs", "history", "archive", "trackers", "KARVANSARA-CORE-TRACKER.md"),
                     Path.Combine("docs", "history", "archive", "trackers", "KARVANSARA-EDGE-TRACKER.md"),
                     Path.Combine("docs", "history", "archive", "trackers", "DIVAN-TRACKER.md"),
                 })
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot(), relative)),
                $"docs/dev/README.md points at {relative} and it is not there.");
        }

        // And the old locations are gone. A file that moved but left a copy keeps every stale
        // reference resolving, which is the failure mode this whole file is about.
        foreach (var moved in new[]
                 {
                     Path.Combine("docs", "dev", "KARVANSARA-PLAN-2026-08-13.md"),
                     Path.Combine("docs", "dev", "NEXT-ERA-FINDINGS-2026-08-23.md"),
                     Path.Combine("plans", "karvansara", "CORE-TRACKER.md"),
                     Path.Combine("plans", "karvansara", "EDGE-TRACKER.md"),
                     Path.Combine("plans", "divan", "TRACKER.md"),
                 })
        {
            Assert.False(File.Exists(Path.Combine(RepoRoot(), moved)),
                $"{moved} moved to docs/history/ at DV7.3 — a copy left at the old path keeps every " +
                "stale reference resolving.");
        }
    }

    /// <summary>The karvansara closure ledger must account for the four bugs that live in KARVAN's
    /// store and therefore reach no prompt in this repo. Everything else in the ledger is checkable
    /// against a store a later session can open; these four are checkable against nothing, which is
    /// exactly why they need a pin. If the carry-forward defect (#46) is ever fixed and they migrate
    /// in, this test is the reminder to say so rather than to delete the rows.</summary>
    [Fact]
    public void TheKarvansaraLedgerAccountsForTheBugsThatLiveInAnotherStore()
    {
        var doc = Followups();
        var start = doc.IndexOf("## KS10.1 closure ledger", StringComparison.Ordinal);
        Assert.True(start > 0, ".conductor/followups.md has lost the KS10.1 closure ledger");
        var section = doc[start..];

        foreach (var orphan in new[] { "#24", "#27", "#31", "#35" })
        {
            var row = section.Split('\n').FirstOrDefault(l =>
                Regex.IsMatch(l, $@"^\|\s*{Regex.Escape(orphan)}\s*\|",
                    RegexOptions.None, TimeSpan.FromSeconds(5)));
            Assert.True(row is not null,
                $"bug {orphan} lives only in karvan's store, so `conductor bug list` in this repo " +
                "cannot show it. The ledger row IS its carrier - dropping it drops the bug.");
            Assert.Contains("karvan's store only", row!, StringComparison.Ordinal);
        }
    }
}
