using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// U0.2 — <c>conductor journey</c>'s pure, testable halves: <see cref="JourneyCommand.DescribeHumanMoments"/>
/// (every point a run can stop for a person, given the plan + tracker) and
/// <see cref="JourneyCommand.DescribeResumeAsync"/> (state.json first, run.db fallback — must mirror
/// RunCommand's own resume detection exactly, since journey promises what `conductor run` will do
/// without writing anything itself). The Spectre rendering shell around them is untestable console
/// output, same split as <c>PlanDiscovery</c> vs <c>ResolvePlanPath</c>.
/// </summary>
public sealed class JourneyCommandTests
{
    private static PlanConfig MinimalPlan() => new() { Name = "test-plan", Repo = Path.GetTempPath() };

    // ── DescribeHumanMoments ──

    [Fact]
    public void DefaultPlan_ReportsPauseOnBlockedOn_AndNoOwnerGatedStages()
    {
        var plan = MinimalPlan(); // PauseOnBlocked defaults true
        var lines = JourneyCommand.DescribeHumanMoments(plan, new TrackerSnapshot());

        Assert.Contains(lines, l => l.StartsWith("pauseOnBlocked: on", StringComparison.Ordinal));
        Assert.Contains(lines, l => l == "owner-gated stages: none");
    }

    [Fact]
    public void PauseOnBlockedFalse_ReportsOff()
    {
        var plan = MinimalPlan();
        plan.PauseOnBlocked = false;

        var lines = JourneyCommand.DescribeHumanMoments(plan, new TrackerSnapshot());

        Assert.Contains(lines, l => l.StartsWith("pauseOnBlocked: off", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnerGatedStages_AreListedById()
    {
        var plan = MinimalPlan();
        plan.Stages.Add(new StageConfig { Id = "S1", OwnerGate = true });
        plan.Stages.Add(new StageConfig { Id = "S2", OwnerGate = false });

        var lines = JourneyCommand.DescribeHumanMoments(plan, new TrackerSnapshot());

        Assert.Contains(lines, l => l.StartsWith("owner-gated stages: S1", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("S2", StringComparison.Ordinal));
    }

    [Fact]
    public void HandoffWithHumanToken_IsSurfaced()
    {
        var plan = MinimalPlan();
        var track = new TrackerSnapshot { HandoffBlock = "last: ok\nHUMAN: pick a database engine" };

        var lines = JourneyCommand.DescribeHumanMoments(plan, track);

        Assert.Contains(lines, l => l.Contains("HUMAN:", StringComparison.Ordinal) && l.Contains("pending right now", StringComparison.Ordinal));
    }

    [Fact]
    public void HandoffWithoutHumanToken_IsNotSurfaced()
    {
        var plan = MinimalPlan();
        var track = new TrackerSnapshot { HandoffBlock = "last: ok, nothing pending" };

        var lines = JourneyCommand.DescribeHumanMoments(plan, track);

        Assert.DoesNotContain(lines, l => l.Contains("pending right now", StringComparison.Ordinal));
    }

    [Fact]
    public void BudgetCaps_AreSurfacedWhenSet_AndOmittedWhenNot()
    {
        var withCaps = MinimalPlan();
        withCaps.Limits.MaxRunCostUsd = 12.5m;
        withCaps.Limits.MaxRunTokens = 1_000_000;
        withCaps.Limits.MaxSessions = 40;
        withCaps.Limits.ApprovalMode = true;

        var lines = JourneyCommand.DescribeHumanMoments(withCaps, new TrackerSnapshot());

        Assert.Contains(lines, l => l.StartsWith("maxRunCostUsd: $12.50", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("maxRunTokens: 1,000,000", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("maxSessions: 40", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("approvalMode: on", StringComparison.Ordinal));

        var withoutCaps = MinimalPlan();
        var bareLines = JourneyCommand.DescribeHumanMoments(withoutCaps, new TrackerSnapshot());

        Assert.DoesNotContain(bareLines, l => l.Contains("maxRunCostUsd", StringComparison.Ordinal));
        Assert.DoesNotContain(bareLines, l => l.Contains("maxRunTokens", StringComparison.Ordinal));
        Assert.DoesNotContain(bareLines, l => l.Contains("maxSessions", StringComparison.Ordinal));
        Assert.DoesNotContain(bareLines, l => l.Contains("approvalMode", StringComparison.Ordinal));
    }

    [Fact]
    public void MaxSessionsZero_IsTreatedAsNoCap()
    {
        var plan = MinimalPlan();
        plan.Limits.MaxSessions = 0;

        var lines = JourneyCommand.DescribeHumanMoments(plan, new TrackerSnapshot());

        Assert.DoesNotContain(lines, l => l.Contains("maxSessions", StringComparison.Ordinal));
    }

    // ── DescribeResumeAsync ──

    private static PlanConfig PlanWithScratchRepo(string repo)
        => new() { Name = "resume-test-plan", Repo = repo };

    [Fact]
    public async Task NoSavedStateAnywhere_ReportsFreshRun()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-journey-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        try
        {
            var plan = PlanWithScratchRepo(repo);
            var description = await JourneyCommand.DescribeResumeAsync(plan);
            Assert.Equal("fresh run — no saved state found", description);
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    [Fact]
    public async Task StateJsonPresent_ReportsResumeFromStateJson()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-journey-statejson-{Guid.NewGuid():N}");
        var stateDir = Path.Combine(repo, ".conductor");
        Directory.CreateDirectory(stateDir);
        try
        {
            var plan = PlanWithScratchRepo(repo);
            var state = new RunState
            {
                PlanName = plan.Name, RunId = "abcdef1234567890", CurrentStage = "S2", SessionCounter = 4,
            };
            state.Save(Path.Combine(stateDir, "state.json"));

            var description = await JourneyCommand.DescribeResumeAsync(plan);

            Assert.Contains("resumes session #5", description, StringComparison.Ordinal);
            Assert.Contains("stage S2", description, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    [Fact]
    public async Task NoStateJson_FallsBackToRunDb_LikeRunCommandDoes()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-journey-rundb-{Guid.NewGuid():N}");
        var stateDir = Path.Combine(repo, ".conductor");
        Directory.CreateDirectory(stateDir);
        var dbPath = Path.Combine(stateDir, "run.db");
        try
        {
            var plan = PlanWithScratchRepo(repo);
            using (var db = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance))
            {
                db.InitializeRun("r1", plan.Name, repo, "b", Conductor.Core.EngineStamp.Parse("v"));
                var state = new RunState { PlanName = plan.Name, RunId = "r1", CurrentStage = "S1", SessionCounter = 2 };
                db.SaveRunState("r1", plan.Name, System.Text.Json.JsonSerializer.Serialize(state, PlanConfig.JsonOpts));
            }

            var description = await JourneyCommand.DescribeResumeAsync(plan);

            Assert.Contains("resumes session #3", description, StringComparison.Ordinal);
            Assert.Contains("stage S1", description, StringComparison.Ordinal);
        }
        finally
        {
            // Microsoft.Data.Sqlite pools native connections past Dispose, so the file can stay
            // locked briefly — best-effort cleanup only, same pattern as RunDbTests.
            foreach (var suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + suffix); } catch { } }
            try { TestTemp.DeleteTree(repo); } catch { }
        }
    }
}
