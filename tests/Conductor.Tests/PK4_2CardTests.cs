using Conductor.Core.Events;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Tests;

/// <summary>PK4.2 / D7 — the checkpoint card: the words stored at the claim, and refused there when they
/// cannot be a card.</summary>
public sealed class PK4_2CardTests
{
    private const string RunId = "pk42-run";

    // -- --tell at the claim ---------------------------------------------------------------------

    [Fact]
    public void ADoneClaimCarriesTheWordsIntoTheGraph()
    {
        var graph = Graph("PK9.1");
        var (evt, error) = TaskWrites.BuildStatusChange(graph, RunId, "PK9.1", "done", "agent", "abc1234",
            "ev.md", "  Figures have a folder | The screenshots stopped living in the repo root. Each one is filed by stage.  ");
        Assert.Null(error);
        graph.Fold([evt!]);

        var card = graph.Find("PK9.1")!;
        Assert.Equal("Figures have a folder | The screenshots stopped living in the repo root. Each one is filed by stage.", card.Tell);

        // A later claim without words keeps them, the way commit and evidence are kept.
        var (again, _) = TaskWrites.BuildStatusChange(graph, RunId, "PK9.1", "done", "agent", evidence: "ev2.md");
        graph.Fold([again!]);
        Assert.Equal("ev2.md", graph.Find("PK9.1")!.Evidence);
        Assert.StartsWith("Figures have a folder |", graph.Find("PK9.1")!.Tell, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("in_progress", "A title | Two sentences. Here.", "rides a done-claim only")]
    [InlineData("done", "no separator at all", "needs \"<title> | <two to four sentences>\"")]
    [InlineData("done", " | only sentences here.", "needs")]
    [InlineData("done", "only a title |   ", "needs")]
    public void WordsThatCannotBeACardAreRefusedAtTheClaim(string status, string tell, string why)
    {
        var graph = Graph("PK9.1");
        var (evt, error) = TaskWrites.BuildStatusChange(graph, RunId, "PK9.1", status, "agent", tell: tell);
        Assert.Null(evt);
        Assert.Contains(why, error, StringComparison.Ordinal);
    }

    /// <summary>The budget as a property: whatever the lengths, words within both ceilings are accepted
    /// and words over either are refused by name.</summary>
    [Fact]
    public void TheWordsBudgetHoldsAtEveryLength()
    {
        foreach (var titleLength in new[] { 1, CardWords.MaxTitle - 1, CardWords.MaxTitle, CardWords.MaxTitle + 1, 400 })
        {
            foreach (var lineLength in new[] { 1, CardWords.MaxLine, CardWords.MaxLine + 1, 2000 })
            {
                var words = new string('t', titleLength) + " | " + new string('s', lineLength);
                var refusal = CardWords.Refusal(words);
                var fits = titleLength <= CardWords.MaxTitle && lineLength <= CardWords.MaxLine;
                Assert.True(fits == (refusal is null), $"title {titleLength}, line {lineLength}: {refusal}");
                if (!fits) Assert.Contains(titleLength > CardWords.MaxTitle ? "title" : "sentences", refusal, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void OnlyTheFirstBarSeparatesTheTitle()
    {
        var parsed = CardWords.Parse("Pipes | a | b stays in the line.");
        Assert.Equal(("Pipes", "a | b stays in the line."), parsed);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static TaskGraph Graph(params string[] checkpoints)
    {
        var graph = new TaskGraph();
        graph.Fold(checkpoints.Select((id, i) => (ConductorEvent)new TaskAdded
        {
            RunId = RunId, TaskId = id, CheckpointId = id, Title = id, Source = "tracker", Order = i, Kind = "checkpoint",
        }));
        return graph;
    }
}
