using Conductor.Core;
using System.Text;

namespace Conductor.Tests;

/// <summary>
/// K1.3 rewrote this class along with the manager. It used to pin the diary contract — "an append
/// always writes an entry, whatever the text said" — and that contract is what made
/// <c>.conductor/lessons.md</c> a file of truncated session narratives that <c>LessonsBattery</c>
/// pasted into every following prompt. The tests are replaced by tests of the rules contract, not
/// deleted: create, newest-first, the byte cap, the ReadRecent limit, the empty-file cases and the
/// directory creation are all still measured, plus the three things that are new — extraction,
/// dedup, and the duplicate-append regression.
/// </summary>
public sealed class LessonsManagerTests : IDisposable
{
    private readonly string _tmpDir;

    public LessonsManagerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"conductor-lessons-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    [Fact]
    public void AppendWritesTheRuleTaggedWithItsSource()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B8", 1, "Never assert on a flaky async drain before ReadAll has returned.");

        var content = mgr.ReadContent();
        Assert.Contains("B8-1", content);
        Assert.Contains("Never assert on a flaky async drain", content);
        Assert.Contains("# Lessons (rules", content);
        Assert.Equal(1, mgr.EntryCount());
    }

    /// <summary>The point of the rewrite. A SESSION-RESULT is mostly status, and status in the next
    /// session's prompt is rent for nothing — so a result that states no rule writes no file at all.</summary>
    [Fact]
    public void AResultThatStatesNoRuleWritesNothing()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("K1", 2, "Landed two checkpoints, each claimed with a fresh evidence artifact and "
                            + "committed separately. Scoped suites green at 143/143. Next session picks up K1.3.");

        Assert.Equal("", mgr.ReadContent());
        Assert.Equal(0, mgr.EntryCount());
        Assert.False(File.Exists(Path.Combine(_tmpDir, "lessons.md")));
    }

    /// <summary>Only the rule-shaped sentences survive a real, mixed report — the status around them
    /// does not come with it.</summary>
    [Fact]
    public void OnlyRuleShapedSentencesAreExtracted()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("K1", 3,
            "Landed the ledger fix across three commits and pushed feat/karvan. "
            + "Never point the fresh build at this repo's .conductor: opening it migrates the schema. "
            + "The gate battery is green. "
            + "A live harness agent must be wired through powershell -File, never cmd.exe.");

        var content = mgr.ReadContent();
        Assert.Equal(2, mgr.EntryCount());
        Assert.Contains("Never point the fresh build", content);
        Assert.Contains("must be wired through powershell -File", content);
        Assert.DoesNotContain("Landed the ledger fix", content);
        Assert.DoesNotContain("The gate battery is green", content);
    }

    /// <summary>Measured against the real file, not a straw man. These are the opening sentences of
    /// two of the five entries this repo's own <c>.conductor/lessons.md</c> was carrying on
    /// 2026-08-04 — the SF7-40 and SF7-38 narratives, the latter being the one stored twice. Not one
    /// sentence in either states a rule, which is the finding behind K1.3: the file was pure diary,
    /// and <c>LessonsBattery</c> was paying cache-read rent to put it in every following prompt.</summary>
    [Fact]
    public void TheRealDiaryEntriesThisFileCarried_YieldNoRulesAtAll()
    {
        var sf7_40 = "SF7.2 delivered and claimed DONE (`conductor task --done SF7.2`). Both remaining "
            + "clauses of the checkpoint closed this session: the CHANGELOG `[Unreleased]` section was "
            + "cut to `[0.3.0] - 2026-08-01` on `master` (commit `e897c2c`, via a scratch worktree so "
            + "`feat/sarban`'s pre-existing dirty state was untouched), tagged `v0.3.0`, and pushed.";
        var sf7_38 = "Landed four commits against SF7.1, all pushed on `feat/sarban`, tree clean, "
            + "`SF7_1DocsMatchRealityTests` green at 12/12 across four partial files. (1) `3268e54` - "
            + "`docs/dev/NEXT-FEATURES.md` was listing ten already-shipped features as future work; "
            + "refreshed into shipped/open sections each naming what closed it.";

        Assert.Empty(LessonsManager.ExtractRules(sf7_40));
        Assert.Empty(LessonsManager.ExtractRules(sf7_38));

        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("SF7", 40, sf7_40);
        mgr.Append("SF7", 38, sf7_38);
        Assert.Equal("", mgr.ReadContent());
    }

    /// <summary>THE REGRESSION. The old TrimToCap re-parsed the content it had already prepended the
    /// new entry to and emitted that entry a second time, so any append crossing the byte cap
    /// duplicated itself — which is why this repo's own file carried the SF7-38 entry twice.</summary>
    [Fact]
    public void AnAppendThatCrossesTheCapNeverDuplicatesItself()
    {
        var mgr = new LessonsManager(_tmpDir, maxBytes: 1024);
        for (var i = 1; i <= 40; i++)
            mgr.Append("B0", i, $"Never let rule number {i} regress, because " + new string('x', 60));

        var content = mgr.ReadContent();
        var lines = content.Split('\n').Where(l => l.TrimStart().StartsWith("- [", StringComparison.Ordinal)).ToList();
        Assert.Equal(lines.Count, lines.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(lines.Count, mgr.EntryCount());
        Assert.True(Encoding.UTF8.GetByteCount(content) <= 1152,
            $"content {Encoding.UTF8.GetByteCount(content)} bytes should stay near the 1024 cap");
        Assert.Contains("B0-40", content);
        Assert.DoesNotContain("B0-1]", content);   // oldest evicted, and the newest is not a copy
    }

    /// <summary>The same lesson learned twice is one lesson, whichever session says it — and it keeps
    /// the position it already had rather than jumping the queue on a restatement.</summary>
    [Fact]
    public void TheSameRuleFromTwoSessionsIsStoredOnce()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B7", 45, "Never kill a dotnet process by name.");
        mgr.Append("B8", 46, "Never kill a dotnet process by name!");   // same rule, restated
        mgr.Append("B9", 47, "Always give a scratch rig its own port.");

        Assert.Equal(2, mgr.EntryCount());
        var content = mgr.ReadContent();
        Assert.Contains("B7-45", content);
        Assert.DoesNotContain("B8-46", content);
        Assert.Contains("B9-47", content);
    }

    [Fact]
    public void NewestRuleIsListedFirst()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B7", 45, "Never regenerate a golden in the same commit as the behaviour change.");
        mgr.Append("B8", 1, "Always read the port back from the rig's own discovery file.");

        var content = mgr.ReadContent();
        var idxFirst = content.IndexOf("B7-45", StringComparison.Ordinal);
        var idxSecond = content.IndexOf("B8-1", StringComparison.Ordinal);
        Assert.True(idxSecond >= 0);
        Assert.True(idxSecond < idxFirst, "newest rule should appear before older ones");
    }

    /// <summary>A rule long enough to be a paragraph is cut on a WORD boundary. The old file was cut
    /// at 500 characters mid-word, which is half of why it read as unfinished.</summary>
    [Fact]
    public void AnOverlongRuleIsCutOnAWordBoundary()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B1", 9, "Never " + string.Join(" ", Enumerable.Repeat("something", 60)) + " end.");

        var rule = mgr.ReadContent().Split('\n').Single(l => l.StartsWith("- [", StringComparison.Ordinal));
        Assert.EndsWith("…", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("someth…", rule, StringComparison.Ordinal);
        Assert.True(rule.Length < 240, $"rule line is {rule.Length} chars");
    }

    [Fact]
    public void ReadRecentReturnsLimitedRules()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B5", 20, "Never trust the plan-set comment about which plan is loaded.");
        mgr.Append("B6", 30, "Always check a pid's command line before touching it.");
        mgr.Append("B7", 40, "Never raise the pragma ceiling to get the ratchet green.");

        var recent = mgr.ReadRecent(2);
        Assert.Contains("B7-40", recent);
        Assert.Contains("B6-30", recent);
        Assert.DoesNotContain("B5-20", recent); // limited to 2
    }

    [Fact]
    public void ReadContentReturnsEmptyWhenNoFile()
    {
        var mgr = new LessonsManager(_tmpDir);
        Assert.Equal("", mgr.ReadContent());
    }

    [Fact]
    public void ReadRecentReturnsEmptyWhenNoFile()
    {
        var mgr = new LessonsManager(_tmpDir);
        Assert.Equal("", mgr.ReadRecent(5));
    }

    /// <summary>A pre-K1.3 diary file contributes no rules and is not mistaken for one. It is
    /// rotating runtime state, so the first append simply rewrites it in the new format.</summary>
    [Fact]
    public void AnOldDiaryFileYieldsNoRulesAndIsRewritten()
    {
        Directory.CreateDirectory(_tmpDir);
        File.WriteAllText(Path.Combine(_tmpDir, "lessons.md"),
            "# Lessons learned (auto-rotating, newest first)\n\n"
            + "> **Last updated:** 2026-08-01T17:45:00.0000000+00:00\n\n"
            + "## SF7-40 — 2026-08-01 17:45 UTC\nSF7.2 delivered and claimed DONE, both remaining\n\n---\n",
            Encoding.UTF8);

        var mgr = new LessonsManager(_tmpDir);
        Assert.Equal(0, mgr.EntryCount());
        Assert.Equal("", mgr.ReadRecent(3));

        mgr.Append("K1", 3, "Never trust a doc comment about behaviour; measure what the code does.");
        var content = mgr.ReadContent();
        Assert.Equal(1, mgr.EntryCount());
        Assert.DoesNotContain("SF7-40", content);
    }

    [Fact]
    public void EntryCountReflectsStoredRules()
    {
        var mgr = new LessonsManager(_tmpDir);
        Assert.Equal(0, mgr.EntryCount());

        mgr.Append("B0", 1, "Never edit a gate into passing.");
        Assert.Equal(1, mgr.EntryCount());

        mgr.Append("B0", 2, "Always leave the working tree clean.");
        Assert.Equal(2, mgr.EntryCount());
    }

    [Fact]
    public void WhitespaceOnlyTextWritesNothing()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B1", 5, "   ");

        Assert.Equal(0, mgr.EntryCount());
        Assert.Equal("", mgr.ReadContent());
    }

    /// <summary>One verbose session cannot flood the file: at most three rules per append.</summary>
    [Fact]
    public void OneSessionContributesAtMostThreeRules()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B2", 7, string.Join(" ", Enumerable.Range(1, 8)
            .Select(i => $"Never do the thing numbered {i} in a hurry.")));

        Assert.Equal(3, mgr.EntryCount());
    }

    [Fact]
    public void DirIsCreatedAutomatically()
    {
        var deepDir = Path.Combine(_tmpDir, "nested", "path");
        Assert.False(Directory.Exists(deepDir));

        var mgr = new LessonsManager(deepDir);
        mgr.Append("B8", 1, "Never assume the directory already exists.");

        Assert.True(Directory.Exists(deepDir));
        Assert.True(File.Exists(Path.Combine(deepDir, "lessons.md")));
    }
}
