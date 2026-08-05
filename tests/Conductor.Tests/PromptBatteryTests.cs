using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public sealed class PromptBatteryTests : IDisposable
{
    private readonly string _tmpDir;

    public PromptBatteryTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"conductor-battery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tmpDir, ".conductor"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) TestTemp.DeleteTree(_tmpDir); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    [Fact]
    public void LessonsBatteryInjectsRecentLessons()
    {
        var lessonsDir = Path.Combine(_tmpDir, ".conductor");
        var mgr = new LessonsManager(lessonsDir);
        // K1.3: rule-shaped fixture text, because the file holds rules now rather than narrative.
        // The assertions are unchanged — this measures that the battery is non-empty and carries the
        // newest rule with its source tag.
        mgr.Append("B8", 1, "Never patch concurrency blindly without understanding the root cause.");
        mgr.Append("B7", 45, "Never poll ReadAll while an async drain is still running: it races.");

        var battery = new LessonsBattery(mgr);
        Assert.Equal("lessons", battery.Name);
        Assert.False(battery.IsEmpty);
        Assert.Contains("while an async drain is still running", battery.Section);
        Assert.Contains("B7-45", battery.Section);
    }

    [Fact]
    public void LessonsBatteryEmptyWhenNoFile()
    {
        var lessonsDir = Path.Combine(_tmpDir, ".conductor");
        var mgr = new LessonsManager(lessonsDir);
        var battery = new LessonsBattery(mgr);

        Assert.True(battery.IsEmpty);
        Assert.Equal("", battery.Section);
    }

    [Fact]
    public void RecentFailureBatteryEmptyWhenAllGreen()
    {
        var state = new RunState();
        state.History.Add(new SessionRecord
        {
            Number = 1, Stage = "B0", Outcome = SessionOutcome.Advanced,
            GateSummary = "build: PASS, tests: PASS"
        });

        var battery = new RecentFailureBattery(state);
        Assert.True(battery.IsEmpty, "should be empty when all sessions succeeded");
    }

    [Fact]
    public void RecentFailureBatteryShowsLastRedSession()
    {
        var state = new RunState();
        state.History.Add(new SessionRecord
        {
            Number = 1, Stage = "B0", Outcome = SessionOutcome.Advanced,
            GateSummary = "build: PASS, tests: PASS"
        });
        state.History.Add(new SessionRecord
        {
            Number = 2, Stage = "B1", Outcome = SessionOutcome.GatesRed,
            GateSummary = "build: PASS, tests: FAIL (3 errors)",
            ResultSummary = "SESSION-RESULT: attempted B1.2 but tests regressed — 3 tests now failing."
        });
        state.History.Add(new SessionRecord
        {
            Number = 3, Stage = "B1", Outcome = SessionOutcome.Advanced,
            GateSummary = "build: PASS, tests: PASS"
        });

        var battery = new RecentFailureBattery(state);
        Assert.False(battery.IsEmpty);
        Assert.Contains("GatesRed", battery.Section);
        Assert.Contains("did not verify", battery.Section);
        Assert.Contains("SESSION-RESULT", battery.Section);
    }

    [Fact]
    public void BatteryGroupComposesInOrder()
    {
        var state = new RunState();
        state.History.Add(new SessionRecord
        {
            Number = 1, Stage = "B0", Outcome = SessionOutcome.GatesRed,
            GateSummary = "build: FAIL",
            ResultSummary = "SESSION-RESULT: build is broken."
        });

        var failure = new RecentFailureBattery(state);
        var empty = new LessonsBattery(new LessonsManager(Path.Combine(_tmpDir, ".conductor")));

        var group = new BatteryGroup([empty, failure]);
        var rendered = group.Render();

        Assert.Contains("### recent-failure", rendered);
        Assert.DoesNotContain("### lessons", rendered);
    }

    [Fact]
    public void BatteryGroupReturnsEmptyWhenAllBatteriesEmpty()
    {
        var lessonsDir = Path.Combine(_tmpDir, ".conductor");
        var emptyLessons = new LessonsBattery(new LessonsManager(lessonsDir));
        var state = new RunState(); // no history

        var group = new BatteryGroup([emptyLessons, new RecentFailureBattery(state)]);
        Assert.True(group.IsEmpty);
        Assert.Equal("", group.Render());
    }

    [Fact]
    public void BatteryGroupTruncatesAtByteCap()
    {
        var state = new RunState();
        state.History.Add(new SessionRecord
        {
            Number = 1, Stage = "B0", Outcome = SessionOutcome.GatesRed,
            GateSummary = new string('g', 500),
            ResultSummary = new string('r', 500),
        });

        var failure = new RecentFailureBattery(state, maxBytes: 2000);
        // Intentionally create a very long section to verify truncation
        var group = new BatteryGroup([failure], maxBytes: 250);
        var rendered = group.Render();

        Assert.True(rendered.Length <= 260, $"rendered {rendered.Length} bytes should be ~250 byte cap");
    }
}
