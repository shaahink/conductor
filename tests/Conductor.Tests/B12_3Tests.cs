using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Models;
using Xunit;

namespace Conductor.Tests;

public sealed class B12_3Tests
{
    // ---------------------------------------------------------------- build-prompt

    [Fact]
    public void MutatingLaneRunner_BuildPrompt_IncludesContextAndTask()
    {
        var lane = new MutatingLaneConfig
        {
            Id = "deliver-login", Kind = "delivery", Name = "Login Feature",
            Prompt = "Implement the login endpoint.",
        };
        var prompt = MutatingLaneRunner.BuildPrompt(lane, "TestPlan", "B12");

        Assert.Contains("isolated git worktree", prompt);
        Assert.Contains("merge gate", prompt);
        Assert.Contains("TestPlan", prompt);
        Assert.Contains("B12", prompt);
        Assert.Contains("Implement the login endpoint", prompt);
    }

    // ---------------------------------------------------------------- happy path: lane commits, gate passes, merge accepted

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_GoodDiff_MergeAccepted()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var plan = new PlanConfig { Repo = repo, Name = "test-plan",
                Gates = new() { new GateConfig { Name = "verify-badge", Command =
                    "if (Select-String -Path badge.txt -Pattern 'ENTERPRISE') { exit 0 } else { exit 1 }",
                    Shell = "powershell", TimeoutMinutes = 1 } } };
            var lane = new MutatingLaneConfig { Id = "add-badge", Kind = "delivery",
                Prompt = "Add ENTERPRISE badge",
                TimeoutMinutes = 1 };
            var agent = new AgentConfig { Command = "cmd",
                Args = new() { "/c", "echo ENTERPRISE>badge.txt && git add badge.txt && git commit -m feat-add-badge" } };

