using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

// B1.4 — configurable progress conventions (R1.3). The load-bearing value: a Shamshir-shaped strict
// tracker whose ids are irregular (P-0, P3.4b, F5) parses into the right stages once the plan sets
// stageIdPattern, while Loom's default (the shape the parser hard-coded before B1.4) cannot even read
// a hyphenated id — exactly the F-1 gap this closes. Handoff marker, HUMAN token and the status
// vocabulary are likewise per-plan config with Loom values as defaults.
public sealed class ProgressConventionsTests
{
    private const string ShamshirTracker = """
        # Shamshir — parity-pipeline TRACKER

        ## Handoff
        last: bootstrap done
        HUMAN: confirm the fixture repo path before P0.1

        ## Checkpoints

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | P-0   | Bootstrap the parity harness | DONE | abc1234 | evidence/p-0.txt |
        | P0.1  | Wire the fixture loader      | TODO | | |
        | P1    | First parser pass            | IN PROGRESS | | |
        | P3.4b | Flaky-retry edge case        | BLOCKED | | waiting on upstream |
        | F5    | Follow-up sweep              | TODO | | |
        """;

    // Admits a hyphen after the stage letters (P-0) and treats the part before the first dot as the
    // stage — so P-0 → P-0, P0.1 → P0, P3.4b → P3, F5 → F5.
    private static ProgressConventions Shamshir() => new()
    {
        StageIdPattern = @"(?<stage>[A-Za-z]+-?\d+)(?:\.\d+)?[a-z]?",
    };

    [Fact]
    public void IrregularIds_ParseIntoRightStages()
    {
        var snap = MarkdownTableProvider.Parse(ShamshirTracker, Shamshir());

        Assert.Equal(new[] { "P-0", "P0.1", "P1", "P3.4b", "F5" }, snap.Checkpoints.Select(c => c.Id));
        Assert.Equal("P-0", snap.ById("P-0")!.StageId);
        Assert.Equal("P0", snap.ById("P0.1")!.StageId);
        Assert.Equal("P1", snap.ById("P1")!.StageId);
        Assert.Equal("P3", snap.ById("P3.4b")!.StageId);
        Assert.Equal("F5", snap.ById("F5")!.StageId);

        Assert.True(snap.ById("P-0")!.IsDone);
        Assert.Equal("abc1234", snap.ById("P-0")!.Commit);
        Assert.True(snap.ById("P1")!.IsInProgress);
        Assert.True(snap.ById("P3.4b")!.IsBlocked);
        Assert.Single(snap.ForStage("P0"));   // only P0.1 — P-0 is its own stage, not part of P0
        Assert.Single(snap.ForStage("P-0"));
    }

    [Fact]
    public void DefaultConventions_CannotParseHyphenatedId_ProvingConfigIsLoadBearing()
    {
        // The original hard-coded row regex (now the default) reads P0.1/P1/P3.4b/F5 but not P-0.
        var snap = MarkdownTableProvider.Parse(ShamshirTracker);
        Assert.DoesNotContain("P-0", snap.Checkpoints.Select(c => c.Id));
        Assert.Contains("P0.1", snap.Checkpoints.Select(c => c.Id));
    }

    [Fact]
    public void PlanConfigConventions_FlowThroughProviderRead()
    {
        var repo = Path.Combine(Path.GetTempPath(), "cbaton-b14-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            File.WriteAllText(Path.Combine(repo, "TRACKER.md"), ShamshirTracker);
            var plan = new PlanConfig { Repo = repo, Tracker = "TRACKER.md", Conventions = Shamshir() };
            var snap = new MarkdownTableProvider().Read(plan);
            Assert.Equal(5, snap.Checkpoints.Count);
            Assert.Equal("P3", snap.ById("P3.4b")!.StageId);
        }
        finally { try { Directory.Delete(repo, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void HandoffAndHumanToken_AreHonoured()
    {
        var conv = Shamshir();
        var snap = MarkdownTableProvider.Parse(ShamshirTracker, conv);
        Assert.Contains("bootstrap done", snap.HandoffBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Checkpoints", snap.HandoffBlock, StringComparison.Ordinal);
        Assert.True(conv.MentionsHuman(snap.HandoffBlock));
        Assert.False(new ProgressConventions { HumanToken = "ESCALATE:" }.MentionsHuman(snap.HandoffBlock));
    }

    [Fact]
    public void CustomHandoffMarker_ExtractsAlternateBlock()
    {
        const string tracker = """
            ## Resume
            pick up at P0.1

            ## Notes
            ignored
            """;
        var snap = MarkdownTableProvider.Parse(tracker, new ProgressConventions { HandoffMarker = "## Resume" });
        Assert.Contains("pick up at P0.1", snap.HandoffBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", snap.HandoffBlock, StringComparison.Ordinal);
        Assert.Empty(MarkdownTableProvider.Parse(tracker).HandoffBlock);   // default marker finds nothing
    }

    [Fact]
    public void Conventions_BindFromPlanJson_CamelCase()
    {
        const string json = """
            {
              "conventions": {
                "stageIdPattern": "(?<stage>[A-Za-z]+-?\\d+)(?:\\.\\d+)?[a-z]?",
                "handoffMarker": "## Resume",
                "humanToken": "ESCALATE:",
                "status": { "done": ["SHIPPED"], "inProgress": ["WIP"] }
              }
            }
            """;
        var plan = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal("## Resume", plan.Conventions.HandoffMarker);
        Assert.Equal("ESCALATE:", plan.Conventions.HumanToken);
        Assert.Equal("P-0", plan.Conventions.DeriveStageId("P-0"));
        Assert.True(plan.Conventions.IsDone("SHIPPED"));
        Assert.True(plan.Conventions.IsInProgress("WIP"));
        Assert.True(plan.Conventions.IsBlocked("BLOCKED"));   // groups absent from JSON keep Loom defaults
    }

    [Fact]
    public void DefaultConventions_AreByteIdenticalToLoomHardcoding()
    {
        var conv = ProgressConventions.Default;
        Assert.Equal("L0", conv.DeriveStageId("L0.1"));
        Assert.Equal("B1", conv.DeriveStageId("B1.4"));
        Assert.True(conv.IsDone("DONE ✅"));
        Assert.True(conv.IsInProgress("IN PROGRESS"));
        Assert.True(conv.IsBlocked("BLOCKED"));
        Assert.True(conv.MentionsHuman("stuck — HUMAN: decide the schema"));
    }
}
