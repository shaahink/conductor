using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>M6.1/M6.2: the deterministic markdown → task-graph parser and the re-import diff. The
/// headline test is the design-doc truth gate: importing <c>docs/MAESTRO-PLAN.md</c> yields exactly
/// stages M1…M9, with no model call.</summary>
public sealed class MarkdownPlanImportTests
{
    private const string Sample = """
        # A plan

        ### M1 — Deconstruction — break the god classes
        - **M1.1** Delete the old UI.
        - **M1.2** Split the commands.

        ### M2 — One truth: the database
        - **M2.1** Schema defined once.
        - **M2.2** IRunStore and SqliteRunStore.
        - **M2.3** run.db authoritative.

        ### M3 — Workflows that bend
        - **M3.1** Declarative workflow steps.
        """;

    [Fact]
    public void Parse_ExtractsStagesTitlesAndCheckpoints()
    {
        var parsed = MarkdownPlanParser.Parse(Sample);

        Assert.Equal(["M1", "M2", "M3"], parsed.Stages.Select(s => s.Id).ToArray());
        Assert.Equal("Deconstruction", parsed.Stages[0].Title);
        Assert.Equal("break the god classes", parsed.Stages[0].Notes);
        Assert.Equal(2, parsed.Stages[0].Checkpoints.Count);
        Assert.Equal(3, parsed.Stages[1].Checkpoints.Count);
        Assert.Equal("M2.1", parsed.Stages[1].Checkpoints[0].Id);
        Assert.Equal("Schema defined once.", parsed.Stages[1].Checkpoints[0].Title);
    }

