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
        // Locate examples/loom/loom.opencode.plan.json relative to the repo; skip if not found (env-independent).
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "examples", "loom", "loom.opencode.plan.json");
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

    [Fact]
    public void DefaultPlanHasVersion1_0()
    {
        var cfg = new PlanConfig();
        Assert.Equal("1.0", cfg.Version);
    }

    [Fact]
    public void UnsupportedVersionThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "version": "2.0",
              "name": "T",
              "repo": ".",
              "tracker": "t.md",
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [{ "id": "S", "title": "T", "sessions": 1 }]
            }
            """)));
        Assert.Contains("2.0", ex.Message);
        Assert.Contains("supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingVersionDefaultsTo1_0()
    {
        var cfg = JsonSerializer.Deserialize<PlanConfig>("""
            { "name": "T" }
            """, PlanConfig.JsonOpts)!;
        Assert.Equal("1.0", cfg.Version);
    }

    [Fact]
    public void MissingVersionLoadsAs1_0()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-version-{Guid.NewGuid():N}");
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
              "stages": [{ "id": "S", "title": "T", "sessions": 1 }]
            }
            """);
            var cfg = PlanConfig.Load(Path.Combine(dir, "plan.json"));
            Assert.Equal("1.0", cfg.Version);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ValidationReportsAllErrorsNotJustFirst()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanConfig.Load(WriteTempPlan("""
            {
              "version": "1.0",
              "name": "T",
              "tracker": "t.md",
              "agent": { "command": "" }
            }
            """)));
        var msg = ex.Message;
        Assert.Contains("repo", msg);
        Assert.Contains("args", msg);
        Assert.Contains("stages", msg);
    }

    private static string WriteTempPlan(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-pc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t.md"), "## Handoff\nlast: none.\n");
        var planPath = Path.Combine(dir, "plan.json");
        File.WriteAllText(planPath, json);
        return planPath;
    }

    [Fact]
    public void BudgetCapsDeserialize()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "limits": { "maxRunCostUsd": 5.00, "maxRunTokens": 500000, "approvalMode": true }
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(5.00m, cfg.Limits.MaxRunCostUsd);
        Assert.Equal(500000, cfg.Limits.MaxRunTokens);
        Assert.True(cfg.Limits.ApprovalMode);
    }

    [Fact]
    public void BudgetCapsDefaultToNull()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] }
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Null(cfg.Limits.MaxRunCostUsd);
        Assert.Null(cfg.Limits.MaxRunTokens);
        Assert.False(cfg.Limits.ApprovalMode);
    }

    [Fact]
    public void StageAgentOverrideMergesOverPlanDefault()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"], "provider": "opencode", "output": "opencode-json", "systemPrompt": "base sys" },
          "stages": [
            { "id": "S1", "title": "First" },
            { "id": "S2", "title": "Second", "agent": { "systemPrompt": "stage sys", "temperature": 0.3, "model": "gpt-5" }, "persona": "qa" }
          ]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;

        // S1: no override → plan default
        var s1 = cfg.ResolveAgent(cfg.Stages[0]);
        Assert.Equal("opencode", s1.Command);
        Assert.Equal("base sys", s1.SystemPrompt);
        Assert.Null(s1.Temperature);

        // S2: override merged over plan default
        var s2 = cfg.ResolveAgent(cfg.Stages[1]);
        Assert.Equal("opencode", s2.Command);           // not overridden → falls back
        Assert.Equal("stage sys", s2.SystemPrompt);     // overridden
        Assert.Equal(0.3, s2.Temperature);              // overridden
        Assert.Equal("gpt-5", s2.Model);                // overridden
        Assert.Equal("opencode", s2.Provider);          // not overridden → falls back

        // Persona resolution: stage.Persona wins
        Assert.Equal("qa", cfg.ResolvePersona(cfg.Stages[1]));
        Assert.Null(cfg.ResolvePersona(cfg.Stages[0]));
    }

    [Fact]
    public void PersonaScrapedFromNotesFallback()
    {
        var cfg = new PlanConfig { Repo = ".", Tracker = "t.md" };
        cfg.Stages.Add(new StageConfig { Id = "S1", Notes = "Read docs/baton/stages/B2.md. Persona: architect. Do the thing." });
        cfg.Stages.Add(new StageConfig { Id = "S2", Notes = "No persona here." });

        Assert.Equal("architect", cfg.ResolvePersona(cfg.Stages[0]));
        Assert.Null(cfg.ResolvePersona(cfg.Stages[1]));
    }

    [Fact]
    public void EmptyArgsDoesNotOverride()
    {
        var cfg = new PlanConfig { Repo = ".", Tracker = "t.md" };
        cfg.Agent.Args.Add("run");
        cfg.Agent.Args.Add("{prompt}");
        cfg.Stages.Add(new StageConfig { Id = "S1", Agent = new AgentConfig { Args = new() } });
        // Empty args list → falls back to plan default
        Assert.Equal(2, cfg.ResolveAgent(cfg.Stages[0]).Args.Count);
    }
}
