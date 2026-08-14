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

    /// <summary>The KS2.3 acceptance says the preview writes NO state — so the peek must leave no
    /// catalogue row and no derived store behind. The repo path is unique per test, so asserting
    /// against the shared process state home is race-free.</summary>
    private static void AssertPeekLeftNoTrace(PlanConfig plan)
    {
        var root = TestEnvironmentIsolation.StateHomeRoot;
        Assert.Null(StateCatalogue.Find(root, plan.Repo, plan.Name));
        Assert.False(File.Exists(StateHome.DerivedRunDbPath(root, plan.Repo, plan.Name)),
            "the resume peek must not import anything into the state home");
    }

    [Fact]
    public async Task NoSavedStateAnywhere_ReportsFreshRun_AndRegistersNothing()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-journey-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        try
        {
            var plan = PlanWithScratchRepo(repo);
            var description = await JourneyCommand.DescribeResumeAsync(plan);
            Assert.Equal("fresh run — no saved state found", description);
            // The verifier's KS2.3 repro: one `journey` against an empty state home used to leave a
            // catalogue row pointing at a run.db that does not exist — a phantom "past run" the hub,
            // history and the picker would all list for a run nobody ever started.
            AssertPeekLeftNoTrace(plan);
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
    public async Task NoStateJson_FallsBackToRunDb_LikeRunCommandDoes_WithoutImportingIt()
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
            // A pre-K3.1 store in the working tree is DESCRIBED from where it lies. Running the
            // legacy import here (plan.RunDbPath used to) would copy the database into the machine
            // home and catalogue the pair — a preview performing K3.1's migration.
            AssertPeekLeftNoTrace(plan);
        }
        finally
        {
            // Microsoft.Data.Sqlite pools native connections past Dispose, so the file can stay
            // locked briefly — best-effort cleanup only, same pattern as RunDbTests.
            foreach (var suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + suffix); } catch { } }
            try { TestTemp.DeleteTree(repo); } catch { }
        }
    }

    /// <summary>A state.json belonging to a DIFFERENT plan means `run` would archive it and start
    /// fresh — journey must say fresh, but the archiving is `run`'s move, not a preview's: the file
    /// stays exactly where it was.</summary>
    [Fact]
    public async Task ForeignPlansStateJson_ReadsAsFresh_AndIsNotArchived()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-journey-foreign-{Guid.NewGuid():N}");
        var stateDir = Path.Combine(repo, ".conductor");
        Directory.CreateDirectory(stateDir);
        var statePath = Path.Combine(stateDir, "state.json");
        try
        {
            var foreign = new RunState { PlanName = "some-other-plan", RunId = "zzz", SessionCounter = 9 };
            foreign.Save(statePath);
            var before = await File.ReadAllTextAsync(statePath);

            var description = await JourneyCommand.DescribeResumeAsync(PlanWithScratchRepo(repo));

            Assert.Equal("fresh run — no saved state found", description);
            Assert.True(File.Exists(statePath), "the preview must not archive another plan's state.json");
            Assert.Equal(before, await File.ReadAllTextAsync(statePath));
            Assert.Single(Directory.GetFiles(stateDir));   // no state.<plan>.<stamp>.json appeared
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    /// <summary>Corrupt state.json: `run` keeps a .corrupt copy and starts fresh; the preview only
    /// reports the fresh start — it does not write the copy.</summary>
    [Fact]
    public async Task CorruptStateJson_ReadsAsFresh_WithoutWritingACorruptCopy()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-journey-corrupt-{Guid.NewGuid():N}");
        var stateDir = Path.Combine(repo, ".conductor");
        Directory.CreateDirectory(stateDir);
        var statePath = Path.Combine(stateDir, "state.json");
        try
        {
            await File.WriteAllTextAsync(statePath, "{ not json");

            var description = await JourneyCommand.DescribeResumeAsync(PlanWithScratchRepo(repo));

            Assert.Equal("fresh run — no saved state found", description);
            Assert.False(File.Exists(statePath + ".corrupt"), "the preview must not write the .corrupt copy");
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    // ── KS2.3: the hub's preview is this verb ──

    /// <summary>The hub's journey preview is this command run as a sibling process — not a copy of
    /// the rendering — so what the hub shows before a launch and what <c>conductor journey</c> prints
    /// are one output that cannot drift into two.</summary>
    [Fact]
    public void TheHubPreviewsWithThisVerbItself()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var hub = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Conductor", "Commands", "HubCommand.cs"));
        Assert.Contains("SiblingAsync(\"journey\", \"-p\", p)", hub, StringComparison.Ordinal);
    }
}
