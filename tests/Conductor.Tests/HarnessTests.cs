using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// F0.3 integration harness: fake agent + temp repo, full orchestration cycle.
/// Proves the orchestrator can: pick a stage, spawn a session, parse agent output,
/// run gates, evaluate outcome, and record the session.
///
/// The fake agent is a batch file that writes opencode-format JSON to stdout and
/// creates a deliverable file. Git commits are verified via direct inspection after the run.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class HarnessTests : IDisposable
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

        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "harness@test");
        GitRun("config", "user.name", "Harness Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# Harness Test Repo");
        GitRun("add", "README.md");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");
        WriteTracker();

        _agentScript = Path.Combine(_repo, "fake-agent.cmd");
        File.WriteAllText(_agentScript, FakeAgentScript());

        _cleanup = () =>
        {
            try { TestTemp.DeleteTree(_repo); }
            catch (Exception) { }
        };
    }

    public void Dispose() => _cleanup();

    /// <summary>SF0.2 (bug #8): one argument per parameter, and the exit code is ASSERTED.
    /// The old helper took a single string and split it on spaces before handing the pieces to
    /// <see cref="Conductor.Core.ProcessRunner"/>'s ArgumentList, so
    /// <c>commit -m "chore: initial commit"</c> reached git as five arguments — message truncated to
    /// a literal quote, the remaining words read as pathspecs. It failed, nothing checked the exit
    /// code, and the harness repo was left with ZERO commits: <c>Git.Head</c> returned "",
    /// <c>Git.CommitsSince(repo, "")</c> short-circuited, and every harness assertion about
    /// <c>NewCommits</c> was vacuously true. Same pattern as <c>SC42NoProgressTests.GitRun</c>.</summary>
    private ProcResult GitRun(params string[] args)
    {
        var r = Conductor.Core.ProcessRunner.Run("git", args,
            _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
        return r;
    }

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

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        var code = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(0, code);

        // M2: verify via in-memory RunState (modified by orchestrator) — no longer reads state.json
        Assert.Single(state.History);

        var session = state.History[0];
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
        var log = GitRun("log", "--oneline", "-3").Output.Trim();
        Assert.Contains("feat: deliver harness checkpoint", log, StringComparison.Ordinal);

        // SF0.2 (bug #8): the assertion the vacuous helper hid. With no initial commit the repo had
        // zero commits, the session's start head was "", and CommitsSince short-circuited to empty —
        // so the fake agent's commit was invisible to the verdict that is supposed to grade it.
        Assert.Single(session.NewCommits);
        Assert.Contains("feat: deliver harness checkpoint", session.NewCommits[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullCycle_StartPaused_IdlesUntilResume_ThenRunsSessionOne()
    {
        var plan = new PlanConfig
        {
            Name = "PausedPlan",
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

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0, StartPaused: true), consoleSink: false);

        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        var runTask = orchestrator.RunAsync(CancellationToken.None);

        // G3.1 gate, part 1: the engine comes up parked — Paused status, zero sessions spawned.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(RunStatus.Paused, state.Status);

        // Hold the pause a few idle cycles to prove it does not self-start.
        await Task.Delay(2000);
        Assert.Empty(state.History);
        Assert.False(runTask.IsCompleted);

        // G3.1 gate, part 2: a resume (same verb the Face palette / CLI send) starts session 1.
        var inbox = host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>();
        inbox.Enqueue(ControlCommand.Of(ControlAction.ResumeRun));

        var code = await runTask.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, code);
        Assert.Single(state.History);
        Assert.Equal(SessionKind.Deliver, state.History[0].Kind);
    }

    [Fact]
    public async Task FullCycle_RoleModelAndMultiItemClaim_ReachTheRealSession()
    {
        // P1 live proof: pipeline rules assign deliver → a distinct model, and multi-item claims two
        // sibling checkpoints. The MODEL must reach the spawned agent's real process args ({model}
        // placeholder) and the PROMPT on disk must name both claimed items.
        await File.WriteAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "# Harness Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| H0.1 | first harness checkpoint | TODO | | |\n" +
            "| H0.2 | second harness checkpoint | TODO | | |\n");
        var modelEcho = Path.Combine(_repo, "model-echo.cmd");
        await File.WriteAllTextAsync(modelEcho, string.Join("\r\n",
            "@echo off",
            "echo %1> model-seen.txt",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"noop.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "exit /b 0",
            ""));

        var plan = new PlanConfig
        {
            Name = "AssignmentPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", modelEcho, "{model}", "{prompt}" },
                Provider = "opencode",
            },
            Pipeline = new PipelineRules
            {
                Roles = new Dictionary<string, RoleAgentRule>(StringComparer.Ordinal)
                {
                    ["deliver"] = new() { Model = "role-override-model" },
                },
                MultiItem = new MultiItemRule { Enabled = true, MaxItems = 2 },
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);

        // The role model reached the REAL process: the fake agent echoed its first arg ({model}).
        var seen = await File.ReadAllTextAsync(Path.Combine(_repo, "model-seen.txt"));
        Assert.Contains("role-override-model", seen, StringComparison.Ordinal);

        // The prompt on disk names BOTH claimed items.
        var promptMd = await File.ReadAllTextAsync(Path.Combine(_stateDir, "logs", "session-001.prompt.md"));
        Assert.Contains("Claimed items this session", promptMd, StringComparison.Ordinal);
        Assert.Contains("H0.1", promptMd, StringComparison.Ordinal);
        Assert.Contains("H0.2", promptMd, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullCycle_ConflictingDeclaredTaskPaths_BlockTheMultiItemClaim()
    {
        // PF3 live proof: the same multi-item setup the P1 test proves claims BOTH checkpoints —
        // except here each checkpoint's open task card DECLARES an overlapping path (differing only
        // in case, which the policy's normalization must still collide). The policy refuses the
        // co-claim, so the real prompt on disk carries no "Claimed items" section: real task data,
        // not plan config, gated the claim.
        await File.WriteAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "# Harness Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| H0.1 | first harness checkpoint | TODO | | |\n" +
            "| H0.2 | second harness checkpoint | TODO | | |\n");

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
            Pipeline = new PipelineRules { MultiItem = new MultiItemRule { Enabled = true, MaxItems = 2 } },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        // Seed REAL task data before the run — the exact events the Kanban card editor writes.
        var store = host.Services.GetRequiredService<IRunStore>();
        store.AppendEvent(new TaskAdded { RunId = state.RunId, TaskId = "H0.1-a1", CheckpointId = "H0.1", Title = "First card", Order = 1, Source = "human" });
        store.AppendEvent(new TaskAdded { RunId = state.RunId, TaskId = "H0.2-a1", CheckpointId = "H0.2", Title = "Second card", Order = 1, Source = "human" });
        store.AppendEvent(new TaskDetailEdited { RunId = state.RunId, TaskId = "H0.1-a1", Paths = ["src/shared.cs"] });
        store.AppendEvent(new TaskDetailEdited { RunId = state.RunId, TaskId = "H0.2-a1", Paths = ["SRC/Shared.cs"] });
        store.FlushEvents();

        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);

        var promptMd = await File.ReadAllTextAsync(Path.Combine(_stateDir, "logs", "session-001.prompt.md"));
        Assert.DoesNotContain("Claimed items this session", promptMd, StringComparison.Ordinal);
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

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false);

        var orchestrator = host.Services.GetRequiredService<Orchestrator>();
        var code = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(0, code);

        // M2: verify via in-memory RunState — dry run produces no sessions
        Assert.Empty(state.History);
        Assert.False(File.Exists(Path.Combine(_repo, "harness-output.txt")));
    }

    /// <summary>SC3.3 live proof: a template the plan supplies with an unresolvable placeholder used
    /// to throw out of the run loop, out of the process and onto stderr — nothing in conductor.log,
    /// no state written, and `status` still calling the dead run idle. Now the run PARKS: NeedsHuman,
    /// the refusal named on the attention surface and in conductor.log, the engine still up, and the
    /// session number not burned — so the operator fixes the template and resumes into the same run.
    /// Driven through the real orchestrator against the real repo, not by calling the renderer.</summary>
    [Fact]
    public async Task FullCycle_UnresolvablePlaceholder_ParksTheRunAndSaysSoInTheLog()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "tpl"));
        await File.WriteAllTextAsync(Path.Combine(_repo, "tpl", "session.md"),
            "Deliver stage {stage} of {planName} in {repo}.\nBudget for this stage: {stageBudget}\n{stageNotes}");

        var plan = new PlanConfig
        {
            Name = "ParkPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            TemplatesDir = "tpl",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", _agentScript, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.PlanFilePath = Path.Combine(_repo, "conductor.plan.json");
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        using var cts = new CancellationTokenSource();
        var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (state.Status != RunStatus.NeedsHuman && !runTask.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(50, cts.Token);

        // The park, not a crash: the loop is still running and the process is still ours.
        Assert.Equal(RunStatus.NeedsHuman, state.Status);
        Assert.False(runTask.IsCompleted, "a parked run stays up — the operator fixes the template and resumes");
        Assert.NotNull(state.AttentionReason);
        Assert.Contains("{stageBudget}", state.AttentionReason!, StringComparison.Ordinal);
        Assert.Contains("H0", state.AttentionReason!, StringComparison.Ordinal);

        // No session was spawned and no session number was burned.
        Assert.Empty(state.History);
        Assert.Equal(0, state.SessionCounter);
        Assert.False(File.Exists(Path.Combine(_repo, "harness-output.txt")));

        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        // The refusal reached the log an operator actually reads.
        var log = await File.ReadAllTextAsync(Path.Combine(_stateDir, "conductor.log"), CancellationToken.None);
        Assert.Contains("NEEDS HUMAN", log, StringComparison.Ordinal);
        Assert.Contains("{stageBudget}", log, StringComparison.Ordinal);
        Assert.Contains("session.md", log, StringComparison.Ordinal);
    }

    /// <summary>SC7.1 live proof, driven through the real orchestrator and a real agent process
    /// speaking claude's <c>stream-json</c>.
    ///
    /// The agent makes three tool calls whose arguments are shaped exactly like the ones the old
    /// capture destroyed: a Write whose 400-character <c>content</c> comes BEFORE its
    /// <c>file_path</c> (so <c>Trunc(rawJson, 150)</c> cut the path away entirely), a Bash with a long
    /// description ahead of its command, and an Edit inside the repo. It really does write the
    /// out-of-repo file.
    ///
    /// What must be true afterwards: transcript.jsonl holds schema-v2 lines carrying the WHOLE path
    /// and the WHOLE command; the session record names the out-of-repo write and only that one; and
    /// the verdict says so in conductor.log.</summary>
    [Fact]
    public async Task FullCycle_StructuredToolEvents_ReachTheTranscriptAndTheVerdict()
    {
        var outsideFile = Path.Combine(Path.GetTempPath(), $"sc71-outside-{Guid.NewGuid():N}.txt");
        var outsideJson = outsideFile.Replace("\\", "\\\\", StringComparison.Ordinal);
        var body = new string('z', 400);
        var longPurpose = "probe the toolchain before touching anything, because the last three attempts " +
                          "died on a missing SDK and nobody could tell from the transcript which command ran";
        var claudeAgent = Path.Combine(_repo, "fake-claude.cmd");
        await File.WriteAllTextAsync(claudeAgent, string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"system\",\"subtype\":\"init\"}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"," +
                $"\"input\":{{\"description\":\"{longPurpose}\",\"command\":\"dotnet build Conductor.slnx -clp:ErrorsOnly\"}}}}]}}}}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m2\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Write\"," +
                $"\"input\":{{\"content\":\"{body}\",\"file_path\":\"{outsideJson}\"}}}}]}}}}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m3\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"," +
                "\"input\":{\"file_path\":\"README.md\",\"old_string\":\"a\",\"new_string\":\"a\\nb\"}}]}}",
            $"echo {body}>\"{outsideFile}\"",
            "echo harness done> harness-output.txt",
            "git add harness-output.txt",
            "git commit -m \"feat: deliver harness checkpoint\"",
            "echo {\"type\":\"result\",\"total_cost_usd\":0.01,\"num_turns\":3," +
                "\"result\":\"SESSION-RESULT: probed and wrote.\",\"usage\":{\"input_tokens\":120,\"output_tokens\":40}}",
            "exit /b 0",
            ""));

        var plan = new PlanConfig
        {
            Name = "StructuredPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", claudeAgent, "{prompt}" },
                Provider = "claude",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        try
        {
            var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
            Assert.Equal(0, code);
            Assert.Single(state.History);

            // ── the transcript holds STRUCTURE, at schema v2 ──
            var lines = TranscriptLog.ReadAll(Path.Combine(_stateDir, "transcript.jsonl"));
            var tools = lines.Where(l => l.Kind == "tool").ToList();
            Assert.Equal(3, tools.Count);
            Assert.All(tools, l => Assert.Equal(2, l.V));
            Assert.All(tools, l => Assert.NotNull(l.Tool));

            var write = tools.Single(l => l.Tool!.Name == "Write").Tool!;
            // The WHOLE path — the old capture cut 400 characters of content before ever reaching it.
            Assert.Equal(outsideFile, write.Field("path"));
            Assert.Equal("400", write.Field("bytes"));

            var bash = tools.Single(l => l.Tool!.Name == "Bash").Tool!;
            Assert.Equal("dotnet build Conductor.slnx -clp:ErrorsOnly", bash.Field("command"));
            Assert.Equal(longPurpose, bash.Field("purpose"));

            var edit = tools.Single(l => l.Tool!.Name == "Edit").Tool!;
            Assert.Equal("README.md", edit.Field("path"));
            Assert.Equal("2", edit.Field("linesAdded"));

            // ── the verdict knows where the session wrote ──
            var session = state.History[0];
            var stray = Assert.Single(session.OutsideRepoWrites);
            Assert.Equal(outsideFile, stray);
            // The in-repo Edit is not a stray, and the agent really did write the outside file.
            Assert.DoesNotContain(session.OutsideRepoWrites, p => p.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(outsideFile), "the fake agent's out-of-repo write must be real");

            var log = await File.ReadAllTextAsync(Path.Combine(_stateDir, "conductor.log"), CancellationToken.None);
            Assert.Contains("note: 1 file(s) written outside the repo", log, StringComparison.Ordinal);
            Assert.Contains(outsideFile, log, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(outsideFile); } catch (IOException) { }
        }
    }
}