    [Fact]
    public void Parse_SkipsDoneBootstrapHeaders()
    {
        const string withBootstrap = """
            ### M0 — Bootstrap (DONE, by hand, before this plan)
            - **M0.1** Something already finished.

            ### M1 — Real work
            - **M1.1** Do the thing.
            """;
        var parsed = MarkdownPlanParser.Parse(withBootstrap);
        Assert.Equal(["M1"], parsed.Stages.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Parse_ReadsTrackerTableRows()
    {
        const string tracker = """
            ### M1 — Deconstruction — delete the old face
            | # | Checkpoint | Status | Commit | Evidence |
            |---|-----------|--------|--------|----------|
            | M1.1 | Delete Ui | DONE | abc123 | - |
            | M1.2 | Split Commands | TODO | - | - |
            """;
        var parsed = MarkdownPlanParser.Parse(tracker);
        Assert.Single(parsed.Stages);
        Assert.Equal(2, parsed.Stages[0].Checkpoints.Count);
        Assert.Equal("DONE", parsed.Stages[0].Checkpoints[0].Status);
        Assert.Equal("Split Commands", parsed.Stages[0].Checkpoints[1].Title);
    }

    [Fact]
    public void LooksStructured_TrueForPlanFalseForProse()
    {
        Assert.True(MarkdownPlanParser.LooksStructured(Sample));
        Assert.False(MarkdownPlanParser.LooksStructured("Please build me a REST API with auth and endpoints."));
    }

    [Fact]
    public void ToImportResult_WiresLinearDependencies()
    {
        var result = MarkdownPlanParser.ToImportResult(MarkdownPlanParser.Parse(Sample));
        Assert.Null(result.Stages[0].DependsOn);
        Assert.Equal(["M1"], result.Stages[1].DependsOn!);
        Assert.Equal(["M2"], result.Stages[2].DependsOn!);
        Assert.Equal(3, result.Stages[1].Sessions); // M2 has 3 checkpoints
    }

    [Fact]
    public void Diff_ReportsAddedAndChangedStagesOnly()
    {
        var plan = new PlanConfig
        {
            Stages = [new StageConfig { Id = "M1", Title = "Old title", Sessions = 2, Kind = "deliver" }],
        };
        var incoming = new ImportResult
        {
            Stages = [
                new StageConfig { Id = "M1", Title = "Deconstruction", Sessions = 2, Kind = "deliver" },
                new StageConfig { Id = "M2", Title = "Database", Sessions = 3, Kind = "deliver" },
            ],
        };

        var diff = PlanDiff.Compute(plan, incoming);

        Assert.Single(diff.AddedStages);
        Assert.Equal("M2", diff.AddedStages[0].Id);
        Assert.Single(diff.ChangedStages);
        Assert.Equal("M1", diff.ChangedStages[0].Id);
        Assert.Contains(diff.ChangedStages[0].Fields, f => f.Field == "title" && f.New == "Deconstruction");
    }

    [Fact]
    public void Diff_ApplyAddsAndUpdatesWithoutClobbering()
    {
        var (planPath, repo) = WriteScratchPlan(
            new StageConfig { Id = "M1", Title = "Old", Sessions = 2, Kind = "deliver", Notes = "hand-tuned note" });
        try
        {
            var plan = PlanConfig.Load(planPath);
            var incoming = new ImportResult
            {
                Stages = [
                    new StageConfig { Id = "M1", Title = "Deconstruction", Sessions = 2, Kind = "deliver" },
                    new StageConfig { Id = "M2", Title = "Database", Sessions = 3, Kind = "deliver", DependsOn = ["M1"] },
                ],
            };
            var diff = PlanDiff.Compute(plan, incoming);
            diff.Apply(plan);

            var reloaded = PlanConfig.Load(planPath);
            Assert.Equal(2, reloaded.Stages.Count);
            Assert.Equal("Deconstruction", reloaded.Stages[0].Title);
            Assert.Equal("hand-tuned note", reloaded.Stages[0].Notes); // untouched — import didn't carry notes
            Assert.Equal("M2", reloaded.Stages[1].Id);
        }
        finally
        {
            try { File.Delete(planPath); } catch { /* best effort */ }
            try { Directory.Delete(repo, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Diff_EmptyWhenPlanAlreadyMatches()
    {
        var plan = new PlanConfig
        {
            Stages = [new StageConfig { Id = "M1", Title = "Deconstruction", Sessions = 2, Kind = "deliver" }],
        };
        var incoming = new ImportResult
        {
            Stages = [new StageConfig { Id = "M1", Title = "Deconstruction", Sessions = 2, Kind = "deliver" }],
        };
        Assert.True(PlanDiff.Compute(plan, incoming).IsEmpty);
    }

    /// <summary>M6 truth gate: import THIS project's design doc → a graph whose stage ids are M1…M9.</summary>
    [Fact]
    public void TruthGate_ImportMaestroDesignDoc_YieldsM1ThroughM9()
    {
        var docPath = Path.Combine(RepoRoot(), "docs", "MAESTRO-PLAN.md");
        Assert.True(File.Exists(docPath), $"design doc not found at {docPath}");

        var markdown = File.ReadAllText(docPath);
        var result = PlanImportService.ParseStructured(markdown);

        Assert.NotNull(result);
        Assert.Equal(
            ["M1", "M2", "M3", "M4", "M5", "M6", "M7", "M8", "M9"],
            result!.Stages.Select(s => s.Id).ToArray());
        // Every stage carried a human title and at least one checkpoint-derived session estimate.
        Assert.All(result.Stages, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
    }

    private static (string PlanPath, string Repo) WriteScratchPlan(params StageConfig[] stages)
    {
        var repo = Path.Combine(Path.GetTempPath(), $"plan-diff-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"), "# Tracker");
        var planPath = Path.Combine(Path.GetTempPath(), $"plan-diff-{Guid.NewGuid():N}.json");
        var plan = new PlanConfig
        {
            Name = "Diff",
            Repo = repo.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [.. stages],
        };
        var json = System.Text.Json.JsonSerializer.Serialize(plan, PlanConfig.JsonOpts);
        File.WriteAllText(planPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return (planPath, repo);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
