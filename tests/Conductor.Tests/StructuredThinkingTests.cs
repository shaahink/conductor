using Conductor.Ui;

namespace Conductor.Tests;

public class StructuredThinkingTests
{
    [Fact]
    public void ParsesAllFourFacetsFromInlineReasoning()
    {
        var t = StructuredThinking.Parse(
            "Goal: implement SymbolRef tiers. Hypothesis: exact-then-fuzzy is safe. " +
            "Evidence: SymbolTable exposes a seam. Action: add ambiguity fixtures then run the gate.");
        Assert.True(t.HasStructure);
        Assert.Equal("implement SymbolRef tiers", t.Goal);
        Assert.Equal("exact-then-fuzzy is safe", t.Hypothesis);
        Assert.Equal("SymbolTable exposes a seam", t.Evidence);
        Assert.Equal("add ambiguity fixtures then run the gate", t.Action);
    }

    [Fact]
    public void UnstructuredProseKeepsRawAndReportsNoStructure()
    {
        var t = StructuredThinking.Parse("The dogfood repo has duplicate short names across services.");
        Assert.False(t.HasStructure);
        Assert.Equal("The dogfood repo has duplicate short names across services.", t.Raw);
        Assert.Null(t.Goal);
        Assert.Null(t.Action);
    }

    [Fact]
    public void ParsesPartialFacetsAndIsCaseInsensitive()
    {
        var t = StructuredThinking.Parse("goal - close the audit gap. ACTION: add negative fixtures.");
        Assert.True(t.HasStructure);
        Assert.Equal("close the audit gap", t.Goal);
        Assert.Equal("add negative fixtures", t.Action);
        Assert.Null(t.Hypothesis);
        Assert.Null(t.Evidence);
    }

    [Fact]
    public void CollapsesNewlinesAndWhitespaceRuns()
    {
        var t = StructuredThinking.Parse("Goal:\n   ship it\r\n\r\nAction:   test it");
        Assert.Equal("ship it", t.Goal);
        Assert.Equal("test it", t.Action);
    }

    [Fact]
    public void EmptyInputIsNotStructured()
    {
        var t = StructuredThinking.Parse("   ");
        Assert.False(t.HasStructure);
        Assert.Equal("", t.Raw);
    }

    [Fact]
    public void WordContainingFacetKeywordIsNotMistakenForAMarker()
    {
        // "goalkeeper" must not trigger a Goal facet — the marker needs a ':' or '-' delimiter.
        var t = StructuredThinking.Parse("The goalkeeper strategy has no evidence markers here");
        Assert.False(t.HasStructure);
    }
}
