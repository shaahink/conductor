using Conductor.Core;
using Conductor.Ui;

namespace Conductor.Tests;

public class PlanTreeTests
{
    private static IReadOnlyList<StageProgress> Sample() => new[]
    {
        new StageProgress
        {
            Id = "B0", Title = "Tooling", Done = 6, Total = 6, State = "confirmed",
            Attempts = 3, LastOutcome = "Advanced", CostUsd = 0.42m,
            Checkpoints = new[] { ("B0.1", "net10 migration", "DONE"), ("B0.2", "analyzers", "DONE") },
        },
        new StageProgress
        {
            Id = "B4", Title = "TUI overhaul", Done = 2, Total = 7, State = "active",
            Attempts = 2, LastOutcome = "Progress", CostUsd = 0.19m,
            Checkpoints = new[]
            {
                ("B4.1", "Alt-screen buffer", "DONE"),
                ("B4.2", "Spectre Layout rebuild", "DONE"),
                ("B4.3", "Hierarchical plan tree", "IN PROGRESS"),
                ("B4.4", "Severity model", "TODO"),
            },
        },
        new StageProgress
        {
            Id = "B5", Title = "Timeline", Done = 0, Total = 4, State = "todo",
            Attempts = 1, LastOutcome = "GatesRed", CostUsd = 0.05m,
            Checkpoints = new[] { ("B5.1", "Timeline view", "BLOCKED"), ("B5.2", "Replay", "TODO") },
        },
    };

    [Fact]
    public void ActiveStageAutoExpands_CollapsedStagesHideCheckpoints()
    {
        var rows = PlanTree.VisibleRows(Sample(), new PlanTreeView());
        // All three stage headers show.
        Assert.Equal(3, rows.Count(r => r.IsStage));
        // Active stage (B4) is auto-expanded → its checkpoints appear.
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B4.3");
        // Collapsed stages (B0, B5) hide their checkpoints by default.
        Assert.DoesNotContain(rows, r => !r.IsStage && r.Id == "B0.1");
        Assert.DoesNotContain(rows, r => !r.IsStage && r.Id == "B5.1");
    }

    [Fact]
    public void ExpandAllRevealsEveryCheckpoint()
    {
        var rows = PlanTree.VisibleRows(Sample(), new PlanTreeView { ExpandAll = true });
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B0.1");
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B5.2");
    }

    [Fact]
    public void TodoFilterDropsFullyDoneStages()
    {
        var rows = PlanTree.VisibleRows(Sample(), new PlanTreeView { Filter = PlanFilter.Todo });
        // B0 is fully DONE → dropped; B4 and B5 have not-done work → kept.
        Assert.DoesNotContain(rows, r => r.IsStage && r.Id == "B0");
        Assert.Contains(rows, r => r.IsStage && r.Id == "B4");
        Assert.Contains(rows, r => r.IsStage && r.Id == "B5");
        // Under Todo, a narrowing filter shows only the not-done checkpoints of shown stages.
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B4.4");
        Assert.DoesNotContain(rows, r => !r.IsStage && r.Id == "B4.1"); // DONE, filtered out
    }

    [Fact]
    public void ActiveFilterKeepsOnlyTheActiveStage()
    {
        var rows = PlanTree.VisibleRows(Sample(), new PlanTreeView { Filter = PlanFilter.Active });
        var stages = rows.Where(r => r.IsStage).Select(r => r.Id).ToList();
        Assert.Equal(new[] { "B4" }, stages);
    }

    [Fact]
    public void FailedFilterSurfacesBlockedAndFailedOutcomes()
    {
        var rows = PlanTree.VisibleRows(Sample(), new PlanTreeView { Filter = PlanFilter.Failed });
        // B5 has a BLOCKED checkpoint and a GatesRed last outcome → shown.
        Assert.Contains(rows, r => r.IsStage && r.Id == "B5");
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B5.1"); // the BLOCKED row
        // B0 (all advanced/done) is not failed → dropped.
        Assert.DoesNotContain(rows, r => r.IsStage && r.Id == "B0");
    }

    [Fact]
    public void SearchMatchesCheckpointIdOrTitle_AcrossStages()
    {
        var rows = PlanTree.VisibleRows(Sample(), new PlanTreeView { Search = "replay" });
        // Only B5 (whose B5.2 title contains "replay") survives; the matching checkpoint is shown.
        Assert.Equal(new[] { "B5" }, rows.Where(r => r.IsStage).Select(r => r.Id).ToList());
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B5.2");
        Assert.DoesNotContain(rows, r => !r.IsStage && r.Id == "B5.1");
    }

    [Fact]
    public void SearchMatchingStageNameShowsItsCheckpoints()
    {
        // "Tooling" matches only stage B0's name (neither checkpoint title contains it) → show all
        // of B0's checkpoints for context.
        var rows = PlanTree.VisibleRows(Sample(), new PlanTreeView { Search = "Tooling" });
        Assert.Equal(new[] { "B0" }, rows.Where(r => r.IsStage).Select(r => r.Id).ToList());
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B0.1");
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B0.2");
    }

    [Fact]
    public void ExplicitExpandRevealsACollapsedStage()
    {
        var view = new PlanTreeView { Expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" } };
        var rows = PlanTree.VisibleRows(Sample(), view);
        Assert.Contains(rows, r => !r.IsStage && r.Id == "B0.1");
    }

    [Fact]
    public void FilterCyclesAllTodoActiveFailedAndBack()
    {
        var f = PlanFilter.All;
        Assert.Equal(PlanFilter.Todo, f = PlanTree.NextFilter(f));
        Assert.Equal(PlanFilter.Active, f = PlanTree.NextFilter(f));
        Assert.Equal(PlanFilter.Failed, f = PlanTree.NextFilter(f));
        Assert.Equal(PlanFilter.All, PlanTree.NextFilter(f));
    }

    [Fact]
    public void BuildRendersStageColumnsAndCollapseGlyph()
    {
        var text = Render(PlanTree.Build(Sample(), new PlanTreeView()));
        Assert.Contains("B4", text);
        Assert.Contains("2/7", text);        // per-stage done column
        Assert.Contains("← active", text);   // active badge
        Assert.Contains("$", text);          // cost column
        Assert.Contains("▸", text);          // collapsed glyph for B0/B5
        Assert.Contains("▾", text);          // expanded glyph for active B4
    }

    private static string Render(Spectre.Console.Rendering.IRenderable r)
    {
        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 80;
        console.Profile.Height = 40;
        console.Write(r);
        return writer.ToString();
    }
}
