using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// SC6.2, end to end: what the ENGINE leaves in git history when a stage closes on a DIRTY tree.
/// This is the configuration every real stage close runs in — the engine rewrites the tracker and the
/// report after the agent has committed them, so the tree is never clean at the close — and it is the
/// one the old squash could not survive: <c>git rebase</c> refuses to start on a dirty tree, which is
/// devcontext #20's "failed at 4 of 6 stage closes" with git's reason discarded.
///
/// <para>The stand-in agent seeds two consecutive <c>chore(conductor):</c> commits and then leaves
/// uncommitted work of all three shapes behind, so both halves of the claim are measured on the same
/// run: the history collapses, and the agent's work is still there afterwards.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SC62StageCloseSquashTests : IDisposable
{
    private readonly string _repo;

    public SC62StageCloseSquashTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sc62h-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        Git.Exec(_repo, "init", "-b", "main");
        Git.Exec(_repo, "config", "user.email", "sc62@harness");
        Git.Exec(_repo, "config", "user.name", "SC62 Harness");
        Git.Exec(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# SC6.2 harness");
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "committed content\n");
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
        "# SC6.2 harness\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
        "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
        $"| H0.1 | the only checkpoint | {status} | | |\n");

    /// <summary>Two consecutive bookkeeping commits for the squash to collapse, one real commit, the
    /// claim — and then the three shapes of uncommitted work a live agent leaves behind.</summary>
    private static string AgentScript() => string.Join("\r\n",
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
        // Left dirty on purpose: unstaged, staged-but-uncommitted, and untracked.
        "echo edited but never staged>> tracked.txt",
        "echo staged never committed> staged.txt",
        "git add staged.txt",
        "echo untracked> untracked.txt",
        "exit /b 0",
        "");

    private List<string> Subjects() => Git.Exec(_repo, "log", "--format=%s").Output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

    /// <summary>The whole of SC6.2 in one run: the stage closes with the tree dirty, the two seeded
    /// bookkeeping commits become one, and every uncommitted file is exactly as the agent left it.
    /// Before this change the same rig logged <c>git rebase returned non-zero</c> and left both.</summary>
    [Fact]
    public async Task A_stage_close_on_a_dirty_tree_squashes_and_keeps_the_uncommitted_work()
    {
        var plan = new PlanConfig
        {
            Name = "SC62Harness",
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
        // MaxSessions, not Once: Once returns before the loop comes back round to the pending phase
        // gate, so the stage never closes and the thing under test never runs (SC6.1's finding).
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 1), consoleSink: false);
        // The loop keeps re-confirming a finished stage until something stops it; the squash lands
        // within seconds of the close, so the cap is a bound on the rig, not on the thing measured.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

        Assert.Contains("H0", state.ConfirmedStages);                       // the stage actually closed
        Assert.True(Git.IsDirty(_repo), "the rig is pointless unless the tree was dirty at the close");

        var subjects = Subjects();
        Assert.Contains("feat: harness deliverable", subjects);             // the agent's commits survived
        Assert.Contains("docs: claim H0.1", subjects);
        var seeded = subjects.Count(s => s.StartsWith("chore(conductor): seeded", StringComparison.Ordinal));
        Assert.Equal(1, seeded);                                            // two collapsed into one
        Assert.Contains("chore(conductor): seeded one", subjects);          // the FIRST message survives
        Assert.Contains("H0", state.SquashedStages);

        // ...and the uncommitted work is untouched, which is what makes the dirty-tree squash safe
        // rather than merely possible.
        // No cts.Token here: it is spent by the time the run returns, and a cancelled token would
        // fail this read for a reason that has nothing to do with the claim.
        var tracked = await File.ReadAllTextAsync(Path.Combine(_repo, "tracked.txt"), CancellationToken.None);
        Assert.EndsWith("edited but never staged\r\n", tracked, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_repo, "untracked.txt")));
        var status = Git.Exec(_repo, "status", "--porcelain").Output;
        Assert.Contains("A  staged.txt", status, StringComparison.Ordinal);  // still staged, not swept in
        Assert.Contains("?? untracked.txt", status, StringComparison.Ordinal);
        Assert.Contains(" M tracked.txt", status, StringComparison.Ordinal);
    }
}
