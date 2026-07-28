using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class B10_2HierarchyTests
{
    // ── Parent validation ──────────────────────────────────────────────

    [Fact]
    public void UnknownParentFailsValidation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "name": "T", "repo": ".", "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "A", "parentId": "Z" }
              ]
            }
            """)));
        Assert.Contains("not a known stage id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelfParentFailsValidation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "name": "T", "repo": ".", "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "A", "parentId": "A" }
              ]
            }
            """)));
        Assert.Contains("itself", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParentCycleFailsValidation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "name": "T", "repo": ".", "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "A", "parentId": "B" },
                { "id": "B", "title": "B", "parentId": "A" }
              ]
            }
            """)));
        Assert.Contains("parent hierarchy cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidParentChainLoads()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-b10-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.md"), "## Handoff\nlast: none.\n");
            File.WriteAllText(Path.Combine(dir, "plan.json"), $$"""
            {
              "name": "T",
              "repo": "{{dir.Replace("\\", "/")}}",
              "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "Parent" },
                { "id": "B", "title": "Child", "parentId": "A" },
                { "id": "C", "title": "Grandchild", "parentId": "B" }
              ]
            }
            """);
            var cfg = PlanConfig.Load(Path.Combine(dir, "plan.json"));
            Assert.Equal(3, cfg.Stages.Count);
            Assert.Null(cfg.Stages[0].ParentId);
            Assert.Equal("A", cfg.Stages[1].ParentId);
            Assert.Equal("B", cfg.Stages[2].ParentId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Depth computation ──────────────────────────────────────────────

    [Fact]
    public void DepthIsZeroForRootStages()
    {
        var stages = new[] { new StageConfig { Id = "A", Title = "A" } };
        Assert.Equal(0, SnapshotBuilder.ComputeDepth("A", stages));
    }

    [Fact]
    public void DepthIsOneForDirectChild()
    {
        var stages = new[]
        {
            new StageConfig { Id = "A", Title = "A" },
            new StageConfig { Id = "B", Title = "B", ParentId = "A" },
        };
        Assert.Equal(0, SnapshotBuilder.ComputeDepth("A", stages));
        Assert.Equal(1, SnapshotBuilder.ComputeDepth("B", stages));
    }

    [Fact]
    public void DepthChainsThreeLevels()
    {
        var stages = new[]
        {
            new StageConfig { Id = "A", Title = "A" },
            new StageConfig { Id = "B", Title = "B", ParentId = "A" },
            new StageConfig { Id = "C", Title = "C", ParentId = "B" },
        };
        Assert.Equal(0, SnapshotBuilder.ComputeDepth("A", stages));
        Assert.Equal(1, SnapshotBuilder.ComputeDepth("B", stages));
        Assert.Equal(2, SnapshotBuilder.ComputeDepth("C", stages));
    }

    // ── JSON deserialization ───────────────────────────────────────────

    [Fact]
    public void DeserializesParentIdFromCamelCase()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [
            { "id": "S1", "title": "Parent" },
            { "id": "S2", "title": "Child", "parentId": "S1" }
          ]
        }
        """;
        var cfg = System.Text.Json.JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Null(cfg.Stages[0].ParentId);
        Assert.Equal("S1", cfg.Stages[1].ParentId);
    }

    private static string WriteTempPlan(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-b10-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t.md"), "## Handoff\nlast: none.\n");
        var planPath = Path.Combine(dir, "plan.json");
        File.WriteAllText(planPath, json);
        return planPath;
    }
}
