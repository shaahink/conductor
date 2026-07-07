using Conductor.Core;

namespace Conductor.Tests;

public class StatusAgentTests
{
    [Fact]
    public void BuildPromptEmbedsLiveContextAndIsReadOnly()
    {
        var snap = new DashboardSnapshot
        {
            PlanName = "Loom",
            Status = "Running",
            StageId = "L1",
            StageTitle = "Identity spine",
            SessionNumber = 5,
            SessionKind = "Deliver",
            DoneCount = 3,
            TotalCount = 35,
            CurrentCheckpoint = "L1.1",
            CurrentCheckpointTitle = "SymbolId/SymbolRef/tiers",
            GateSummary = "build:OK · tests:OK",
            StageOverview = new[] { ("L0", 3, 3, "confirmed"), ("L1", 0, 5, "active") },
            StageCheckpoints = new[] { ("L1.1", "SymbolId", "TODO") },
        };
        var prompt = StatusAgent.BuildPrompt(snap, "branch: feat/loom-l1\nrecent commits:\n  abc123 do things",
            new[] { "» bash git status", "◆ build ok" }, new[] { "thinking about L1.5" });

        Assert.Contains("read-only status reporter", prompt);
        Assert.Contains("Do NOT edit files", prompt);
        Assert.Contains("L1", prompt);
        Assert.Contains("SymbolId/SymbolRef/tiers", prompt);
        Assert.Contains("build:OK", prompt);
        Assert.Contains("feat/loom-l1", prompt);
        Assert.Contains("thinking about L1.5", prompt);
    }
}
