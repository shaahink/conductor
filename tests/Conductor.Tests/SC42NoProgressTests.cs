using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// SC4.2: NoProgress has to mean no progress.
///
/// Two wrong verdicts this pins down, both paid for in the field:
/// a session that delivered a checkpoint but landed no commit in THIS repo scored NoProgress
/// (sk #3, twice, in a plan written to avoid it); and a session whose only commit was conductor's
/// own <c>chore(conductor):</c> status write scored Progress (devcontext #14 — "the verdict for
/// session #2 read commits 3, of which one was the agent's work and the rest were conductor's own").
///
/// Every case here drives the REAL orchestrator over a real git repo with a stand-in agent, so the
/// assertion is on the outcome the engine recorded, not on a reading of the branch.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SC42NoProgressTests : IDisposable
{
    private readonly string _repo;

    public SC42NoProgressTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sc42-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "sc42@test");
        GitRun("config", "user.name", "SC42 Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# SC4.2 repo");
        WriteTracker(doneFirst: false);
        Commit("chore: initial commit");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception) { }
    }

    /// <summary>One argument per parameter — ProcessRunner hands each straight to ArgumentList, so a
    /// space-split command line would smuggle literal quotes into a commit message and silently
    /// leave the repo with no commits at all.</summary>
    private ProcResult GitRun(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
        return r;
    }

    private void Commit(string message)
    {
        GitRun("add", "-A");
        GitRun("commit", "-m", message, "--no-gpg-sign");
    }

    private string Head() => GitRun("rev-parse", "HEAD").Output.Trim();

    /// <summary>TWO checkpoints, so claiming one never completes the stage — without that,
    /// <c>stageComplete</c> turns the verdict green on its own and the case proves nothing.</summary>
    private void WriteTracker(bool doneFirst) => File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
        "# SC4.2 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
        "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
        $"| H0.1 | first checkpoint | {(doneFirst ? "DONE" : "TODO")} | | |\n" +
        "| H0.2 | second checkpoint | TODO | | |\n");

    private string WriteAgent(string name, params string[] body)
    {
        var path = Path.Combine(_repo, name);
        File.WriteAllText(path, string.Join("\r\n", new[]
        {
            "@echo off",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"SC4.2 stand-in agent.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
        }.Concat(body).Concat(["exit /b 0", ""])));
        return path;
    }

    private PlanConfig PlanFor(string agentScript) => new PlanConfig
    {
        Name = "SC42Plan",
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "H0", Title = "SC42", Sessions = 1 } },
        Agent = new AgentConfig
        {
            Command = "cmd.exe",
            Args = { "/c", agentScript, "{prompt}" },
            Provider = "opencode",
        },
        GatePolicy = "perSession",
        Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
    };

    private async Task<SessionRecord> RunOneSessionAsync(PlanConfig plan)
    {
        plan.Report.Commit = false;
        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);
        return Assert.Single(state.History);
    }

    /// <summary>sk #3: the checkpoint was delivered and claimed, the commit landed somewhere this
    /// repo's git log cannot see. That is progress, and the engine used to call it NoProgress.</summary>
    [Fact]
    public async Task ClaimWithoutCommits_ScoresAdvanced_NotNoProgress()
    {
        var trackerDone = Path.Combine(_repo, "tracker-done.md");
        WriteTracker(doneFirst: true);
        File.Move(Path.Combine(_repo, "TRACKER.md"), trackerDone);
        WriteTracker(doneFirst: false);
        Commit("chore: stage the claimed tracker");

        // Claims H0.1 and commits NOTHING.
        var agent = WriteAgent("claim-agent.cmd", $"copy /y \"{trackerDone}\" TRACKER.md > NUL");
        var head = Head();
        Assert.NotEmpty(head);

        var session = await RunOneSessionAsync(PlanFor(agent));

        Assert.Empty(session.NewCommits);
        Assert.Contains("H0.1", session.NewlyDone, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(SessionOutcome.Advanced, session.Outcome);
        Assert.Equal(head, Head());
    }

    /// <summary>devcontext #14: conductor's own status write is not the agent's work, and on its
    /// own it must not buy a green verdict.</summary>
    [Fact]
    public async Task OnlyBookkeepingCommit_ScoresNoProgress()
    {
        var agent = WriteAgent("chore-agent.cmd",
            "echo bookkeeping> bookkeeping.md",
            "git add bookkeeping.md",
            "git commit -m \"chore(conductor): s1 H0 running - Idle\" --no-gpg-sign -- bookkeeping.md");

        var session = await RunOneSessionAsync(PlanFor(agent));

        // History stays honest — the commit happened, it just is not progress.
        Assert.Single(session.NewCommits);
        Assert.Contains("chore(conductor):", session.NewCommits[0], StringComparison.Ordinal);
        Assert.Empty(session.NewlyDone);
        Assert.Equal(SessionOutcome.NoProgress, session.Outcome);
    }

    /// <summary>The other direction: real work sitting next to a bookkeeping commit still counts,
    /// and the fix prompt is never told about commits the verdict did not act on.</summary>
    [Fact]
    public async Task BookkeepingBesideRealWork_StillCountsTheRealCommit()
    {
        var agent = WriteAgent("mixed-agent.cmd",
            "echo bookkeeping> bookkeeping.md",
            "git add bookkeeping.md",
            "git commit -m \"chore(conductor): s1 H0 running - Idle\" --no-gpg-sign -- bookkeeping.md",
            "echo deliverable> deliverable.md",
            "git add deliverable.md",
            "git commit -m \"feat: real work\" --no-gpg-sign -- deliverable.md");

        var session = await RunOneSessionAsync(PlanFor(agent));

        Assert.Equal(2, session.NewCommits.Count);
        Assert.Equal(SessionOutcome.Progress, session.Outcome);
    }
}

/// <summary>SC4.2: the one place that decides what counts as conductor's own commit.</summary>
public sealed class BookkeepingCommitTests
{
    [Theory]
    [InlineData("a1b2c3d chore(conductor): s2 G1 GatesRed - Idle")]
    [InlineData("chore(conductor): Paused")]
    [InlineData("a1b2c3d CHORE(Conductor): shouty")]
    public void RecognisesConductorsOwnCommits(string oneline)
        => Assert.True(Git.IsBookkeepingCommit(oneline));

    [Theory]
    [InlineData("a1b2c3d feat: deliver SC4.2")]
    [InlineData("a1b2c3d chore: rig baseline")]          // a plain chore is a human's commit
    [InlineData("a1b2c3d chore(deps): bump xunit")]
    [InlineData("add cafe support")]                     // all-hex first word, but no sha to strip
    [InlineData("abc1234")]                              // bare sha, no subject
    [InlineData("")]
    public void LeavesEverythingElseAlone(string oneline)
        => Assert.False(Git.IsBookkeepingCommit(oneline));

    [Fact]
    public void ExcludeBookkeeping_KeepsOrderAndDropsOnlyConductorsOwn()
    {
        var commits = new List<string>
        {
            "1111111 feat: first",
            "2222222 chore(conductor): s1 A Advanced - Idle",
            "3333333 fix: second",
        };

        Assert.Equal(["1111111 feat: first", "3333333 fix: second"], Git.ExcludeBookkeeping(commits));
        Assert.Equal(3, commits.Count); // non-destructive: the caller's history is untouched
    }
}
