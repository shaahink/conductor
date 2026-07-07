using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class GateRunnerTests
{
    private static PlanConfig Plan(params GateConfig[] gates) => new()
    {
        Repo = Path.GetTempPath(),
        Gates = gates.ToList(),
    };

    [Fact]
    public void CapturesRealExitCodes()
    {
        // The lesson that motivated this tool: `cmd | tail` hid a non-building UI.
        var plan = Plan(
            new GateConfig { Name = "pass", Command = "exit 0", TimeoutMinutes = 1 },
            new GateConfig { Name = "fail", Command = "Write-Output 'boom'; exit 3", TimeoutMinutes = 1 });
        var results = GateRunner.RunAll(plan);
        Assert.True(results[0].Passed);
        Assert.False(results[1].Passed);
        Assert.Equal(3, results[1].ExitCode);
        Assert.Contains("boom", results[1].Tail);
        Assert.False(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public void NativeCommandExitCodePropagates()
    {
        var plan = Plan(new GateConfig { Name = "git-fail", Command = "git rev-parse --verify definitely-not-a-ref", TimeoutMinutes = 1 });
        var results = GateRunner.RunAll(plan);
        Assert.False(results[0].Passed);
        Assert.NotEqual(0, results[0].ExitCode);
    }

    [Fact]
    public void SkipsGateWhenProbeMissing()
    {
        var plan = Plan(new GateConfig { Name = "later", Command = "exit 1", SkipIfMissing = $"does-not-exist-{Guid.NewGuid():N}.ps1" });
        var results = GateRunner.RunAll(plan);
        Assert.True(results[0].Skipped);
        Assert.True(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public void OptionalGateFailureDoesNotBlock()
    {
        var plan = Plan(new GateConfig { Name = "advisory", Command = "exit 5", Optional = true, TimeoutMinutes = 1 });
        var results = GateRunner.RunAll(plan);
        Assert.False(results[0].Passed);
        Assert.True(GateRunner.AllRequiredPassed(results));
    }

    [Fact]
    public void FailureDetailsEmbedsTails()
    {
        var plan = Plan(new GateConfig { Name = "fail", Command = "Write-Output 'line-one'; Write-Output 'line-two'; exit 2", TimeoutMinutes = 1 });
        var details = GateRunner.FailureDetails(GateRunner.RunAll(plan));
        Assert.Contains("Gate `fail` FAILED (exit 2", details);
        Assert.Contains("line-two", details);
    }
}
