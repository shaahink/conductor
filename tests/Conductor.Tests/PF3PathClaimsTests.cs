using Conductor.Core.Events;

namespace Conductor.Tests;

/// <summary>PF3: declared task-card paths are real task data — validated/cleaned on write, folded
/// into the graph (null = unchanged, empty = cleared, exactly like P3's context), and unioned per
/// checkpoint over OPEN cards only, which is what <c>ReadyItem.PathClaims</c> carries into the
/// assignment policy.</summary>
public sealed class PF3PathClaimsTests
{
    private static TaskGraph GraphWith(params ConductorEvent[] events)
    {
        var g = new TaskGraph();
        g.Fold(events);
        return g;
    }

    [Fact]
    public void BuildDetailEdit_PathsOnly_IsAValidEdit_AndEntriesAreCleaned()
    {
        var g = GraphWith(new TaskAdded { RunId = "r", Seq = 1, TaskId = "t1", CheckpointId = "C1", Title = "Card", Order = 1, Source = "human" });
        var (evt, error) = TaskWrites.BuildDetailEdit(g, "r", "t1", title: null, context: null,
            paths: [" src/Foo.cs ", "", "  ", "face-go/internal/tui/plan.go"]);
        Assert.Null(error);
        Assert.Equal(new[] { "src/Foo.cs", "face-go/internal/tui/plan.go" }, evt!.Paths);
        Assert.Null(evt.Title);
        Assert.Null(evt.Context);
    }

    [Fact]
    public void BuildDetailEdit_NothingGiven_StillRefused()
    {
        var g = GraphWith(new TaskAdded { RunId = "r", Seq = 1, TaskId = "t1", CheckpointId = "C1", Title = "Card", Order = 1, Source = "human" });
        var (evt, error) = TaskWrites.BuildDetailEdit(g, "r", "t1", null, null, null);
        Assert.Null(evt);
        Assert.Contains("nothing to edit", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Fold_PathsEditUpdates_AbsentLeavesUnchanged_EmptyClears()
    {
        var g = GraphWith(
            new TaskAdded { RunId = "r", Seq = 1, TaskId = "t1", CheckpointId = "C1", Title = "Card", Order = 1, Source = "human" },
            new TaskDetailEdited { RunId = "r", Seq = 2, TaskId = "t1", Paths = ["src/A.cs"] },
            // a pre-PF3 event (no paths on the wire) must leave the declared claims alone
            new TaskDetailEdited { RunId = "r", Seq = 3, TaskId = "t1", Title = "Renamed" });
        Assert.Equal(new[] { "src/A.cs" }, g.Find("t1")!.Paths);
        Assert.Equal("Renamed", g.Find("t1")!.Title);

        g.Fold([new TaskDetailEdited { RunId = "r", Seq = 4, TaskId = "t1", Paths = [] }]);
        Assert.Empty(g.Find("t1")!.Paths);
    }

    [Fact]
    public void DeclaredOpenPaths_UnionsOpenCards_DropsDoneOnes_AndDedupes()
    {
        var g = GraphWith(
            new TaskAdded { RunId = "r", Seq = 1, TaskId = "t1", CheckpointId = "C1", Title = "One", Order = 1, Source = "human" },
            new TaskAdded { RunId = "r", Seq = 2, TaskId = "t2", CheckpointId = "C1", Title = "Two", Order = 2, Source = "human" },
            new TaskAdded { RunId = "r", Seq = 3, TaskId = "t3", CheckpointId = "C1", Title = "Done card", Order = 3, Source = "human" },
            new TaskDetailEdited { RunId = "r", Seq = 4, TaskId = "t1", Paths = ["src/A.cs", "src/B.cs"] },
            new TaskDetailEdited { RunId = "r", Seq = 5, TaskId = "t2", Paths = ["SRC/a.cs", "src/C.cs"] },
            new TaskDetailEdited { RunId = "r", Seq = 6, TaskId = "t3", Paths = ["src/Z.cs"] },
            new TaskStatusChanged { RunId = "r", Seq = 7, TaskId = "t3", Status = "done" });
        // Case-insensitive dedupe keeps the first spelling; the done card's claim is gone.
        Assert.Equal(new[] { "src/A.cs", "src/B.cs", "src/C.cs" }, g.DeclaredOpenPaths("C1"));
    }

    [Fact]
    public void DeclaredOpenPaths_NoDeclarations_IsNull()
    {
        var g = GraphWith(new TaskAdded { RunId = "r", Seq = 1, TaskId = "t1", CheckpointId = "C1", Title = "Card", Order = 1, Source = "human" });
        Assert.Null(g.DeclaredOpenPaths("C1"));
        Assert.Null(g.DeclaredOpenPaths("ghost"));
    }
}
