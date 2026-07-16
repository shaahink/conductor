using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public sealed class PlanImportServiceTests : IDisposable
{
    private readonly string _planPath;
    private readonly string _tempRepo;
    private readonly PlanConfig _plan;

    public PlanImportServiceTests()
    {
        _tempRepo = Path.Combine(Path.GetTempPath(), $"plan-import-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRepo);
        // PlanConfig.Validate requires tracker + planDoc to exist
        File.WriteAllText(Path.Combine(_tempRepo, "TRACKER.md"), "# Test Tracker");
        Directory.CreateDirectory(Path.Combine(_tempRepo, "docs"));
        File.WriteAllText(Path.Combine(_tempRepo, "docs", "PLAN.md"), "# Test Plan");

        _planPath = Path.Combine(Path.GetTempPath(), $"plan-import-{Guid.NewGuid():N}.json");

        var plan = new PlanConfig
        {
            Name = "TestPlan",
            Repo = _tempRepo.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            PlanDoc = "docs/PLAN.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"], Provider = "fake", Output = "text" },
            Stages = [new StageConfig { Id = "F0", Title = "Dummy", Sessions = 1, Notes = "placeholder" }],
            Gates = [],
            GatePolicy = "perPhase",
            Limits = new LimitsConfig(),
            ReadOrder = [],
            Batteries = new BatteriesConfig { Lessons = false, RecentFailure = false },
        };
        var json = JsonSerializer.Serialize(plan, PlanConfig.JsonOpts);
        File.WriteAllText(_planPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _plan = PlanConfig.Load(_planPath);
    }

    public void Dispose()
    {
        try { File.Delete(_planPath); } catch { }
        try { Directory.Delete(_tempRepo, true); } catch { }
    }

    [Fact]
    public void ApplyToPlan_AddsNewStages()
    {
        var result = new ImportResult
        {
            Stages = [
                new StageConfig { Id = "S1", Title = "Stage One", Sessions = 2, Kind = "deliver" },
                new StageConfig { Id = "S2", Title = "Stage Two", Sessions = 3, Kind = "review", DependsOn = ["S1"] },
            ],
            Gates = []
        };

        PlanImportService.ApplyToPlan(_plan, result);

        Assert.Equal(3, _plan.Stages.Count); // 1 dummy F0 + 2 new
        Assert.Equal("S1", _plan.Stages[1].Id);
        Assert.Equal("Stage One", _plan.Stages[1].Title);
        Assert.Equal("S2", _plan.Stages[2].Id);
        Assert.Single(_plan.Stages[2].DependsOn!);
        Assert.Equal("S1", _plan.Stages[2].DependsOn![0]);
    }

    [Fact]
    public void ApplyToPlan_MergesExistingStages()
    {
        _plan.Stages.Add(new StageConfig { Id = "S1", Title = "Old Title", Sessions = 1, Kind = "deliver" });

        var result = new ImportResult
        {
            Stages = [
                new StageConfig { Id = "S1", Title = "New Title", Kind = "review", Sessions = 0 },
            ],
            Gates = []
        };

        PlanImportService.ApplyToPlan(_plan, result);

        Assert.Equal(2, _plan.Stages.Count); // 1 F0 + 1 (S1 merged, not added)
        // Title and Kind updated on S1 (index 1, after dummy F0)
        Assert.Equal("New Title", _plan.Stages[1].Title);
        Assert.Equal("review", _plan.Stages[1].Kind);
        // Sessions NOT overwritten (result.Sessions was 0, which is <= 0)
        Assert.Equal(1, _plan.Stages[1].Sessions);
    }

    [Fact]
    public void ApplyToPlan_UpdatesSessionCountWhenPositive()
    {
        _plan.Stages.Add(new StageConfig { Id = "S1", Title = "Test", Sessions = 1, Kind = "deliver" });

        var result = new ImportResult
        {
            Stages = [
                new StageConfig { Id = "S1", Sessions = 5, Kind = "deliver" },
            ],
            Gates = []
        };

        PlanImportService.ApplyToPlan(_plan, result);

        Assert.Equal(5, _plan.Stages[1].Sessions);
    }

    [Fact]
    public void ApplyToPlan_AddsNewGates()
    {
        var result = new ImportResult
        {
            Stages = [],
            Gates = [
                new GateConfig { Name = "lint", Command = "dotnet format", Tier = "fast", TimeoutMinutes = 5 },
            ]
        };

        PlanImportService.ApplyToPlan(_plan, result);

        Assert.Single(_plan.Gates);
        Assert.Equal("lint", _plan.Gates[0].Name);
        Assert.Equal("dotnet format", _plan.Gates[0].Command);
        Assert.Equal("fast", _plan.Gates[0].Tier);
    }

    [Fact]
    public void ApplyToPlan_MergesExistingGates()
    {
        _plan.Gates.Add(new GateConfig { Name = "build", Command = "dotnet build", Tier = "fast", TimeoutMinutes = 10 });

        var result = new ImportResult
        {
            Stages = [],
            Gates = [
                new GateConfig { Name = "build", Command = "dotnet build --no-restore", Tier = "full", TimeoutMinutes = 0 },
            ]
        };

        PlanImportService.ApplyToPlan(_plan, result);

        Assert.Single(_plan.Gates);
        Assert.Equal("dotnet build --no-restore", _plan.Gates[0].Command);
        Assert.Equal("full", _plan.Gates[0].Tier);
        // Timeout NOT overwritten (result.TimeoutMinutes was 0)
        Assert.Equal(10, _plan.Gates[0].TimeoutMinutes);
    }

    [Fact]
    public void ApplyToPlan_IncrementsPlanVersion()
    {
        var v1 = _plan.PlanVersion;

        var result = new ImportResult { Stages = [], Gates = [] };
        PlanImportService.ApplyToPlan(_plan, result);

        Assert.Equal(v1 + 2, _plan.PlanVersion); // ApplyToPlan calls BumpVersion + Save (which also bumps)
    }

    [Fact]
    public void ApplyToPlan_MixedAddAndMerge()
    {
        _plan.Stages.Add(new StageConfig { Id = "F5", Title = "Old F5", Sessions = 1, Kind = "deliver" });
        _plan.Gates.Add(new GateConfig { Name = "build", Command = "dotnet build", Tier = "fast", TimeoutMinutes = 10 });

        var result = new ImportResult
        {
            Stages = [
                new StageConfig { Id = "F5", Title = "Updated F5", Kind = "deliver" },
                new StageConfig { Id = "F6", Title = "New Stage", Sessions = 3, Kind = "refactor", DependsOn = ["F5"] },
            ],
            Gates = [
                new GateConfig { Name = "tests", Command = "dotnet test", Tier = "full", TimeoutMinutes = 20 },
            ]
        };

        PlanImportService.ApplyToPlan(_plan, result);

        Assert.Equal(3, _plan.Stages.Count); // F0 + updated F5 + new F6
        Assert.Equal("Updated F5", _plan.Stages[1].Title);
        Assert.Equal("F6", _plan.Stages[2].Id);
        Assert.Equal(["F5"], _plan.Stages[2].DependsOn);

        Assert.Equal(2, _plan.Gates.Count);
        Assert.Equal("build", _plan.Gates[0].Name);
        Assert.Equal("tests", _plan.Gates[1].Name);
    }

    [Fact]
    public void ApplyToPlan_DoesNotClobberDependsOnWhenEmpty()
    {
        _plan.Stages.Add(new StageConfig { Id = "S1", Title = "S1", Sessions = 1, Kind = "deliver", DependsOn = ["F0"] });

        var result = new ImportResult
        {
            Stages = [
                new StageConfig { Id = "S1", Title = "S1 v2", Kind = "deliver" },
            ],
            Gates = []
        };

        PlanImportService.ApplyToPlan(_plan, result);

        // DependsOn should NOT be overwritten because result's DependsOn is null (not Count > 0)
        Assert.Equal(["F0"], _plan.Stages[1].DependsOn);
    }

    [Fact]
    public void ResolveInterpreterModel_OverrideWins()
    {
        _plan.Advisor = new AdvisorConfig { Args = ["--model", "claude-fable-5"] };
        Assert.Equal("claude-opus-4-8", PlanImportService.ResolveInterpreterModel(_plan, "claude-opus-4-8"));
    }

    [Fact]
    public void ResolveInterpreterModel_ReadsModelFromAdvisorArgs()
    {
        _plan.Advisor = new AdvisorConfig { Args = ["-p", "{prompt}", "--model", "claude-fable-5"] };
        Assert.Equal("claude-fable-5", PlanImportService.ResolveInterpreterModel(_plan));
    }

    [Fact]
    public void ResolveInterpreterModel_SkipsUnfilledPlaceholderAndMissingArgs()
    {
        _plan.Advisor = new AdvisorConfig { Args = ["--model", "{model}"] };
        Assert.Null(PlanImportService.ResolveInterpreterModel(_plan));
        _plan.Advisor = new AdvisorConfig { Args = ["-p", "{prompt}"] };
        Assert.Null(PlanImportService.ResolveInterpreterModel(_plan));
        _plan.Advisor = null;
        Assert.Null(PlanImportService.ResolveInterpreterModel(_plan));
    }

    [Fact]
    public void ApplyToPlan_ClobbersDependsOnWhenProvided()
    {
        _plan.Stages.Add(new StageConfig { Id = "S1", Title = "S1", Sessions = 1, Kind = "deliver", DependsOn = ["F0"] });

        var result = new ImportResult
        {
            Stages = [
                new StageConfig { Id = "S1", Title = "S1 v2", Kind = "deliver", DependsOn = ["F5"] },
            ],
            Gates = []
        };

        PlanImportService.ApplyToPlan(_plan, result);

        // DependsOn SHOULD be overwritten because result's DependsOn has Count > 0
        Assert.Equal(["F5"], _plan.Stages[1].DependsOn);
    }
}