            var sink = new CollectingEventSink();
            var log = new List<string>();
            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", sink,
                msg => log.Add(msg), CancellationToken.None);

            Assert.True(result.IsSuccess,
                $"Lane failed: Error='{result.Error}', IsSuccess={result.IsSuccess}, Merged={result.Merged}, MergeGatePassed={result.MergeGatePassed}, AgentCommitted={result.AgentCommitted}. Log: [{string.Join(" | ", log)}]");
            Assert.True(result.Merged,
                $"MergeGate: Passed={result.MergeGatePassed}, FailureSummary={result.MergeGate?.FailureSummary}");
            Assert.True(result.MergeGatePassed);
            Assert.True(result.AgentCommitted);
            Assert.Null(result.Error);

            // Verify the lane's commit is in the primary repo
            Assert.True(File.Exists(Path.Combine(repo, "badge.txt")));
            var content = await File.ReadAllTextAsync(Path.Combine(repo, "badge.txt"));
            Assert.Contains("ENTERPRISE", content);

            // Verify events
            var started = sink.Events.OfType<MutatingLaneStarted>().ToList();
            Assert.Single(started);
            Assert.Equal("add-badge", started[0].LaneId);

            var finished = sink.Events.OfType<MutatingLaneFinished>().ToList();
            Assert.Single(finished);
            Assert.Equal("success", finished[0].Outcome);
            Assert.True(finished[0].AgentCommitted);

            var verdict = sink.Events.OfType<MergeGateVerdict>().ToList();
            Assert.Single(verdict);
            Assert.True(verdict[0].Passed);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- bad diff: gate fails, merge rejected

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_BadDiff_MergeRejected()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            // Gate checks for "ENTERPRISE" in badge.txt
            var plan = new PlanConfig { Repo = repo, Name = "test-plan",
                Gates = new() { new GateConfig { Name = "verify-badge", Command =
                    "if (Select-String -Path badge.txt -Pattern 'ENTERPRISE') { exit 0 } else { exit 1 }",
                    Shell = "powershell", TimeoutMinutes = 1 } } };
            var lane = new MutatingLaneConfig { Id = "bad-badge", Kind = "delivery",
                Prompt = "Add WRONG badge",
                TimeoutMinutes = 1 };
            // Agent writes "WRONG" not "ENTERPRISE" — gate should fail
            var agent = new AgentConfig { Command = "cmd",
                Args = new() { "/c", "echo WRONG>badge.txt && git add badge.txt && git commit -m feat-bad-badge" } };

            var sink = new CollectingEventSink();
            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", sink, null,
                CancellationToken.None);

            // The lane-level success is false because merge gate rejected
            Assert.False(result.IsSuccess);
            Assert.False(result.Merged);
            Assert.False(result.MergeGatePassed);
            Assert.True(result.AgentCommitted);
            Assert.NotNull(result.Error);
            Assert.Contains("rejected", result.Error);

            // Verify the primary repo does NOT have the bad file (no merge)
            Assert.False(File.Exists(Path.Combine(repo, "badge.txt")));

            // Verify the primary repo is still clean — only the initial commit exists
            // CommitsSince with empty string returns empty; HEAD should still be at initial commit
            var head = Git.Head(repo);
            Assert.NotNull(head);
            Assert.NotEmpty(head);

            // Verify events
            var verdict = sink.Events.OfType<MergeGateVerdict>().ToList();
            Assert.Single(verdict);
            Assert.False(verdict[0].Passed);
            Assert.Equal(1, verdict[0].FailedCount);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- agent doesn't commit — trivially successful, no merge

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_NoCommits_ReturnsSuccessWithoutMerge()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var plan = new PlanConfig { Repo = repo, Name = "test-plan" };
            var lane = new MutatingLaneConfig { Id = "noop", Kind = "delivery",
                Prompt = "Do nothing", TimeoutMinutes = 1 };
            // Agent runs but doesn't commit anything
            var agent = new AgentConfig { Command = "cmd",
                Args = new() { "/c", "echo 'nothing to do'" } };

            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", null, null,
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.False(result.Merged);
            Assert.Null(result.MergeGatePassed); // no gate was run
            Assert.False(result.AgentCommitted);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- worktree isolation: primary tree untouched during lane

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_WorktreeIsolation_PrimaryTreeUntouchedUntilMerge()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var initialHead = Git.Head(repo);
            var initialFiles = new HashSet<string>(
                Directory.GetFiles(repo, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(repo, f))
                    .Where(f => !f.StartsWith(".git" + Path.DirectorySeparatorChar)));

            var plan = new PlanConfig { Repo = repo, Name = "test-plan",
                Gates = new() { new GateConfig { Name = "verify-marker", Command =
                    "if (Select-String -Path marker.txt -Pattern 'DELIVERED') { exit 0 } else { exit 1 }",
                    Shell = "powershell", TimeoutMinutes = 1 } } };
            var lane = new MutatingLaneConfig { Id = "marker", Kind = "delivery",
                Prompt = "Add marker", TimeoutMinutes = 1 };
            var agent = new AgentConfig { Command = "cmd",
                Args = new() { "/c", "echo DELIVERED>marker.txt && git add marker.txt && git commit -m feat-marker" } };

            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", null, null,
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.True(result.Merged);
            Assert.True(File.Exists(Path.Combine(repo, "marker.txt")));

            // After merge, the primary tree has changed (new file from lane)
            var finalFiles = Directory.GetFiles(repo, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(repo, f))
                .Where(f => !f.StartsWith(".git" + Path.DirectorySeparatorChar));
            Assert.Contains("marker.txt", finalFiles);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- merge gate with lane-specific gates falls back to plan gates

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_UsesLaneSpecificMergeGates_WhenConfigured()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            // Plan-level gate that would fail
            var plan = new PlanConfig { Repo = repo, Name = "test-plan",
                Gates = new() { new GateConfig { Name = "always-fail", Command = "exit 1",
                    Shell = "powershell", TimeoutMinutes = 1 } } };
            // Lane-specific gate that passes
            var lane = new MutatingLaneConfig { Id = "lane-gates", Kind = "delivery",
                Prompt = "Add file", TimeoutMinutes = 1,
                MergeGates = new() { new GateConfig { Name = "always-pass", Command = "exit 0",
                    Shell = "powershell", TimeoutMinutes = 1 } } };
            var agent = new AgentConfig { Command = "cmd",
                Args = new() { "/c", "echo ok > result.txt && git add result.txt && git commit -m feat-result" } };

            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", null, null,
                CancellationToken.None);

            // Should pass because lane-specific gate (always-pass) is used instead of plan gate (always-fail)
            Assert.True(result.IsSuccess);
            Assert.True(result.Merged);
            Assert.True(result.MergeGatePassed);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- merge conflict → rejected

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_MergeConflict_Rejected()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            // Create a file so the lane can meaningfully interact with the repo
            await File.WriteAllTextAsync(Path.Combine(repo, "version.txt"), "v1");
            Git.Exec(repo, "add", "version.txt");
            Git.Exec(repo, "commit", "-m", "add version file");

            // Plan has a gate that checks for "v2" but lane writes "v1-unchanged"
            var plan = new PlanConfig { Repo = repo, Name = "test-plan",
                Gates = new() { new GateConfig { Name = "check-version", Command =
                    "if (Select-String -Path version.txt -Pattern 'v2') { exit 0 } else { exit 1 }",
                    Shell = "powershell", TimeoutMinutes = 1 } } };
            var lane = new MutatingLaneConfig { Id = "no-change", Kind = "delivery",
                Prompt = "Leave version unchanged", TimeoutMinutes = 1 };
            // Agent doesn't change the file — gate checking for v2 should fail
            var agent = new AgentConfig { Command = "cmd",
                Args = new() { "/c", "echo v1-unchanged>version.txt && git add version.txt && git commit -m no-change" } };

            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", null, null,
                CancellationToken.None);

            // Gate should fail because version.txt doesn't contain "v2"
            Assert.False(result.IsSuccess);
            Assert.False(result.Merged);
            Assert.False(result.MergeGatePassed);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- lane cancelled via token

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_Cancellation_ReturnsCancelled()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var plan = new PlanConfig { Repo = repo, Name = "test-plan" };
            var lane = new MutatingLaneConfig { Id = "cancel", Kind = "delivery",
                Prompt = "Long task", TimeoutMinutes = 5 };
            var agent = new AgentConfig { Command = "powershell",
                Args = new() { "-NoProfile", "-Command", "Start-Sleep -Seconds 120" } };

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", null, null,
                cts.Token);

            Assert.Contains("cancelled", result.Error);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- worktree is cleaned up after completion

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MutatingLane_WorktreeCleanedUp_AfterCompletion()
    {
        var startedUtc = DateTime.UtcNow;
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var plan = new PlanConfig { Repo = repo, Name = "test-plan",
                Gates = new() { new GateConfig { Name = "verify-marker", Command =
                    "if (Select-String -Path marker.txt -Pattern 'DONE') { exit 0 } else { exit 1 }",
                    Shell = "powershell", TimeoutMinutes = 1 } } };
            var lane = new MutatingLaneConfig { Id = "cleanup", Kind = "delivery",
                Prompt = "Add marker", TimeoutMinutes = 1 };
            var agent = new AgentConfig { Command = "cmd",
                Args = new() { "/c", "echo DONE>marker.txt && git add marker.txt && git commit -m feat-marker" } };

            var result = await MutatingLaneRunner.RunAsync(plan, lane, agent, "B12", null, null,
                CancellationToken.None);

            Assert.True(result.IsSuccess);

            // Verify no lane-related branches remain in the repo
            var branches = Git.Exec(repo, "branch").Output;
            Assert.DoesNotContain("conductor-lane-cleanup", branches);
            Assert.DoesNotContain("conductor-staging-cleanup", branches);

            // Verify THIS lane leaked no temp dirs. The runner names them
            // `conductor-{mutating,mergegate}-{lane.Id}-{suffix}`, so scoping to this lane's OWN id
            // is what makes the assertion about this lane. Scoping by creation time alone was not
            // enough: the global temp also holds leftovers from sibling tests (the cancellation test
            // can legitimately abandon a worktree when its 500ms cancel races the clone), and under a
            // full parallel battery those siblings run DURING this test, not before it — the known
            // "passes in isolation, fails once in ~8 full runs" flake.
            var tempRoot = Path.GetTempPath();
            var leakedDirs = Directory.GetDirectories(tempRoot, $"conductor-mutating-{lane.Id}-*")
                .Concat(Directory.GetDirectories(tempRoot, $"conductor-mergegate-{lane.Id}-*"))
                .Where(d => Directory.GetCreationTimeUtc(d) >= startedUtc)
                .ToList();
            Assert.Empty(leakedDirs);
        }
        finally { cleanup(); }
    }

    // ---------------------------------------------------------------- MutatingLaneConfig defaults

    [Fact]
    public void MutatingLaneConfig_Defaults()
    {
        var lane = new MutatingLaneConfig();
        Assert.Equal("", lane.Id);
        Assert.Equal("delivery", lane.Kind);
        Assert.Equal(30, lane.TimeoutMinutes);
        Assert.True(lane.Enabled);
        Assert.Null(lane.StageTrigger);
        Assert.Null(lane.Agent);
        Assert.Null(lane.MergeGates);
    }

    // ---------------------------------------------------------------- PlanConfig.MutatingLanes defaults empty

    [Fact]
    public void PlanConfig_MutatingLanes_DefaultsEmpty()
    {
        var plan = new PlanConfig();
        Assert.NotNull(plan.MutatingLanes);
        Assert.Empty(plan.MutatingLanes);
    }

    // ---------------------------------------------------------------- MutatingLaneResult states

    [Fact]
    public void MutatingLaneResult_IsSuccess_ReflectsMergeGate()
    {
        var success = new MutatingLaneResult { LaneId = "s", Kind = "delivery",
            Merged = true, MergeGatePassed = true };
        Assert.True(success.IsSuccess);

        var rejected = new MutatingLaneResult { LaneId = "r", Kind = "delivery",
            Merged = false, MergeGatePassed = false, Error = "merge gate rejected" };
        Assert.False(rejected.IsSuccess);

        var noCommit = new MutatingLaneResult { LaneId = "n", Kind = "delivery",
            Merged = false, MergeGatePassed = null, AgentCommitted = false };
        Assert.True(noCommit.IsSuccess); // no commits = no merge needed = success

        var error = new MutatingLaneResult { LaneId = "e", Kind = "delivery",
            Error = "worktree creation failed" };
        Assert.False(error.IsSuccess);
    }

    // ---------------------------------------------------------------- helper: create a real git repo with one initial commit

    private static (string repoPath, Action cleanup) CreateTestRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-b12_3-test-{Guid.NewGuid():N}"[..42]);
        Directory.CreateDirectory(repo);

        Git.Exec(repo, "init", "-b", "main");

        // Set git user for commits (CI-safe)
        Git.Exec(repo, "config", "user.email", "conductor@test.local");
        Git.Exec(repo, "config", "user.name", "Conductor Test");

        // Create an initial file so the repo has a base commit
        File.WriteAllText(Path.Combine(repo, "README.md"), "# Test Repo\n");
        Git.Exec(repo, "add", "README.md");
        Git.Exec(repo, "commit", "-m", "initial commit");

        void Cleanup()
        {
            try
            {
                // Clear read-only flag on .git objects (Windows git may set these)
                var gitDir = Path.Combine(repo, ".git");
                if (Directory.Exists(gitDir))
                {
                    foreach (var f in Directory.GetFiles(gitDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                }
                Directory.Delete(repo, recursive: true);
            }
            catch { /* best effort */ }
        }

        return (repo, Cleanup);
    }
}
