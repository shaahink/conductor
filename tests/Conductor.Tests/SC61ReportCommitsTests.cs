using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>SC6.1: pure status transitions stop landing commits, what still commits is coalesced.
/// Every test here drives a real temp git repo through the real <see cref="Reporter.WriteAndPublish"/> —
/// the defect devcontext #14 recorded is a property of the git history the engine leaves behind, and
/// nothing short of reading that history proves it.</summary>
public class SC61ReportCommitsTests
{
    private static string NewRepo()
    {
        var repo = Directory.CreateTempSubdirectory("conductor-sc61-").FullName;
        Git.Exec(repo, "init", "-b", "main");
        Git.Exec(repo, "config", "user.email", "sc61@rig");
        Git.Exec(repo, "config", "user.name", "SC61");
        Git.Exec(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "x");
        Git.Exec(repo, "add", "-A");
        Git.Exec(repo, "commit", "-m", "seed");
        return repo;
    }

    private static PlanConfig PlanIn(string repo) => new()
    {
        Name = "T",
        Repo = repo,
        Report = new ReportConfig { Commit = true, Push = false },
        Stages = { new StageConfig { Id = "G1", Title = "spine" } },
    };

    private static int CommitCount(string repo)
        => int.TryParse(Git.Exec(repo, "rev-list", "--count", "HEAD").Output.Trim(), out var n) ? n : 0;

    /// <summary>The report as GIT has it — the copy a reviewer reads, not the working-tree file.</summary>
    private static string CommittedReport(string repo)
        => Git.Exec(repo, "show", "HEAD:.conductor/REPORT.md").Output;

    private static List<string> Subjects(string repo)
        => Git.Exec(repo, "log", "--format=%s").Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

    private static RunState StateWithFinishedSession() => new()
    {
        PlanName = "T",
        CurrentStage = "G1",
        Status = RunStatus.Idle,
        History =
        {
            new SessionRecord
            {
                Number = 2, Stage = "G1", Kind = SessionKind.Deliver, Attempt = 1,
                Outcome = SessionOutcome.GatesRed, GateSummary = "1/2",
            },
        },
    };

    /// <summary>devcontext #14's exact sequence, replayed: one session's verdict written while the run
    /// walks Idle → Paused → Aborted. That produced three commits four seconds apart, all touching only
    /// REPORT.md. Only the first carries news.</summary>
    [Fact]
    public void IdleThenPausedThenAborted_lands_one_commit_not_three()
    {
        var repo = NewRepo();
        try
        {
            var plan = PlanIn(repo);
            var state = StateWithFinishedSession();
            var track = new TrackerSnapshot();
            var before = CommitCount(repo);

            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            Assert.Equal(before + 1, CommitCount(repo));   // the verdict itself: real news, one commit

            state.Status = RunStatus.Paused;
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            state.Status = RunStatus.Aborted;
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });

