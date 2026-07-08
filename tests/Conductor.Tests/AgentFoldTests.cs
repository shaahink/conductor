using Conductor.Ui;

namespace Conductor.Tests;

public class AgentFoldTests
{
    private static DashboardState.AgentLine L(string kind, string text)
        => new(kind, text, DateTime.UtcNow);

    private static readonly IReadOnlyList<DashboardState.AgentLine> Stream = new[]
    {
        L("tool", "bash git status"),
        L("result", " M SymbolTable.cs"),
        L("result", "?? SymbolRefTests.cs"),
        L("text", "reading the stage section"),
        L("tool", "bash dotnet build"),
        L("result", "build succeeded"),
    };

    [Fact]
    public void FoldedCollapsesToolOutputBehindABadge()
    {
        var rows = AgentFold.Build(Stream, expand: false);
        // 2 tool headers + 1 text line = 3 visible rows when folded.
        Assert.Equal(3, rows.Count);
        var firstTool = rows[0];
        Assert.True(firstTool.IsToolHeader);
        Assert.Equal(2, firstTool.FoldedCount);  // two result lines hidden
        Assert.Equal("text", rows[1].Kind);       // narrative preserved between tools
        Assert.Equal(1, rows[2].FoldedCount);      // second tool hides its single result
    }

    [Fact]
    public void ExpandedRevealsIndentedToolOutput()
    {
        var rows = AgentFold.Build(Stream, expand: true);
        Assert.Equal(Stream.Count, rows.Count);           // every line visible
        Assert.All(rows.Where(r => r.IsToolHeader), r => Assert.Equal(0, r.FoldedCount));
        var output = rows.Where(r => r.Kind == "result").ToList();
        Assert.All(output, r => Assert.True(r.Indent));   // output indented under its tool
    }

    [Fact]
    public void TextOnlyStreamHasNoFolding()
    {
        var rows = AgentFold.Build(new[] { L("text", "a"), L("text", "b") }, expand: false);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsToolHeader));
        Assert.All(rows, r => Assert.Equal(0, r.FoldedCount));
    }

    [Fact]
    public void ToolWithNoTrailingOutputShowsZeroBadge()
    {
        var rows = AgentFold.Build(new[] { L("tool", "read file"), L("text", "done") }, expand: false);
        Assert.True(rows[0].IsToolHeader);
        Assert.Equal(0, rows[0].FoldedCount);
    }

    [Fact]
    public void EmptyStreamProducesNoRows()
    {
        var rows = AgentFold.Build(Array.Empty<DashboardState.AgentLine>(), expand: false);
        Assert.Empty(rows);
    }
}
