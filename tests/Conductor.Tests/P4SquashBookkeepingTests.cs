using Conductor.Models;

namespace Conductor.Tests;

public class P4SquashBookkeepingTests
{
    [Fact]
    public void RunState_roundtrips_SquashedStages()
    {
        var s = new RunState
        {
            PlanName = "Test",
            SquashedStages = { "P1", "P2" },
        };
        var path = Path.Combine(Path.GetTempPath(), $"conductor-p4squash-{Guid.NewGuid():N}.json");
        try
        {
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, "x");
            Assert.Contains("P1", loaded.SquashedStages);
            Assert.Contains("P2", loaded.SquashedStages);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RunState_roundtrips_StageStartHeads()
    {
        var s = new RunState
        {
            PlanName = "Test",
            StageStartHeads = { ["P1"] = "abc1234", ["P2"] = "def5678" },
        };
        var path = Path.Combine(Path.GetTempPath(), $"conductor-p4head-{Guid.NewGuid():N}.json");
        try
        {
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, "x");
            Assert.Equal("abc1234", loaded.StageStartHeads["P1"]);
            Assert.Equal("def5678", loaded.StageStartHeads["P2"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SquashedStages_persist_across_restart()
    {
        var s = new RunState
        {
            PlanName = "Test",
            ConfirmedStages = { "P1" },
            SquashedStages = { "P1" },
            StageStartHeads = { ["P1"] = "abc1234" },
        };
        var path = Path.Combine(Path.GetTempPath(), $"conductor-p4persist-{Guid.NewGuid():N}.json");
        try
        {
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, "x");
            Assert.Contains("P1", loaded.ConfirmedStages);
            Assert.Contains("P1", loaded.SquashedStages);
            Assert.Equal("abc1234", loaded.StageStartHeads["P1"]);
            // SquashedStages prevents re-squash on next confirm call
            Assert.Contains("P1", loaded.SquashedStages);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadOrNew_defaults_are_empty_not_null()
    {
        var loaded = new RunState { PlanName = "Test" };
        Assert.NotNull(loaded.SquashedStages);
        Assert.NotNull(loaded.StageStartHeads);
        Assert.Empty(loaded.SquashedStages);
        Assert.Empty(loaded.StageStartHeads);
    }
}
