using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

[Trait("Category", "Integration")]
public class GateRunnerTests
{
    private static PlanConfig Plan(params GateConfig[] gates) => new()
    {
        Repo = Path.GetTempPath(),
        Gates = gates.ToList(),
    };

    [Fact]
    public async Task CapturesRealExitCodes()
    {
        // The lesson that motivated this tool: `cmd | tail` hid a non-building UI.
        var plan = Plan(
            new GateConfig { Name = "pass", Command = "exit 0", TimeoutMinutes = 1 },
            new GateConfig { Name = "fail", Command = "Write-Output 'boom'; exit 3", TimeoutMinutes = 1 });
        var results = await GateRunner.RunAllAsync(plan);
        Assert.True(results[0].Passed);
        Assert.False(results[1].Passed);
        Assert.Equal(3, results[1].ExitCode);
        Assert.Contains("boom", results[1].Tail);
        Assert.False(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public async Task NativeCommandExitCodePropagates()
    {
        var plan = Plan(new GateConfig { Name = "git-fail", Command = "git rev-parse --verify definitely-not-a-ref", TimeoutMinutes = 1 });
        var results = await GateRunner.RunAllAsync(plan);
        Assert.False(results[0].Passed);
        Assert.NotEqual(0, results[0].ExitCode);
    }

    [Fact]
    public async Task SkipsGateWhenProbeMissing()
    {
        var plan = Plan(new GateConfig { Name = "later", Command = "exit 1", SkipIfMissing = $"does-not-exist-{Guid.NewGuid():N}.ps1" });
        var results = await GateRunner.RunAllAsync(plan);
        Assert.True(results[0].Skipped);
        Assert.True(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public async Task OptionalGateFailureDoesNotBlock()
    {
        var plan = Plan(new GateConfig { Name = "advisory", Command = "exit 5", Optional = true, TimeoutMinutes = 1 });
        var results = await GateRunner.RunAllAsync(plan);
        Assert.False(results[0].Passed);
        Assert.True(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public async Task FailureDetailsEmbedsTails()
    {
        var plan = Plan(new GateConfig { Name = "fail", Command = "Write-Output 'line-one'; Write-Output 'line-two'; exit 2", TimeoutMinutes = 1 });
        var details = GateRunner.FailureDetails(await GateRunner.RunAllAsync(plan));
        Assert.Contains("Gate `fail` FAILED (exit 2", details);
        Assert.Contains("line-two", details);
    }

    [Fact]
    public async Task FastOnlyRunsOnlyFastTierGates()
    {
        var plan = Plan(
            new GateConfig { Name = "build", Command = "exit 0", Tier = "fast", TimeoutMinutes = 1 },
            new GateConfig { Name = "tests", Command = "exit 0", Tier = "full", TimeoutMinutes = 1 });
        var results = await GateRunner.RunAllAsync(plan, fastOnly: true);
        Assert.Single(results);
        Assert.Equal("build", results[0].Name);
    }

    [Fact]
    public async Task ParallelGatesAllRunAndKeepOrderAndExitCodes()
    {
        var plan = Plan(
            new GateConfig { Name = "build", Command = "exit 0", TimeoutMinutes = 1 },
            new GateConfig { Name = "a", Command = "Start-Sleep -Milliseconds 200; exit 0", Parallel = true, TimeoutMinutes = 1 },
            new GateConfig { Name = "b", Command = "Start-Sleep -Milliseconds 200; exit 7", Parallel = true, TimeoutMinutes = 1 });
        var results = await GateRunner.RunAllAsync(plan);
        // batch runs concurrently but results keep the configured order
        Assert.Equal(new[] { "build", "a", "b" }, results.Select(r => r.Name));
        Assert.True(results[0].Passed);
        Assert.True(results[1].Passed);
        Assert.Equal(7, results[2].ExitCode);
        Assert.False(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public async Task StageFilterRunsOnlyMatchingStages()
    {
        var plan = Plan(
            new GateConfig { Name = "build", Command = "exit 0", TimeoutMinutes = 1 },
            new GateConfig { Name = "mcp-qa", Command = "exit 0", Stages = new() { "L5", "L8" }, TimeoutMinutes = 1 });

        var onL1 = await GateRunner.RunAllAsync(plan, currentStage: "L1");
        Assert.Equal(new[] { "build" }, onL1.Select(r => r.Name)); // mcp-qa filtered out on L1

        var onL5 = await GateRunner.RunAllAsync(plan, currentStage: "L5");
        Assert.Equal(new[] { "build", "mcp-qa" }, onL5.Select(r => r.Name));
    }

    [Fact]
    public void AppliesToStageIsCaseInsensitiveAndDefaultsToAll()
    {
        Assert.True(new GateConfig().AppliesToStage("L1"));                       // no filter → all
        Assert.True(new GateConfig { Stages = new() { "l5" } }.AppliesToStage("L5"));
        Assert.False(new GateConfig { Stages = new() { "L5" } }.AppliesToStage("L1"));
    }

    [Fact]
    public void BatterySignatureChangesWithHeadAndStage()
    {
        var plan = Plan(
            new GateConfig { Name = "build", Command = "exit 0" },
            new GateConfig { Name = "mcp-qa", Command = "exit 0", Stages = new() { "L5" } });

        var l1a = GateRunner.BatterySignature(plan, "sha1", "L1");
        var l1b = GateRunner.BatterySignature(plan, "sha2", "L1"); // different HEAD
        var l5 = GateRunner.BatterySignature(plan, "sha1", "L5");  // different gate-set (mcp-qa applies)

        Assert.NotEqual(l1a, l1b);
        Assert.NotEqual(l1a, l5);
        Assert.Equal(l1a, GateRunner.BatterySignature(plan, "sha1", "L1")); // stable
    }

    [Fact]
    public async Task LiveGateProgressReportsRunningThenFinal()
    {
        var plan = Plan(new GateConfig { Name = "build", Command = "exit 0", TimeoutMinutes = 1 });
        var states = new List<string>();
        await GateRunner.RunAllAsync(plan, onGates: g => { if (g.Count > 0) states.Add(g[0].State); });
        Assert.Contains("running", states);
        Assert.Contains("pass", states);
    }
}

/// <summary>U0.3: <see cref="GateRunner.Summary"/> on a gateless plan (pure, no process spawn —
/// deliberately not in the Integration-tagged class above).</summary>
public sealed class GateRunnerSummaryTests
{
    [Fact]
    public void EmptyResults_ReadsGatesGreenNoneConfigured()
    {
        Assert.Equal("gates green (none configured)", GateRunner.Summary([]));
    }

    [Fact]
    public void NonEmptyResults_JoinsNameAndGlyph()
    {
        var results = new[] { new GateResult("build", true, false, false, 0, TimeSpan.Zero, "") };
        Assert.Equal("build:OK", GateRunner.Summary(results));
    }

    [Fact]
    public void EmptyResults_AllRequiredPassedIsVacuouslyTrue()
    {
        Assert.True(GateRunner.AllRequiredPassed(Array.Empty<GateResult>()));
    }
}
