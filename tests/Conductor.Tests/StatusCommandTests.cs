using System.Text;
using System.Text.Json;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Models;
using Spectre.Console.Cli;

namespace Conductor.Tests;

public class StatusCommandTests
{
    [Fact]
    public void SettingsDefaultsWork()
    {
        var s = new StatusCommand.Settings();
        Assert.Null(s.Since);
        Assert.False(s.NoLlm);
        Assert.Null(s.Plan);
    }

    [Fact]
    public void CliPromptContainsKeyContext()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        var track = TrackerParser.Parse(SampleTracker);
        var logTail = "2026-07-09 12:00 [INFO] gate build: OK\n2026-07-09 12:01 [INFO] gate tests: OK";
        var gitSummary = "branch: feat/era-v3\nHEAD: abc1234\nrecent commits:\n  abc1234 feat(era3): D1 status";

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, logTail, gitSummary, 1, 2, null);

        Assert.Contains("Conductor-Era3", prompt);
        Assert.Contains("Idle", prompt);
        Assert.Contains("gate build: OK", prompt);
        Assert.Contains("feat/era-v3", prompt);
        Assert.Contains("abc1234", prompt);
        Assert.Contains("D1", prompt);
        Assert.Contains("D2", prompt);
    }

    [Fact]
    public void CliPromptIncludesSinceWhenSet()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        var track = TrackerParser.Parse(SampleTracker);
        var since = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, since);

        Assert.Contains("Since:", prompt);
        Assert.Contains("2026", prompt);
    }

    [Fact]
    public void CliPromptIncludesPendingFixWhenPresent()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.PendingFix = new PendingFix { FromSession = 5, GateFailures = "tests", ProgressSummary = "x" };
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, null);

        Assert.Contains("Pending fix", prompt);
        Assert.Contains("#5", prompt);
        Assert.Contains("tests", prompt);
    }

    [Fact]
    public void CliPromptIncludesPendingPhaseGateWhenPresent()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.PendingPhaseGate = new PendingPhaseGate { StageId = "D1", StageStartHead = "abc" };
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, null);

        Assert.Contains("Pending phase gate", prompt);
        Assert.Contains("D1", prompt);
    }

    [Fact]
    public void CliPromptIncludesHistory()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.History.Add(new SessionRecord
        {
            Number = 1,
            Stage = "D1",
            Kind = SessionKind.Deliver,
            Outcome = SessionOutcome.Advanced,
            NewlyDone = new List<string> { "D1" },
            NewCommits = new List<string> { "abc123" },
            GateSummary = "build:OK · tests:OK",
        });
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 1, 2, null);

        Assert.Contains("Recent sessions", prompt);
        Assert.Contains("#1", prompt);
        Assert.Contains("Deliver", prompt);
        Assert.Contains("Advanced", prompt);
        Assert.Contains("commits: 1", prompt);
    }

    [Fact]
    public void CliPromptIncludesAttentionReason()
    {
        var plan = CreateSamplePlan();
        var state = CreateSampleState();
        state.AttentionReason = "gate build failed 3 times consecutively";
        var track = TrackerParser.Parse(SampleTracker);

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", "", 0, 2, null);

        Assert.Contains("gate build failed 3 times consecutively", prompt);
    }

    [Fact]
    public void StatusAgentConfigModelDefaultIsNull()
    {
        var json = "{}";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Null(cfg.Model);
    }

    [Fact]
    public void StatusAgentConfigModelDeserializes()
    {
        var json = """{ "model": "deepseek/deepseek-chat" }""";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal("deepseek/deepseek-chat", cfg.Model);
    }

    [Fact]
    public void StatusAgentConfigMaxPerHourDefaultIs12()
    {
        var json = "{}";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(12, cfg.MaxPerHour);
    }

    [Fact]
    public void StatusAgentConfigMaxPerHourDeserializes()
    {
        var json = """{ "maxPerHour": 5 }""";
        var cfg = JsonSerializer.Deserialize<StatusAgentConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(5, cfg.MaxPerHour);
    }

    [Fact]
    public void BuildPromptStillWorksForDashboard()
    {
        var snap = new DashboardSnapshot
        {
            PlanName = "Era3",
            Status = "Running",
            StageId = "D1",
            StageTitle = "status command",
            SessionNumber = 1,
            SessionKind = "Deliver",
            DoneCount = 0,
            TotalCount = 14,
            CurrentCheckpoint = "D1",
            CurrentCheckpointTitle = "conductor status",
            GateSummary = "build:OK · tests:OK",
            StageOverview = new[] { ("D1", 0, 1, "active"), ("D2", 0, 1, "todo") },
        };
        var prompt = StatusAgent.BuildPrompt(snap, "branch: feat/era-v3\n  abc1234 feat(era3): D1",
            new[] { "bash: dotnet build" }, new[] { "thinking about status command" });

        Assert.Contains("read-only status reporter", prompt);
        Assert.Contains("Era3", prompt);
        Assert.Contains("D1", prompt);
        Assert.Contains("status command", prompt);
        Assert.Contains("build:OK", prompt);
    }

    [Fact]
    public void PlanConfigLoadsNewStatusFields()
    {
        var json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "statusAgent": { "enabled": true, "model": "deepseek/deepseek-flash", "maxPerHour": 6 }
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;

        Assert.NotNull(cfg.StatusAgent);
        Assert.True(cfg.StatusAgent.Enabled);
        Assert.Equal("deepseek/deepseek-flash", cfg.StatusAgent.Model);
        Assert.Equal(6, cfg.StatusAgent.MaxPerHour);
    }

    private static PlanConfig CreateSamplePlan()
    {
        return new PlanConfig
        {
            Name = "Conductor-Era3",
            Repo = "C:/Code/conductor-baton",
            Tracker = "TRACKER.md",
            Stages = new List<StageConfig>
            {
                new() { Id = "D1", Title = "conductor status", Sessions = 1 },
                new() { Id = "D2", Title = "conductor gate", Sessions = 1 },
            },
            Agent = new AgentConfig { Command = "opencode", Args = new List<string> { "run", "{prompt}" } },
        };
    }

    private static RunState CreateSampleState()
    {
        return new RunState
        {
            PlanName = "Conductor-Era3",
            Status = RunStatus.Idle,
            CurrentStage = "D1",
            SessionCounter = 0,
        };
    }

    private const string SampleTracker = """
        # Conductor-Era3 — Tracker

        ## Handoff
        last: Plan created. All stages TODO.
        stage: D1

        ## Checkpoints

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | D1 | conductor status | DONE | | |
        | D2 | conductor gate | TODO | | |
        """;
}
