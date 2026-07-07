using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class ReporterTests
{
    private static int CommitCount(string repo)
        => int.TryParse(Git.Exec(repo, "rev-list", "--count", "HEAD").Output.Trim(), out var n) ? n : 0;

    private static PlanConfig PlanIn(string repo) => new()
    {
        Name = "T",
        Repo = repo,
        Report = new ReportConfig { Commit = true, Push = false },
        Stages = { new StageConfig { Id = "L1", Title = "spine" } },
    };

    [Fact]
    public void BuildIncludesLiveActivitySection()
    {
        var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
            new TrackerSnapshot(), null, "**Recent actions:**\n- did a thing");
        Assert.Contains("Latest activity (live)", report);
        Assert.Contains("did a thing", report);
    }

    [Fact]
    public void TimestampOnlyRewriteDoesNotCreateDuplicateCommit()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Git.Exec(repo, "init");
            Git.Exec(repo, "config", "user.email", "t@t.dev");
            Git.Exec(repo, "config", "user.name", "t");
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "x");
            Git.Exec(repo, "add", "-A");
            Git.Exec(repo, "commit", "-m", "seed");
            var plan = PlanIn(repo);
            var state = new RunState { PlanName = "T", CurrentStage = "L1",
                History = { new SessionRecord { Number = 1, Stage = "L1", Kind = SessionKind.Deliver, Outcome = SessionOutcome.Progress } } };
            var track = new TrackerSnapshot();

            var before = CommitCount(repo);
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            var afterFirst = CommitCount(repo);
            Assert.Equal(before + 1, afterFirst);                 // first report → one commit

            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            Assert.Equal(afterFirst, CommitCount(repo));           // timestamp-only rewrite → NO new commit

            state.History.Add(new SessionRecord { Number = 2, Stage = "L1", Kind = SessionKind.Fix, Outcome = SessionOutcome.Advanced });
            Reporter.WriteAndPublish(plan, state, track, null, _ => { });
            Assert.Equal(afterFirst + 1, CommitCount(repo));       // real change → one new commit
        }
        finally { try { Directory.Delete(repo, recursive: true); } catch { } }
    }
}
