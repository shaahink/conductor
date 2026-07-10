using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Models;
using Conductor.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conductor.Tests;

/// <summary>
/// F0.3 integration harness: fake agent + temp repo, full orchestration cycle.
/// Proves the orchestrator can: pick a stage, spawn a session, parse agent output,
/// run gates, evaluate outcome, and record the session.
///
/// The fake agent is a batch file that writes opencode-format JSON to stdout and
/// creates a deliverable file. Git commits are verified via direct inspection after the run.
/// </summary>
public sealed class HarnessTests : IDisposable
{
    private readonly string _repo;
    private readonly string _stateDir;
    private readonly string _agentScript;
    private readonly Action _cleanup;

    public HarnessTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        _stateDir = Path.Combine(_repo, ".conductor");

        GitRun("init -b main");
        GitRun("config user.email harness@test");
        GitRun("config user.name \"Harness Test\"");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# Harness Test Repo");
        GitRun("add README.md");
        GitRun("commit -m \"chore: initial commit\" --no-gpg-sign");
        WriteTracker();

        _agentScript = Path.Combine(_repo, "fake-agent.cmd");
        File.WriteAllText(_agentScript, FakeAgentScript());

        _cleanup = () =>
        {
            try { Directory.Delete(_repo, recursive: true); }
            catch (Exception) { }
        };
    }

    public void Dispose() => _cleanup();

    private ProcResult GitRun(string args) =>
        Conductor.Core.ProcessRunner.Run("git", args.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            _repo, TimeSpan.FromSeconds(30), CancellationToken.None);

    private void WriteTracker() => File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
        "# Harness Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
        "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
        "| H0.1 | harness checkpoint | TODO | | |\n");

    private static string FakeAgentScript() => string.Join("\r\n",
        "@echo off",
        "echo {\"type\":\"step_start\"}",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"Delivering harness checkpoint H0.1.\"}}",
        "echo {\"type\":\"tool_use\",\"part\":{\"tool\":\"Write\",\"state\":{\"title\":\"Write harness-output.txt\",\"input\":\"{}\"}}}",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"Created deliverable. Committing.\"}}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":350,\"output\":120,\"reasoning\":80,\"cache\":{\"read\":0}}}}",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"Session complete.\"}}",
        "echo harness done> harness-output.txt",
        // Agent also commits to prove full cycle: deliverable + git commit
        "git add harness-output.txt",
        "git commit -m \"feat: deliver harness checkpoint\"",
        "exit /b 0",
        "");

    [Fact]
    public async Task FullCycle_FakeAgent_RecordsSessionAndParsesOutput()
    {
        var plan = new PlanConfig
        {
            Name = "HarnessPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", _agentScript, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates =
            {
                new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 },
            },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        var statePath = Path.Combine(_stateDir, "state.json");

        using var host = ConductorHost.Build(plan, state, statePath, new PlainSink(), NullEventSink.Instance,
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        var code = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(0, code);

        var loadedState = RunState.LoadOrNew(statePath, plan.Name);
        Assert.Single(loadedState.History);

        var session = loadedState.History[0];
        Assert.Equal(SessionKind.Deliver, session.Kind);
        Assert.Equal("H0", session.Stage);
        Assert.NotNull(session.EndedUtc);
        Assert.NotNull(session.Outcome);

        // Agent output was parsed — summary and cost/tokens populated
        Assert.NotNull(session.ResultSummary);
        Assert.Contains("Delivering harness", session.ResultSummary, StringComparison.Ordinal);
        Assert.True(session.CostUsd > 0, $"CostUsd=${session.CostUsd}");
        Assert.True(session.TokensInput > 0, $"TokensInput={session.TokensInput}");

        // Agent created a deliverable file on disk
        Assert.True(File.Exists(Path.Combine(_repo, "harness-output.txt")));

        // Git commit made by the agent must be visible in the repo
        var log = GitRun("log --oneline -3").Output.Trim();
        Assert.Contains("feat: deliver harness checkpoint", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullCycle_DryRun_DoesNotModifyRepo()
    {
        var plan = new PlanConfig
        {
            Name = "DryRunPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", _agentScript, "{prompt}" },
                Provider = "opencode",
            },
        };

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        var statePath = Path.Combine(_stateDir, "state.json");

        using var host = ConductorHost.Build(plan, state, statePath, new PlainSink(), NullEventSink.Instance,
            new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false);

        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        var code = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(0, code);

        var loadedState = RunState.LoadOrNew(statePath, plan.Name);
        Assert.Empty(loadedState.History);
        Assert.False(File.Exists(Path.Combine(_repo, "harness-output.txt")));
    }
}
