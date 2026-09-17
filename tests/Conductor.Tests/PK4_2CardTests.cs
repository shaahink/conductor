using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

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

    // -- the numbers a card is composed from ------------------------------------------------------

    [Fact]
    public void TheDefaultLiveRuleIsTheLastCheckpointOfAStageAndCountsOnlyConfirmed()
    {
        // A: both confirmed -> live. B: B.1 confirmed, B.2 claimed but not confirmed -> not live.
        // C.1 skipped -> not counted at all.
        var graph = Graph("A.1", "A.2", "B.1", "B.2", "C.1");
        Claim(graph, "A.1", "A.2", "B.1", "B.2");
        Confirm(graph, "A.1", "A.2", "B.1");
        graph.Fold([new TaskStatusChanged { RunId = RunId, TaskId = "C.1", Status = "skipped" }]);
        var plan = Plan("A", "B", "C");

        var forB = CardFacts.Count(plan, graph, "B");
        Assert.Equal(new CardCounts(Done: 3, Total: 4, Live: 2, StageLive: false), forB);
        Assert.True(CardFacts.Count(plan, graph, "A").StageLive);

        // A stage that deploys at B.1 is live the moment B.1 is confirmed, whatever comes after it.
        plan.Stages[1].Deploys = "B.1";
        Assert.Equal(new CardCounts(3, 4, 3, true), CardFacts.Count(plan, graph, "B"));
    }

    [Fact]
    public void TheBarIsTenCellsAndNeverLies()
    {
        for (var total = 1; total <= 40; total++)
        {
            var previous = -1;
            for (var done = 0; done <= total; done++)
            {
                var bar = CardFacts.Bar(done, total);
                Assert.Equal(10, bar.Length);
                var filled = bar.Count(ch => ch == '█');
                Assert.True(filled >= previous, $"{done}/{total} filled {filled} after {previous}");
                previous = filled;
                if (done == 0) Assert.Equal(0, filled);
                if (done == total) Assert.Equal(10, filled);
            }
        }
        Assert.Equal("", CardFacts.Bar(0, 0));
    }

    [Fact]
    public void ARoomStringHasItsFourNamesFilledAndNothingElseTouched()
    {
        var filled = CardFacts.Fill("{done}/{total} fixed · {live} live · {stage} · {other}", new CardCounts(5, 9, 2, false), "T2");
        Assert.Equal("5/9 fixed · 2 live · T2 · {other}", filled);
    }

    [Fact]
    public void ThePairIsTheNewestBeforeAndAfterThatResolveOrNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"conductor-pk42-pair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string File(string name) { var path = Path.Combine(root, name); System.IO.File.WriteAllText(path, name); return path; }
            string? Resolve(string path) => System.IO.File.Exists(path) ? path : null;
            var t0 = DateTimeOffset.Parse("2026-09-17T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

            var registry = new EvidenceRegistry();
            registry.Add(Artifact(File("S3.1-before.png"), EvidenceKinds.Image, "S3.1", t0));
            Assert.Null(CardFacts.Pair(registry, "S3.1", Resolve));   // half a comparison is not one

            registry.Add(Artifact(File("S3.1-after.png"), EvidenceKinds.Image, "S3.1", t0.AddMinutes(1)));
            registry.Add(Artifact(File("S3.1-after-old.txt"), EvidenceKinds.Text, "S3.1", t0.AddMinutes(2)));
            registry.Add(Artifact(File("S3.2-before.png"), EvidenceKinds.Image, "S3.2", t0.AddMinutes(3)));
            registry.Add(Artifact(File("S3.1-retake-before.png"), EvidenceKinds.Image, "S3.1", t0.AddMinutes(4)));

            var pair = CardFacts.Pair(registry, "S3.1", Resolve);
            Assert.NotNull(pair);
            Assert.Equal([Path.Combine(root, "S3.1-retake-before.png"), Path.Combine(root, "S3.1-after.png")], pair);

            registry.Add(Artifact(Path.Combine(root, "S3.1-gone-after.png"), EvidenceKinds.Image, "S3.1", t0.AddMinutes(5)));
            Assert.Equal(Path.Combine(root, "S3.1-after.png"), CardFacts.Pair(registry, "S3.1", Resolve)![1]);
        }
        finally
        {
            TestTemp.DeleteTree(root);
        }
    }

    [Fact]
    public void AStageCardListsItsCommitSubjectsEscapedAndClipped()
    {
        var subjects = Enumerable.Range(1, CardFacts.MaxChanges + 3).Select(i => $"feat: step {i} <b>").ToList();
        var changes = CardFacts.Changes(subjects);
        var lines = changes.Split('\n');
        Assert.Equal(CardFacts.MaxChanges + 1, lines.Length);
        Assert.Equal("• feat: step 1 &lt;b&gt;", lines[0]);
        Assert.Equal("… and 3 more", lines[^1]);
    }

    // -- the held words --------------------------------------------------------------------------

    [Fact]
    public void TheNextPromptCarriesTheWordsOfEveryClaimNotYetConfirmed()
    {
        var graph = Graph("A.1", "A.2", "A.3");
        foreach (var (id, words) in new[] { ("A.1", "Confirmed one | It is out."), ("A.2", "Held one | The gates went red. Nothing was posted.") })
        {
            var (evt, _) = TaskWrites.BuildStatusChange(graph, RunId, id, "done", "agent", tell: words);
            graph.Fold([evt!]);
        }
        Claim(graph, "A.3");   // claimed without words: nothing to hold
        Confirm(graph, "A.1");

        var battery = new HeldWordsBattery(graph.Checkpoints());
        Assert.False(battery.IsEmpty);
        Assert.Contains("A.2: Held one | The gates went red. Nothing was posted.", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("A.1", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("A.3", battery.Section, StringComparison.Ordinal);

        Confirm(graph, "A.2");
        Assert.True(new HeldWordsBattery(graph.Checkpoints()).IsEmpty);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static void Claim(TaskGraph graph, params string[] ids) =>
        graph.Fold(ids.Select(id => (ConductorEvent)new TaskStatusChanged { RunId = RunId, TaskId = id, Status = "done", Source = "agent" }));

    private static void Confirm(TaskGraph graph, params string[] ids) =>
        graph.Fold(ids.Select(id => (ConductorEvent)new CheckpointConfirmed { RunId = RunId, CheckpointId = id, StageId = id.Split('.')[0] }));

    private static PlanConfig Plan(params string[] stages)
    {
        var plan = new PlanConfig { Name = "pk42" };
        foreach (var id in stages) plan.Stages.Add(new StageConfig { Id = id, Title = "Stage " + id, Sessions = 1 });
        return plan;
    }

    private static EvidenceArtifact Artifact(string path, string kind, string checkpoint, DateTimeOffset at) =>
        new(path, kind, checkpoint, checkpoint.Split('.')[0], 1, Guid.NewGuid().ToString("N"), 10, at, "test");

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
