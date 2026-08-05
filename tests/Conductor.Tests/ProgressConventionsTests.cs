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
        finally { try { TestTemp.DeleteTree(repo); } catch (IOException) { } }
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

    // Audit regression guard: the row regex captures the status keyword with its ORIGINAL inner
    // whitespace (`IN\s+PROGRESS`), so a hand-edited cell with a double space / tab still reaches
    // classification verbatim. The old hard-coded parser used StartsWith("IN") and caught it; the new
    // vocabulary keyword is the literal "IN PROGRESS", so matching must be whitespace-tolerant or the
    // active checkpoint silently reads as not-in-progress (the exact silent-corruption class the stage
    // trap warns about).
    [Fact]
    public void InProgress_WithIrregularInnerWhitespace_StillClassifies()
    {
        var conv = ProgressConventions.Default;
        Assert.True(conv.IsInProgress("IN  PROGRESS"));      // double space
        Assert.True(conv.IsInProgress("IN\tPROGRESS"));      // tab
        Assert.True(conv.IsInProgress("IN  PROGRESS 🚧"));   // + trailing decoration

        // And through a full parse of a row whose status cell has a double space.
        const string tracker = """
            | L2.4 | Checkout truth test | IN  PROGRESS | | |
            """;
        Assert.True(MarkdownTableProvider.Parse(tracker).ById("L2.4")!.IsInProgress);
    }

    /// <summary>B1.7 — prove the Shamshir parity-pipeline TRACKER.md template parses with the
    /// shamshir conventions (irregular ids P-0, P0.1, P3.4b). Stage-id derivation yields the
    /// owning phase prefix for each checkpoint.</summary>
    [Fact]
    public void ShamshirParityPipelineTrackerTemplate_ParsesCorrectly()
    {
        // Locate the template copy relative to the repo.
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "examples", "shamshir", "parity-pipeline.TRACKER.md");
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        if (path == null) return; // not in a full checkout — soft skip

        var text = File.ReadAllText(path);
        var conv = new ProgressConventions
        {
            StageIdPattern = @"(?<stage>[A-Za-z]+-?\d+)(?:\.\d+)?[a-z]?",
        };
        var snap = MarkdownTableProvider.Parse(text, conv);

        // All checkpoint ids from the template
        var ids = snap.Checkpoints.Select(c => c.Id).ToList();
        Assert.Contains("P-0", ids);
        Assert.Contains("P0.1", ids);
        Assert.Contains("P0.5", ids);
        Assert.Contains("P3.4", ids);
        Assert.Contains("P6.1", ids);
        Assert.Equal(17, ids.Count); // the template has 17 checkpoint rows

        // Stage-id derivation: P-0 stays P-0; dotted ids strip the sub-index
        Assert.Equal("P-0", snap.ById("P-0")!.StageId);
        Assert.Equal("P0", snap.ById("P0.1")!.StageId);
        Assert.Equal("P3", snap.ById("P3.4")!.StageId);
        Assert.Equal("P6", snap.ById("P6.1")!.StageId);

        // Per-stage grouping reflects the irregular ids
        Assert.Single(snap.ForStage("P-0"));  // only P-0
        Assert.Equal(5, snap.ForStage("P0").Count());   // P0.1..P0.5
        Assert.Equal(2, snap.ForStage("P1").Count());   // P1.1, P1.2

        // Handoff block extracted
        Assert.Contains("(none)", snap.HandoffBlock, StringComparison.Ordinal);
        Assert.Contains("P-0 NOT STARTED", snap.HandoffBlock, StringComparison.Ordinal);
    }
}
