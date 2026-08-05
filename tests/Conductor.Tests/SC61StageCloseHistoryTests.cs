using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// SC6.1, end to end: what the ENGINE leaves in git history when a stage closes. The unit tests next
/// door drive <see cref="Reporter.WriteAndPublish"/> directly; this one runs the whole orchestrator
/// against a temp repo with <c>report.commit</c> ON — the only configuration under which the defect
/// devcontext #14 recorded can happen at all — and reads the resulting log.
///
/// <para>The stand-in agent seeds two consecutive <c>chore(conductor):</c> commits of its own, so
/// anything the engine adds is separable from what was already there.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SC61StageCloseHistoryTests : IDisposable
{
    private readonly string _repo;

    public SC61StageCloseHistoryTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sc61h-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        Git.Exec(_repo, "init", "-b", "main");
        Git.Exec(_repo, "config", "user.email", "sc61@harness");
        Git.Exec(_repo, "config", "user.name", "SC61 Harness");
        Git.Exec(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# SC6.1 harness");
        WriteTracker("TODO");
        File.WriteAllText(Path.Combine(_repo, "agent.cmd"), AgentScript());
        Git.Exec(_repo, "add", "-A");
        Git.Exec(_repo, "commit", "-m", "chore: baseline");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WriteTracker(string status) => File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
        "# SC6.1 harness\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
        "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
        $"| H0.1 | the only checkpoint | {status} | | |\n");

    /// <summary>Two conductor-shaped bookkeeping commits, one real commit, then the claim — written
    /// straight into the tracker and committed, so the tree the stage closes on is clean.</summary>
    private string AgentScript() => string.Join("\r\n",
        "@echo off",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"Delivering H0.1.\"}}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
        "echo one> bookkeeping.md",
        "git add bookkeeping.md",
        "git commit -q -m \"chore(conductor): seeded one\" -- bookkeeping.md",
        "echo two>> bookkeeping.md",
        "git add bookkeeping.md",
        "git commit -q -m \"chore(conductor): seeded two\" -- bookkeeping.md",
        "echo delivered> deliverable.md",
        "git add deliverable.md",
        "git commit -q -m \"feat: harness deliverable\" -- deliverable.md",
        "powershell -NoProfile -Command \"(Get-Content TRACKER.md) -replace '\\| TODO \\|', '| DONE |' | Set-Content TRACKER.md\"",
        "git add TRACKER.md",
        "git commit -q -m \"docs: claim H0.1\" -- TRACKER.md",
        "exit /b 0",
        "");

    private List<string> Subjects() => Git.Exec(_repo, "log", "--format=%s").Output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

    /// <summary>The headline claim, measured against real history: closing a stage leaves at most ONE
    /// bookkeeping commit from the engine.
    ///
    /// <para>Before SC6.1 this rig produced four of them with byte-identical subjects — one per
    /// status transition, and three of those landed AFTER the squash meant to clean them up, because
    /// the squash ran before the final state write. Two independent causes, one visible count: the
    /// substance gate stops the status churn committing at all, and the reordering stops anything
    /// trailing the squash.</para></summary>
    [Fact]
    public async Task A_stage_close_leaves_at_most_one_engine_bookkeeping_commit()
    {
        var plan = new PlanConfig
        {
            Name = "SC61Harness",
            Repo = _repo,
            Tracker = "TRACKER.md",
            GatePolicy = "perPhase",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", Path.Combine(_repo, "agent.cmd"), "{prompt}" },
                Provider = "opencode",
            },
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "full", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = true;
        plan.Report.Push = false;
        plan.Audit = new AuditConfig { Enabled = false };

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        // NOT Once: that returns the moment the session ends, before the loop comes back round to the
        // pending phase gate — so the stage never closes and the thing under test never runs. The
        // session cap is what bounds this run instead.
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 1), consoleSink: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

        Assert.Contains("H0", state.ConfirmedStages);                       // the stage actually closed
        var subjects = Subjects();
        Assert.Contains("feat: harness deliverable", subjects);             // the agent's work survived

        var seeded = subjects.Count(s => s.StartsWith("chore(conductor): seeded", StringComparison.Ordinal));
        var fromEngine = subjects.Count(s => s.StartsWith("chore(conductor):", StringComparison.Ordinal)) - seeded;
        // Was 2 until SC6.2. The seeded pair is consecutive and the tree is dirty at the close, so
        // under SC6.1 the rebase refused and both survived; the squash that replaced it works on a
        // dirty tree and collapses them into the first one. The claim this test exists for is the
        // NEXT line — what the ENGINE left — and that arithmetic is unchanged.
        Assert.Equal(1, seeded);
        Assert.Contains("chore(conductor): seeded one", subjects);
        Assert.True(fromEngine <= 1,
            $"engine left {fromEngine} bookkeeping commits at the stage close:\n  " + string.Join("\n  ", subjects));
    }
}
