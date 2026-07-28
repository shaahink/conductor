using Conductor.Commands;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// <c>conductor demo</c> is the credential-free front door — the first thing a stranger runs, and
/// on a platform we may never have tested. So the parts that can be checked without spawning the
/// whole loop are checked here: the scaffold must LOAD (the same self-check discipline
/// <c>conductor init</c> applies), the gates must stay shell-agnostic so the demo is portable, and
/// the built-in agent must pick the same checkpoint the engine's assignment policy would.
/// </summary>
public sealed class DemoCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"demo-t-{Guid.NewGuid():N}");

    public DemoCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Write exactly what ScaffoldAsync writes, and return the plan path. The plan is only
    /// valid alongside its tracker — which is itself the check that the two stay in step.</summary>
    private string Scaffold()
    {
        var planPath = Path.Combine(_dir, "conductor.plan.json");
        File.WriteAllText(planPath, DemoCommand.PlanJson(_dir, "/usr/local/bin/conductor"));
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), DemoCommand.Tracker);
        return planPath;
    }

    [Fact]
    public void ScaffoldedPlanLoads()
    {
        var plan = PlanConfig.Load(Scaffold());

        Assert.Equal("conductor-demo", plan.Name);
        Assert.Equal(2, plan.Stages.Count);
        Assert.Equal(2, plan.Gates.Count);
    }

    [Fact]
    public void GatesPinNoShellSoTheDemoIsPortable()
    {
        // The whole point: on Linux/macOS these must resolve to bash, not try to find powershell.
        // If someone pins a shell here, the demo stops being the cross-platform proof it exists to be.
        var plan = PlanConfig.Load(Scaffold());

        Assert.All(plan.Gates, g => Assert.True(string.IsNullOrEmpty(g.Shell),
            $"gate '{g.Name}' pins shell '{g.Shell}' — the demo's gates must run on the host's own shell"));
        Assert.All(plan.Gates, g => Assert.StartsWith("git ", g.Command, StringComparison.Ordinal));
    }

    [Fact]
    public void PlanPointsTheAgentAtThisExecutable()
    {
        var plan = PlanConfig.Load(Scaffold());

        Assert.Equal("/usr/local/bin/conductor", plan.Agent.Command);
        Assert.Contains("fake-agent", plan.Agent.Args);
        Assert.Contains("{prompt}", plan.Agent.Args);
        Assert.Contains("{sessionId}", plan.Agent.Args);
    }

    [Fact]
    public void TrackerHasThreeOpenCheckpointsAcrossTheTwoStages()
    {
        Assert.Equal("D1.1", FakeAgentCommand.FirstOpenRow(DemoCommand.Tracker, ""));
        Assert.Equal("D1.1", FakeAgentCommand.FirstOpenRow(DemoCommand.Tracker, "D1"));
        Assert.Equal("D2.1", FakeAgentCommand.FirstOpenRow(DemoCommand.Tracker, "D2"));
    }

    [Theory]
    [InlineData("DELIVER the next incomplete checkpoint(s) of stage D2 only.", "D2")]
    [InlineData("## Stage D1 — Build the thing", "D1")]
    [InlineData("no stage marker anywhere in this text", "")]
    public void StageIsReadOutOfThePrompt(string prompt, string expected) =>
        Assert.Equal(expected, FakeAgentCommand.StageFromPrompt(prompt));

    [Fact]
    public void ConfirmedRowsAreNotClaimedAgain()
    {
        // The engine regenerates the tracker after every session, so the agent sees earlier rows as
        // DONE. It must move on rather than re-deliver the same checkpoint forever.
        const string tracker = """
            | # | Checkpoint | Status | Commit | Evidence |
            |---|-----------|--------|--------|----------|
            | D1.1 | first | DONE | abc1234 | e |
            | D1.2 | second | DONE ✓ | def5678 | e |
            | D1.3 | third | TODO |  |  |
            """;

        Assert.Equal("D1.3", FakeAgentCommand.FirstOpenRow(tracker, "D1"));
    }

    [Fact]
    public void AStageWithNothingOpenYieldsNothing()
    {
        const string tracker = """
            | # | Checkpoint | Status | Commit | Evidence |
            |---|-----------|--------|--------|----------|
            | D1.1 | first | DONE | abc1234 | e |
            | D2.1 | other stage | TODO |  |  |
            """;

        // D1 is finished — the agent must not wander into D2's row just because it is open.
        Assert.Null(FakeAgentCommand.FirstOpenRow(tracker, "D1"));
        Assert.Equal("D2.1", FakeAgentCommand.FirstOpenRow(tracker, "D2"));
    }

    [Fact]
    public void InProgressCountsAsOpen()
    {
        const string tracker = """
            | # | Checkpoint | Status | Commit | Evidence |
            |---|-----------|--------|--------|----------|
            | D1.1 | resumed work | IN PROGRESS |  |  |
            """;

        Assert.Equal("D1.1", FakeAgentCommand.FirstOpenRow(tracker, "D1"));
    }
}
