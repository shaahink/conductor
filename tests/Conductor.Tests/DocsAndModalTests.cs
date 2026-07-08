using Conductor.Core;
using Conductor.Ui;
using Spectre.Console;

namespace Conductor.Tests;

public class DocsAndModalTests
{
    private const string Doc = """
    # Loom proposal

    ## L4 — Flows + projections
    L4 builds the flow store.
    - L4.1 Flow store
    - L4.2 Projections

    ## L5 — MCP v2: cold-agent ergonomics
    L5 makes the cold-agent QA the gate.
    Gate: cold-agent QA >= 90%.

    ### L5.5 detail
    nested detail stays in L5.

    ## L6 — Workbench repair
    L6 is UI.
    """;

    [Fact]
    public void DocsExtractorReturnsOnlyTheStageSection()
    {
        var l5 = DocsExtractor.ForStage(Doc, "L5");
        Assert.Contains("MCP v2", l5);
        Assert.Contains("cold-agent QA the gate", l5);
        Assert.Contains("nested detail stays in L5", l5); // deeper heading kept
        Assert.DoesNotContain("Workbench repair", l5);    // stops at next same-level heading
        Assert.DoesNotContain("Flow store", l5);          // doesn't bleed the previous section
    }

    [Fact]
    public void DocsExtractorMissingStageReturnsEmpty()
        => Assert.Equal("", DocsExtractor.ForStage(Doc, "L9"));

    [Fact]
    public void DocOnSelectResolvesSelectedRowToItsStageSection()
    {
        // B4.7 doc-on-select: selecting a sub-checkpoint row resolves to its owning stage, whose doc
        // section is the one shown — never a neighbouring stage's. This is the resolution the `D` key
        // performs (PlanTree.StageForRow → DocsExtractor.ForStage), verified end to end.
        var stages = new[]
        {
            new StageProgress { Id = "L4", Title = "Flows", Checkpoints = new[] { ("L4.1", "Flow store", "DONE") } },
            new StageProgress { Id = "L5", Title = "MCP", Checkpoints = new[] { ("L5.5", "detail", "TODO") } },
        };
        var stage = PlanTree.StageForRow(stages, "L5.5");
        Assert.Equal("L5", stage);
        var section = DocsExtractor.ForStage(Doc, stage);
        Assert.Contains("MCP v2", section);          // the selected row's stage section
        Assert.DoesNotContain("Flow store", section); // not the neighbouring L4 section
    }

    [Fact]
    public void ModalRendersWithScrollPositionAndDoesNotThrow()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}").ToList();
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 120;
        console.Profile.Height = 30;
        console.Write(DashboardRenderer.BuildModal("thinking", lines, offset: 50, width: 120, height: 30));
        var outp = writer.ToString();
        Assert.Contains("thinking", outp);
        Assert.Contains("line 51", outp);       // window starts at the offset
        Assert.Contains("/ 100", outp);          // position indicator
        Assert.DoesNotContain("line 1 ", outp);  // scrolled past the top
    }

    [Fact]
    public void GitViewOnNonRepoIsGracefulNotFatal()
    {
        var outp = GitView.Summary(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(outp)); // never throws, always returns something
    }
}
