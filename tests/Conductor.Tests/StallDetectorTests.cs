using Conductor.Core;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// F3.1/F3.2: Multi-signal stall detector tests — proves that a session is NOT considered
/// stalled when bg processes are alive or tool calls are recent, and that the soft-kill
/// grace window flows correctly.
/// </summary>
public sealed class StallDetectorTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GraceWindow = TimeSpan.FromMinutes(3);

    // ---------------------------------------------------------------- stall detection

    [Fact]
    public void ActiveWhenStdoutRecent()
    {
        var d = new StallDetector(Threshold, GraceWindow);
        var v = d.Evaluate(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-10), false);
        Assert.Equal(StallVerdict.Active, v);
    }

    [Fact]
    public void ActiveWhenToolCallRecent()
    {
        var d = new StallDetector(Threshold, GraceWindow);
        var v = d.Evaluate(DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow, false);
        Assert.Equal(StallVerdict.Active, v);
    }

    [Fact]
    public void ActiveWhenBgProcessAlive()
    {
        var d = new StallDetector(Threshold, GraceWindow);
        var v = d.Evaluate(DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-10), true);
        Assert.Equal(StallVerdict.Active, v);
    }

    [Fact]
    public void ActiveWithAnySignalAlive_CancelsGrace()
    {
        var d = new StallDetector(Threshold, GraceWindow);
        // First: all signals dead → SoftKillStarted
        var v1 = d.Evaluate(DateTime.UtcNow.AddMinutes(-6), DateTime.UtcNow.AddMinutes(-6), false);
        Assert.Equal(StallVerdict.SoftKillStarted, v1);

        // Next tick: agent produces output → Active, grace cancelled
        var v2 = d.Evaluate(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-7), false);
        Assert.Equal(StallVerdict.Active, v2);

        // Reset → next all-dead should start fresh grace
        var v3 = d.Evaluate(DateTime.UtcNow.AddMinutes(-6), DateTime.UtcNow.AddMinutes(-6), false);
        Assert.Equal(StallVerdict.SoftKillStarted, v3);
    }

    [Fact]
    public void SoftKillToHardKillSequence()
    {
        var t0 = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var clockTime = t0;
        Func<DateTime> clock = () => clockTime;
        var d = new StallDetector(Threshold, GraceWindow, clock);

        var past6m = t0.AddMinutes(-6);

        // First detection at t0
        var v1 = d.Evaluate(past6m, past6m, false);
        Assert.Equal(StallVerdict.SoftKillStarted, v1);

        // Mid-grace (t0 + 1 minute) → GraceRunning
        clockTime = t0.AddMinutes(1);
        var v2 = d.Evaluate(past6m, past6m, false);
        Assert.Equal(StallVerdict.GraceRunning, v2);

        // Grace window expired (t0 + 3 minutes) → HardKill
        clockTime = t0.AddMinutes(GraceWindow.TotalMinutes);
        var v3 = d.Evaluate(past6m, past6m, false);
        Assert.Equal(StallVerdict.HardKill, v3);
    }

    // ---------------------------------------------------------------- zero grace window (legacy behaviour)

    [Fact]
    public void ZeroGraceWindow_ImmediateHardKill()
    {
        var d = new StallDetector(Threshold, TimeSpan.Zero);
        var past = DateTime.UtcNow.AddMinutes(-6);

        var v = d.Evaluate(past, past, false);
        Assert.Equal(StallVerdict.HardKill, v);
    }

    // ---------------------------------------------------------------- Reset

    [Fact]
    public void ResetClearsGraceState()
    {
        var d = new StallDetector(Threshold, GraceWindow);
        var past = DateTime.UtcNow.AddMinutes(-6);
        d.Evaluate(past, past, false);
        Assert.True(d.InGraceWindow);

        d.Reset();
        Assert.False(d.InGraceWindow);
    }

    // ---------------------------------------------------------------- AnyBgProcessAlive integration

    [Fact]
    public void AnyBgProcessAlive_ReturnsFalseWhenRunDbNull()
    {
        var alive = StallDetector.AnyBgProcessAlive(null, "run-1");
        Assert.False(alive);
    }

    [Fact]
    public void AnyBgProcessAlive_ReturnsFalseWhenRunIdNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stall-bg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbDir = Path.Combine(dir, ".conductor");
        Directory.CreateDirectory(dbDir);
        var db = new SqliteRunStore(Path.Combine(dbDir, "run.db"), NullLogger<SqliteRunStore>.Instance);

        var alive = StallDetector.AnyBgProcessAlive(db, null);
        Assert.False(alive);

        db.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void AnyBgProcessAlive_EmptyRun_NoAliveProcesses()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stall-bg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbDir = Path.Combine(dir, ".conductor");
        Directory.CreateDirectory(dbDir);
        var runId = "test-run-empty";
        var db = new SqliteRunStore(Path.Combine(dbDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        db.InitializeRun(runId, "test-plan", dir, "master", "1.0.0");

        var alive = StallDetector.AnyBgProcessAlive(db, runId);
        Assert.False(alive);

        db.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
