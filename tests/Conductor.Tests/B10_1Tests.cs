using Conductor.Models;

namespace Conductor.Tests;

public class B10_1DependsOnTests
{
    [Fact]
    public void CycleDetectionFailsValidation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "name": "T", "repo": ".", "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "A", "dependsOn": ["B"] },
                { "id": "B", "title": "B", "dependsOn": ["A"] }
              ]
            }
            """)));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelfDependencyFailsValidation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "name": "T", "repo": ".", "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "A", "dependsOn": ["A"] }
              ]
            }
            """)));
        Assert.Contains("itself", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownDependencyFailsValidation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "name": "T", "repo": ".", "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "A", "dependsOn": ["Z"] }
              ]
            }
            """)));
        Assert.Contains("not a known stage id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinearChainOfThreeValidates()
    {
        // A → B → C (no cycle, all deps known) — should load fine
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
                { "id": "A", "title": "A" },
                { "id": "B", "title": "B", "dependsOn": ["A"] },
                { "id": "C", "title": "C", "dependsOn": ["B"] }
              ]
            }
            """);
            var cfg = PlanConfig.Load(Path.Combine(dir, "plan.json"));
            Assert.Equal(3, cfg.Stages.Count);
            Assert.Equal(new[] { "A" }, cfg.Stages[1].DependsOn);
            Assert.Equal(new[] { "B" }, cfg.Stages[2].DependsOn);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DiamondDependencyValidates()
    {
        // A → B, A → C, B+C → D
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
                { "id": "A", "title": "A" },
                { "id": "B", "title": "B", "dependsOn": ["A"] },
                { "id": "C", "title": "C", "dependsOn": ["A"] },
                { "id": "D", "title": "D", "dependsOn": ["B", "C"] }
              ]
            }
            """);
            var cfg = PlanConfig.Load(Path.Combine(dir, "plan.json"));
            Assert.Equal(4, cfg.Stages.Count);
            Assert.Equal(new[] { "B", "C" }, cfg.Stages[3].DependsOn);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DeserializesDependsOnFromCamelCase()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [
            { "id": "S1", "title": "First" },
            { "id": "S2", "title": "Second", "dependsOn": ["S1"] }
          ]
        }
        """;
        var cfg = System.Text.Json.JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Null(cfg.Stages[0].DependsOn);
        Assert.Equal(new[] { "S1" }, cfg.Stages[1].DependsOn);
    }

    [Fact]
    public void SelfLoopViaIntermediateIsDetected()
    {
        // A → B → C → A
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "name": "T", "repo": ".", "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [
                { "id": "A", "title": "A", "dependsOn": ["C"] },
                { "id": "B", "title": "B", "dependsOn": ["A"] },
                { "id": "C", "title": "C", "dependsOn": ["B"] }
              ]
            }
            """)));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
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
