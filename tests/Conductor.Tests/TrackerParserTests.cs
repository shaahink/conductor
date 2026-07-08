using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

public class TrackerParserTests
{
    private const string Sample = """
        # Loom — Phase Tracker

        ## Handoff  (overwrite this block, ≤10 lines, no history)
        last: planning session
        stage: L0 NOT STARTED
        next: L0.1 truth expectations

        ## Checkpoints

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | L0.1 | Truth expectations (6 repos, named flows) | TODO | | |
        | L0.2 | Cold-agent MCP QA harness + baseline | IN PROGRESS | | |
        | L0.3 | UI drive gate (red items enumerated) | DONE | 1e2198a | eval-results/2026-07-07/ui/ |
        | L1.1 | SymbolId/SymbolRef + ambiguity fixtures | BLOCKED | | waiting on decision |
        | L2.4 | **Checkout truth test GREEN (depth ≥5)** | TODO | | |

        ## Quick commands
        """;

    [Fact]
    public void ParsesAllRows()
    {
        var t = TrackerParser.Parse(Sample);
        Assert.Equal(5, t.Checkpoints.Count);
        Assert.Equal(new[] { "L0.1", "L0.2", "L0.3", "L1.1", "L2.4" }, t.Checkpoints.Select(c => c.Id));
    }

    [Fact]
    public void ParsesStatusesIncludingSpacedOnes()
    {
        var t = TrackerParser.Parse(Sample);
        Assert.False(t.ById("L0.1")!.IsDone);
        Assert.True(t.ById("L0.2")!.IsInProgress);
        Assert.True(t.ById("L0.3")!.IsDone);
        Assert.True(t.ById("L1.1")!.IsBlocked);
    }

    [Fact]
    public void CapturesCommitAndEvidence()
    {
        var row = TrackerParser.Parse(Sample).ById("L0.3")!;
        Assert.Equal("1e2198a", row.Commit);
        Assert.Equal("eval-results/2026-07-07/ui/", row.Evidence);
    }

    [Fact]
    public void GroupsByStage()
    {
        var t = TrackerParser.Parse(Sample);
        Assert.Equal(3, t.ForStage("L0").Count());
        Assert.False(t.StageDone("L0"));
        Assert.False(t.AllDone);
        var allDone = TrackerParser.Parse("| L9.1 | x | DONE | a | b |\n");
        Assert.True(allDone.StageDone("L9"));
        Assert.True(allDone.AllDone);
    }

    [Fact]
    public void ExtractsHandoffBlock()
    {
        var t = TrackerParser.Parse(Sample);
        Assert.Contains("stage: L0 NOT STARTED", t.HandoffBlock);
        Assert.DoesNotContain("Checkpoints", t.HandoffBlock);
    }

    [Fact]
    public void RealLoomTrackerParsesIfPresent()
    {
        var path = @"C:\code\DevContext2-ui\LOOM-START.md";
        if (!File.Exists(path)) return; // machine-specific — skip elsewhere
        var t = TrackerParser.ParseFile(path);
        // This is a live, foreign tracker that a separate Loom run mutates; assert the parser
        // invariants (well-formed rows parse, stage grouping works, every id is populated) rather
        // than a magic count coupled to that run's churn. Malformed rows are correctly rejected.
        Assert.True(t.Checkpoints.Count >= 30, $"expected the bulk of rows to parse, got {t.Checkpoints.Count}");
        Assert.Contains("L0", t.Checkpoints.Select(c => c.StageId)); // stage grouping works
        Assert.All(t.Checkpoints, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
    }

    // B1.2 — the parse moved behind IProgressProvider. Prove the default provider is byte-identical
    // to the facade every existing call site still uses, so decoupling changed no behaviour (D-2).
    [Fact]
    public void MarkdownTableProviderIsByteIdenticalToFacade()
    {
        var viaFacade = TrackerParser.Parse(Sample);
        var viaProvider = new MarkdownTableProvider().Read(WritePlanFor(Sample, out var cleanup));
        try
        {
            Assert.Equal("markdown-table", new MarkdownTableProvider().Name);
            Assert.Equal(
                viaFacade.Checkpoints.Select(c => (c.Id, c.Title, c.Status, c.Commit, c.Evidence)),
                viaProvider.Checkpoints.Select(c => (c.Id, c.Title, c.Status, c.Commit, c.Evidence)));
            Assert.Equal(viaFacade.HandoffBlock, viaProvider.HandoffBlock);
            Assert.Equal(viaFacade.RawText, viaProvider.RawText);
        }
        finally { cleanup(); }
    }

    private static PlanConfig WritePlanFor(string trackerText, out Action cleanup)
    {
        var repo = Path.Combine(Path.GetTempPath(), "cbaton-b12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"), trackerText);
        cleanup = () => { try { Directory.Delete(repo, recursive: true); } catch (IOException) { } };
        return new PlanConfig { Repo = repo, Tracker = "TRACKER.md" };
    }
}
