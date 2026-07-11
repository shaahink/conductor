using Conductor.Core;

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
    public void GitViewOnNonRepoIsGracefulNotFatal()
    {
        var outp = GitView.Summary(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(outp)); // never throws, always returns something
    }
}
