using Conductor.Ui;
using Spectre.Console;

namespace Conductor.Tests;

public class CommandHistoryTests
{
    private static readonly DateTime T0 = new(2026, 7, 8, 16, 0, 0, DateTimeKind.Utc);
    private static HistoryEntry E(string kind, string text, int sec) => new(kind, text, T0.AddSeconds(sec));

    // A synthetic run feed: two commands, a thought, a narrative line, an error.
    private static readonly IReadOnlyList<HistoryEntry> Feed = new[]
    {
        E("tool", "bash git status --porcelain", 1),
        E("thinking", "Goal: get the compile clean before wiring the resolver.", 2),
        E("tool", "bash dotnet build Conductor.slnx", 3),
        E("text", "Running the test battery next.", 4),
        E("tool", "bash dotnet test Conductor.slnx", 5),
        E("stderr", "error CS0246: type or namespace not found", 6),
    };

    [Fact]
    public void EmptyQueryReturnsEverything()
    {
        var q = CommandHistory.Parse("");
        Assert.False(q.IsActive);
        Assert.Equal(Feed.Count, CommandHistory.Filter(Feed, q).Count);
    }

    [Fact]
    public void CategoryTokenSelectsCommandsOnly()
    {
        var q = CommandHistory.Parse("/commands");
        Assert.Equal(HistoryCategory.Commands, q.Category);
        Assert.Equal("", q.Search);
        var rows = CommandHistory.Filter(Feed, q);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("tool", r.Kind));
    }

    [Fact]
    public void ThoughtsCategoryIncludesReasoningAndNarrative()
    {
        var rows = CommandHistory.Filter(Feed, CommandHistory.Parse("/thoughts"));
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Kind == "thinking");
        Assert.Contains(rows, r => r.Kind == "text");
    }

    [Fact]
    public void ErrorsCategorySelectsStderrOnly()
    {
        var rows = CommandHistory.Filter(Feed, CommandHistory.Parse("/err"));
        Assert.Single(rows);
        Assert.Equal("stderr", rows[0].Kind);
    }

    [Fact]
    public void SlashSearchTokenIsAFreeTextSearchAcrossAll()
    {
        // "/build" is not a category — it becomes the search term "build" (leading slash stripped).
        var q = CommandHistory.Parse("/build");
        Assert.Equal(HistoryCategory.All, q.Category);
        Assert.Equal("build", q.Search);
        var rows = CommandHistory.Filter(Feed, q);
        Assert.Single(rows);
        Assert.Contains("dotnet build", rows[0].Text);
    }

    [Fact]
    public void GitSearchMatchesTheGitCommandCaseInsensitively()
    {
        var rows = CommandHistory.Filter(Feed, CommandHistory.Parse("/GIT"));
        Assert.Single(rows);
        Assert.Contains("git status", rows[0].Text);
    }

    [Fact]
    public void CategoryAndSearchCombine()
    {
        // Only commands containing "test" — excludes the "test battery" narrative text line.
        var q = CommandHistory.Parse("/commands test");
        Assert.Equal(HistoryCategory.Commands, q.Category);
        Assert.Equal("test", q.Search);
        var rows = CommandHistory.Filter(Feed, q);
        Assert.Single(rows);
        Assert.Equal("bash dotnet test Conductor.slnx", rows[0].Text);
    }

    [Fact]
    public void NextCategoryCyclesBackToAll()
    {
        var c = HistoryCategory.All;
        c = CommandHistory.NextCategory(c); Assert.Equal(HistoryCategory.Commands, c);
        c = CommandHistory.NextCategory(c); Assert.Equal(HistoryCategory.Thoughts, c);
        c = CommandHistory.NextCategory(c); Assert.Equal(HistoryCategory.Errors, c);
        c = CommandHistory.NextCategory(c); Assert.Equal(HistoryCategory.All, c);
    }

    [Fact]
    public void FilteredHistoryRendersOnlyMatchesInTheModal()
    {
        // End-to-end display path: filter the feed, then render it through the real command-history
        // modal. Searching "git" must show the git command and drop the build/test commands.
        var rows = CommandHistory.Filter(Feed, CommandHistory.Parse("/commands git"));
        var lines = rows.Select(r => r.Text).ToList();
        var outp = RenderModal("command history · filter commands · /git", lines);

        Assert.Contains("git status", outp);
        Assert.DoesNotContain("dotnet build", outp);
        Assert.DoesNotContain("dotnet test", outp);
    }

    private static string RenderModal(string title, IReadOnlyList<string> lines)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 160;
        console.Profile.Height = 40;
        console.Write(DashboardRenderer.BuildModal(title, lines, offset: 0, width: 160, height: 40));
        return writer.ToString();
    }
}
