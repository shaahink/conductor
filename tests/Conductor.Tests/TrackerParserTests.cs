using Conductor.Core;

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
        Assert.Equal(35, t.Checkpoints.Count); // L0.1–L8.1 (3+5+4+3+4+5+6+4+1)
        Assert.All(t.Checkpoints, c => Assert.False(c.IsDone)); // fresh plan
        Assert.Contains("L0", t.Checkpoints.Select(c => c.StageId));
    }
}
