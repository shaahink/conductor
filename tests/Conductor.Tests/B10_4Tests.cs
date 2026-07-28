using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class B10_4BatteryCollapseTests
{
    // ── Model: BatteryCollapse flag ─────────────────────────────────────

    [Fact]
    public void BatteryCollapseDefaultsToFalse()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [{ "id": "S1", "title": "S1" }]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.False(cfg.BatteryCollapse);
    }

    [Fact]
    public void BatteryCollapseDeserializesTrue()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "batteryCollapse": true,
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [{ "id": "S1", "title": "S1" }]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.True(cfg.BatteryCollapse);
    }

    // ── Prompt: battery collapse note in session template ───────────────

    [Fact]
    public void SessionPromptIncludesBatteryCollapseNoteWhenEnabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-b10-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.md"), "## Handoff\nlast: none.\n");
            var planPath = Path.Combine(dir, "plan.json");
            File.WriteAllText(planPath, $$"""
            {
              "name": "T",
              "repo": "{{dir.Replace("\\", "/")}}",
              "tracker": "t.md",
              "batteryCollapse": true,
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [{ "id": "S1", "title": "S1" }]
            }
            """);
            var plan = PlanConfig.Load(planPath);
            var builder = new PromptBuilder(plan);
            var prompt = builder.Deliver(plan.Stages[0], 1, 1, 2);

            Assert.Contains("battery collapse", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Do NOT run build or test", prompt, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SessionPromptOmitsBatteryCollapseNoteWhenDisabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-b10-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.md"), "## Handoff\nlast: none.\n");
            var planPath = Path.Combine(dir, "plan.json");
            File.WriteAllText(planPath, $$"""
            {
              "name": "T",
              "repo": "{{dir.Replace("\\", "/")}}",
              "tracker": "t.md",
              "batteryCollapse": false,
              "agent": { "command": "e", "args": ["{prompt}"] },
              "stages": [{ "id": "S1", "title": "S1" }]
            }
            """);
            var plan = PlanConfig.Load(planPath);
            var builder = new PromptBuilder(plan);
            var prompt = builder.Deliver(plan.Stages[0], 1, 1, 2);

            Assert.DoesNotContain("battery collapse", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Run the gate battery", prompt, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SelfPlanHasBatteryCollapseEnabled()
    {
        var selfPlanPath = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "plans", "conductor.self.plan.json"));
        if (!File.Exists(selfPlanPath)) return; // skip if running from different layout
        // Deserialize rather than Load: Load also validates, and validation asserts that plan.repo
        // EXISTS on this machine. The self-plan names the owner's checkout by absolute path, so on
        // any other clone — a CI runner, a contributor's laptop — Load throws and this test fails for
        // a reason that has nothing to do with batteryCollapse. The subject is one committed field;
        // parse the file (which still proves it is well-formed JSON the real options bind) and read it.
        var plan = System.Text.Json.JsonSerializer.Deserialize<PlanConfig>(
            File.ReadAllText(selfPlanPath), PlanConfig.JsonOpts);
        Assert.NotNull(plan);
        Assert.True(plan.BatteryCollapse,
            "The self-plan must have batteryCollapse: true for B10.4 to be active");
    }
}
