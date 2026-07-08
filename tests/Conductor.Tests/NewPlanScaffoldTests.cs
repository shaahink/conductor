using System.Text.Json;
using Conductor.Commands;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

// B1.6 audit — the `new-plan` scaffold must emit a plan whose tracker is drivable: every declared
// stage has to own at least one checkpoint row, or that stage can never flip to DONE and the run
// stalls. The shamshir template regressed this (declared P-0/P0/P1 stages but scaffolded S1 rows);
// this locks coherence across every template so the drift can't come back.
public sealed class NewPlanScaffoldTests
{
    [Theory]
    [InlineData("minimal")]
    [InlineData("dotnet")]
    [InlineData("node")]
    [InlineData("shamshir")]
    public void Scaffold_EveryDeclaredStageOwnsAtLeastOneCheckpointRow(string template)
    {
        var plan = JsonSerializer.Deserialize<PlanConfig>(
            NewPlanCommand.BuildPlanJson(template, "demo", "C:/tmp/demo"), PlanConfig.JsonOpts)!;
        var snap = MarkdownTableProvider.Parse(NewPlanCommand.BuildTrackerMd(template, "demo"), plan.Conventions);

        Assert.NotEmpty(plan.Stages);
        Assert.NotEmpty(snap.Checkpoints);
        Assert.All(snap.Checkpoints, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
        foreach (var stage in plan.Stages)
            Assert.True(
                snap.ForStage(stage.Id).Any(),
                $"template '{template}': stage '{stage.Id}' has no checkpoint rows in the scaffolded tracker — it can never complete");
    }

    [Fact]
    public void ShamshirScaffold_UsesIrregularStageIds_NotTheDefaultS1()
    {
        var plan = JsonSerializer.Deserialize<PlanConfig>(
            NewPlanCommand.BuildPlanJson("shamshir", "demo", "C:/tmp/demo"), PlanConfig.JsonOpts)!;
        var snap = MarkdownTableProvider.Parse(NewPlanCommand.BuildTrackerMd("shamshir", "demo"), plan.Conventions);

        Assert.Equal(new[] { "P-0", "P0", "P1" }, plan.Stages.Select(s => s.Id));
        Assert.Equal(new[] { "P-0", "P0.1", "P1.1" }, snap.Checkpoints.Select(c => c.Id));
        Assert.Equal("P-0", snap.ById("P-0")!.StageId);   // hyphenated id resolves under the plan's pattern
        Assert.Contains("P-0 NOT STARTED", snap.HandoffBlock, StringComparison.Ordinal);
    }
}
