using Conductor.Core;
using Conductor.Core.Events;
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
    public void BuildRendersTimelineSectionFromTheEventLog()
    {
        // B5.1: the report's Timeline section derives from the folded event log (no parallel store).
        const string ndjson = """
        {"type":"runStarted","plan":"T","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T06:53:00Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":6,"agentSessionId":"a","seq":2,"ts":"2026-07-08T06:53:01Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","newlyDone":["S1.1"],"seq":3,"ts":"2026-07-08T06:53:06Z","runId":"r","sessionId":"1"}
        """;
        var path = Path.Combine(Path.GetTempPath(), $"conductor-rep-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, ndjson);
        try
        {
            var timeline = Timeline.Build(EventLog.ReadAll(path));
            var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
                new TrackerSnapshot(), null, null, timeline);

            Assert.Contains("## Timeline", report);
            Assert.Contains("events.jsonl", report);
            Assert.Contains("session #1 S1 Deliver started", report);
            Assert.Contains("session #1 S1 → Advanced", report);
            Assert.Contains("(5.0s)", report);           // the session span duration
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BuildOmitsTimelineSectionWhenNoEvents()
    {
        var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
            new TrackerSnapshot(), null, null, timeline: null);
        Assert.DoesNotContain("## Timeline", report);
    }

    [Fact]
    public void BuildRendersHealthSectionFromTheEventLog()
    {
        // B5.3: the report's Health section derives from the same folded event log — a stuck stage
        // (three consecutive red sessions) surfaces as a same-failure-loop flag.
        const string ndjson = """
        {"type":"stageEntered","stageId":"S1","seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":2,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"GatesRed","seq":3,"ts":"2026-07-08T10:00:20Z","runId":"r","sessionId":"1"}
        {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Fix","attempt":2,"maxAttempts":4,"seq":4,"ts":"2026-07-08T10:00:30Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"GatesRed","seq":5,"ts":"2026-07-08T10:00:40Z","runId":"r","sessionId":"2"}
        {"type":"sessionStarted","number":3,"stageId":"S1","kind":"Fix","attempt":3,"maxAttempts":4,"seq":6,"ts":"2026-07-08T10:00:50Z","runId":"r","sessionId":"3"}
        {"type":"sessionFinished","number":3,"stageId":"S1","outcome":"GatesRed","seq":7,"ts":"2026-07-08T10:01:00Z","runId":"r","sessionId":"3"}
        """;
        var path = Path.Combine(Path.GetTempPath(), $"conductor-hrep-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, ndjson);
        try
        {
            var health = HealthMetrics.Compute(EventLog.ReadAll(path));
            var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
                new TrackerSnapshot(), null, null, null, health);

            Assert.Contains("## Health", report);
            Assert.Contains("same-failure-loop", report);
            Assert.Contains("overall Alert", report);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BuildOmitsHealthSectionWhenNoSessions()
    {
        var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
            new TrackerSnapshot(), null, null, null, health: null);
        Assert.DoesNotContain("## Health", report);
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
            // SC6.1: a real change still reaches git — but it FOLDS INTO the bookkeeping commit that is
            // still the tip instead of stacking a second one beside it. What this test has always been
            // about is that history does not grow for nothing, so the assertion moved from "one more
            // commit exists" to the stronger "the new session is in the committed report", plus the
            // count staying flat because the two were coalesced.
            Assert.Equal(afterFirst, CommitCount(repo));
            Assert.Contains("| 2 | L1 | Fix |",
                Git.Exec(repo, "show", "HEAD:.conductor/REPORT.md").Output, StringComparison.Ordinal);
        }
        finally { try { TestTemp.DeleteTree(repo); } catch { } }
    }
}
