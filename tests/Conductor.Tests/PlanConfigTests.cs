using System.Text.Json;
using Conductor.Models;

namespace Conductor.Tests;

public class PlanConfigTests
{
    [Fact]
    public void GateStagesFieldDeserializesFromCamelCaseJson()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "gatePolicy": "perPhase",
          "gates": [
            { "name": "build", "command": "dotnet build" },
            { "name": "mcp-qa", "command": "node run.js", "stages": ["L5", "L8"], "parallel": true }
          ]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;

        Assert.True(cfg.PerPhaseGates);
        var build = cfg.Gates[0];
        var mcp = cfg.Gates[1];
        Assert.Null(build.Stages);
        Assert.True(build.AppliesToStage("L1"));            // unscoped → every stage
        Assert.Equal(new[] { "L5", "L8" }, mcp.Stages);
        Assert.True(mcp.Parallel);
        Assert.True(mcp.AppliesToStage("L5"));
        Assert.False(mcp.AppliesToStage("L1"));
    }

    [Fact]
    public void ShippedLoomPlanParsesWhenPresent()
    {
        // Locate plans/loom.opencode.plan.json relative to the repo; skip if not found (env-independent).
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "plans", "loom.opencode.plan.json");
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        if (path == null) return; // not in a full checkout — soft skip

        var text = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<PlanConfig>(text, PlanConfig.JsonOpts)!;
        Assert.Equal("perPhase", cfg.GatePolicy);
        var pnpm = cfg.Gates.First(g => g.Name == "pnpm-check");
        var mcp = cfg.Gates.First(g => g.Name == "mcp-qa");
        Assert.False(pnpm.AppliesToStage("L1"));   // UI gate scoped away from backend phases
        Assert.True(pnpm.AppliesToStage("L6"));
        Assert.True(mcp.AppliesToStage("L5"));
        Assert.False(mcp.AppliesToStage("L3"));
    }
}