            Assert.Equal(before + 1, CommitCount(repo));   // two pure status transitions: NO new commits
            Assert.Single(Subjects(repo), s => s.StartsWith("chore(conductor):", StringComparison.Ordinal));
        }
        finally { TryDelete(repo); }
    }

    /// <summary>The report is still WRITTEN on every transition — this changes what is committed, not
    /// what an operator reads. A fix that stopped refreshing the file would pass the test above.</summary>
    [Fact]
    public void Uncommitted_status_transitions_still_reach_the_file_on_disk()
    {
        var repo = NewRepo();
        try
        {
            var plan = PlanIn(repo);
            var state = StateWithFinishedSession();
            var track = new TrackerSnapshot();
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });

            state.Status = RunStatus.Aborted;
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });

            var onDisk = File.ReadAllText(Reporter.ReportPath(plan));
            Assert.Contains("**Status:** Aborted", onDisk, StringComparison.Ordinal);
            // ...and the committed copy is the older one, deliberately: it is regenerable from run.db.
            Assert.Contains("**Status:** Idle",
                Git.Exec(repo, "show", "HEAD:.conductor/REPORT.md").Output, StringComparison.Ordinal);
        }
        finally { TryDelete(repo); }
    }

    /// <summary>Positive control: the fix must not silence the report commit. Every piece of delivered
    /// work still reaches git — a session concluding, a checkpoint going DONE, a stage confirmed. What
    /// changes is that a run of them with nothing in between folds into ONE commit rather than three
    /// near-identical subjects; the committed report is checked after each step to prove the folding
    /// carries the news forward rather than dropping it.</summary>
    [Fact]
    public void Delivered_work_always_reaches_the_committed_report()
    {
        var repo = NewRepo();
        try
        {
            var plan = PlanIn(repo);
            var state = StateWithFinishedSession();
            var track = new TrackerSnapshot();
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            var after1 = CommitCount(repo);

            // a second session concludes
            state.History.Add(new SessionRecord
            {
                Number = 3, Stage = "G1", Kind = SessionKind.Fix, Attempt = 2,
                Outcome = SessionOutcome.Advanced, NewlyDone = { "G1.1" },
            });
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            Assert.Contains("| 3 | G1 | Fix |", CommittedReport(repo), StringComparison.Ordinal);

            // a checkpoint flips to DONE on the board
            var withDone = new TrackerSnapshot
            {
                Checkpoints = { new CheckpointRow("G1.1", "spine", "DONE", "abc1234", "-") },
            };
            Reporter.WriteAndPublish(plan, state, withDone, null, _ => { });
            Assert.Contains("✅ DONE", CommittedReport(repo), StringComparison.Ordinal);

            // the stage is confirmed
            state.ConfirmedStages.Add("G1");
            Reporter.WriteAndPublish(plan, state, withDone, null, _ => { });
            Assert.Contains("**Confirmed phases:** G1", CommittedReport(repo), StringComparison.Ordinal);

            // ...and all three folded into the one commit that was already there.
            Assert.Equal(after1, CommitCount(repo));
            Assert.Single(Subjects(repo), s => s.StartsWith("chore(conductor):", StringComparison.Ordinal));
        }
        finally { TryDelete(repo); }
    }

    /// <summary>A session that has merely STARTED has delivered nothing, and its record churns
    /// (cost, tokens) on every heartbeat. Including it would put the every-few-seconds commit straight
    /// back.</summary>
    [Fact]
    public void A_running_session_accruing_cost_lands_no_commit()
    {
        var repo = NewRepo();
        try
        {
            var plan = PlanIn(repo);
            var state = StateWithFinishedSession();
            var track = new TrackerSnapshot();
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            var settled = CommitCount(repo);

            var settledSha = Git.Head(repo);

            var running = new SessionRecord { Number = 3, Stage = "G1", Kind = SessionKind.Deliver, Attempt = 1 };
            state.History.Add(running);
            state.Status = RunStatus.Running;
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            running.CostUsd = 0.42m;          // TotalCostUsd/TotalTokens* are derived from History,
            running.TokensInput = 90_000;     // so moving the record moves the report's cost line too
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            Assert.Equal(settled, CommitCount(repo));
            Assert.Equal(settledSha, Git.Head(repo));      // not even an amend: git was never touched

            // ...until it concludes, which is news, and reaches the committed report.
            running.Outcome = SessionOutcome.Progress;
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            Assert.NotEqual(settledSha, Git.Head(repo));
            Assert.Contains("| 3 | G1 | Deliver |", CommittedReport(repo), StringComparison.Ordinal);
        }
        finally { TryDelete(repo); }
    }

    /// <summary>Coalescing: two substantive publishes back to back leave ONE commit, because the second
    /// amends the first while it is still the tip. The count stays flat, the sha moves, and the subject
    /// is the newer one.</summary>
    [Fact]
    public void Consecutive_bookkeeping_commits_are_coalesced_into_one()
    {
        var repo = NewRepo();
        try
        {
            var plan = PlanIn(repo);
            var state = StateWithFinishedSession();
            var track = new TrackerSnapshot();
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            var afterFirst = CommitCount(repo);
            var shaFirst = Git.Head(repo);

            state.ConfirmedStages.Add("G1");
            state.Status = RunStatus.Paused;
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });

            Assert.Equal(afterFirst, CommitCount(repo));           // folded in, not stacked
            Assert.NotEqual(shaFirst, Git.Head(repo));             // ...by rewriting the tip
            Assert.Contains("Paused", Subjects(repo)[0], StringComparison.Ordinal);
            Assert.Single(Subjects(repo), s => s.StartsWith("chore(conductor):", StringComparison.Ordinal));
        }
        finally { TryDelete(repo); }
    }

    /// <summary>The amend is bounded by the sha it recorded: once the agent's own commit is the tip,
    /// the next report commit lands beside it. Amending there would rewrite the agent's work.</summary>
    [Fact]
    public void An_agent_commit_on_top_stops_the_amend()
    {
        var repo = NewRepo();
        try
        {
            var plan = PlanIn(repo);
            var state = StateWithFinishedSession();
            var track = new TrackerSnapshot();
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            var afterReport = CommitCount(repo);

            File.WriteAllText(Path.Combine(repo, "feature.txt"), "the agent's work");
            Git.Exec(repo, "add", "-A");
            Git.Exec(repo, "commit", "-m", "feat: real work");
            var agentSha = Git.Head(repo);

            state.ConfirmedStages.Add("G1");
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });

            Assert.Equal(afterReport + 2, CommitCount(repo));       // agent's commit + a fresh report commit
            Assert.Equal(agentSha, Git.Exec(repo, "rev-parse", "HEAD~1").Output.Trim());
            Assert.Contains("feat: real work", Subjects(repo));     // survived intact
        }
        finally { TryDelete(repo); }
    }

    /// <summary>Work the agent has staged but not committed must be neither swept into the amended
    /// commit nor unstaged by it — the amend is pathspec-scoped to the report.</summary>
    [Fact]
    public void An_amend_leaves_the_agents_staged_work_alone()
    {
        var repo = NewRepo();
        try
        {
            var plan = PlanIn(repo);
            var state = StateWithFinishedSession();
            var track = new TrackerSnapshot();
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });

            File.WriteAllText(Path.Combine(repo, "wip.txt"), "half-finished");
            Git.Exec(repo, "add", "wip.txt");

            state.ConfirmedStages.Add("G1");
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });

            Assert.DoesNotContain("wip.txt",
                Git.Exec(repo, "show", "--name-only", "--format=", "HEAD").Output, StringComparison.Ordinal);
            Assert.Contains("A  wip.txt", Git.Exec(repo, "status", "--porcelain").Output, StringComparison.Ordinal);
        }
        finally { TryDelete(repo); }
    }

    /// <summary>The gate is a property of the state, not of the render: two states that differ only in
    /// how the engine describes itself have the same substance, and two that differ in delivered work
    /// do not.</summary>
    [Fact]
    public void Substance_ignores_engine_self_description_and_tracks_work()
    {
        var track = new TrackerSnapshot { Checkpoints = { new CheckpointRow("G1.1", "t", "TODO", "-", "-") } };
        var a = StateWithFinishedSession();
        var baseline = ReportSubstance.Of(a, track);

        a.Status = RunStatus.Aborted;
        a.AttemptsThisStage = 4;
        a.CurrentStage = "G2";
        a.SessionCounter = 99;
        a.SetAttention("something went wrong");
        a.PendingFix = new PendingFix { FromSession = 2 };
        Assert.Equal(baseline, ReportSubstance.Of(a, track));

        var doneTrack = new TrackerSnapshot { Checkpoints = { new CheckpointRow("G1.1", "t", "DONE", "abc", "e.md") } };
        Assert.NotEqual(baseline, ReportSubstance.Of(a, doneTrack));

        a.ConfirmedStages.Add("G1");
        Assert.NotEqual(baseline, ReportSubstance.Of(a, track));
    }

    [Fact]
    public void RunState_roundtrips_the_last_committed_substance_and_sha()
    {
        var s = new RunState { PlanName = "T", LastReportSubstance = "cp:x", LastReportCommitSha = "abc1234" };
        var path = Path.Combine(Path.GetTempPath(), $"conductor-sc61-{Guid.NewGuid():N}.json");
        try
        {
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, s.PlanName);
            Assert.Equal("cp:x", loaded.LastReportSubstance);
            Assert.Equal("abc1234", loaded.LastReportCommitSha);
        }
        finally { File.Delete(path); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
