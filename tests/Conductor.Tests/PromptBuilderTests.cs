using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class PromptBuilderTests
{
    private static PlanConfig Plan() => new()
    {
        Name = "Loom",
        Repo = @"C:\repo",
        Tracker = "LOOM-START.md",
        PlanDoc = "docs/proposal.md",
        TemplatesDir = $"no-such-dir-{Guid.NewGuid():N}", // force built-in templates
        PromptExtra = "EXTRA-MARKER",
    };

    private static readonly StageConfig Stage = new() { Id = "L2", Title = "BodyFacts", Sessions = 3, Notes = "watch the anchoring" };

    [Fact]
    public void DeliverFillsEveryPlaceholder()
    {
        var p = new PromptBuilder(Plan()).Deliver(Stage, 5, 2, 6);
        Assert.Contains("session #5", p);
        Assert.Contains("stage L2 — BodyFacts", p);
        Assert.Contains("attempt 2/6", p);
        Assert.Contains("LOOM-START.md", p);
        Assert.Contains("EXTRA-MARKER", p);
        Assert.Contains("watch the anchoring", p);
        Assert.DoesNotContain("{", p.Replace("{planName}", "")); // no unfilled {placeholders} left
    }

    [Fact]
    public void FixEmbedsGateFailures()
    {
        var fix = new PendingFix { FromSession = 4, GateFailures = "### Gate `build` FAILED", ProgressSummary = "commits: 0" };
        var p = new PromptBuilder(Plan()).Fix(Stage, 5, 3, 6, fix);
        Assert.Contains("session (#4) did not verify", p);
        Assert.Contains("Gate `build` FAILED", p);
        Assert.Contains("commits: 0", p);
    }

    [Fact]
    public void ResumeStatesTheReason()
    {
        var resume = new PendingResume { ClaudeSessionId = "abc", Reason = "session stalled (no output)", ResumeCount = 1 };
        var p = new PromptBuilder(Plan()).Resume(Stage, 6, 3, 6, resume);
        Assert.Contains("session stalled (no output)", p);
        Assert.Contains("git status", p);
    }

    [Fact]
    public void DeliverRendersReadOrder()
    {
        var plan = Plan();
        plan.ReadOrder = ["docs/ARCH.md", "docs/API.md", "docs/STYLE.md"];
        var p = new PromptBuilder(plan).Deliver(Stage, 1, 1, 1);
        Assert.Contains("Required reading (in order):", p);
        Assert.Contains("1. docs/ARCH.md", p);
        Assert.Contains("2. docs/API.md", p);
        Assert.Contains("3. docs/STYLE.md", p);
    }

    [Fact]
    public void ReadOrderEmptyWhenUnset()
    {
        var p = new PromptBuilder(Plan()).Deliver(Stage, 1, 1, 1);
        Assert.DoesNotContain("Required reading", p);
    }

    [Fact]
    public void TemplateFileOverridesBuiltIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-tpl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "templates"));
        var planPath = Path.Combine(dir, "plan.json");
        try
        {
            File.WriteAllText(Path.Combine(dir, "templates", "session.md"), "CUSTOM {stage} TEMPLATE");
            File.WriteAllText(planPath, "{}");
            var plan = Plan();
            plan.TemplatesDir = "templates";
            typeof(PlanConfig).GetProperty(nameof(PlanConfig.PlanFilePath))!.SetValue(plan, planPath);
            var p = new PromptBuilder(plan).Deliver(Stage, 1, 1, 6);
            Assert.Equal("CUSTOM L2 TEMPLATE", p);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
