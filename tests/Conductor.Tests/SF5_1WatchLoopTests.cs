using Conductor.Core;
using Conductor.Core.Watch;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF5.1 — the blocking half, against real files: the backlog fold, the already-parked check, the
/// incremental drain and the heartbeat. These use a temp <c>.conductor</c> rather than mocks because
/// the failure mode being guarded against is a watch that reads the wrong file or replays history as
/// if it were news, and a mock cannot be wrong about that.
/// </summary>
public sealed class SF5_1WatchLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sf51-{Guid.NewGuid():N}", ".conductor");

    public SF5_1WatchLoopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Events => Path.Combine(_dir, "events.jsonl");

    private void Append(params string[] lines) => File.AppendAllLines(Events, lines);

    private const string Park = """{"type":"attentionRequested","reason":"agent asked for a human","seq":9,"ts":"2026-08-01T10:00:00Z","runId":"r"}""";
    private const string Backoff = """{"type":"sessionFinished","number":1,"stageId":"S1","outcome":"LimitBackoff","seq":1,"ts":"2026-08-01T09:00:00Z","runId":"r"}""";
    private const string Enter = """{"type":"stageEntered","stageId":"S1","startHead":"abc","seq":2,"ts":"2026-08-01T09:01:00Z","runId":"r"}""";

    [Fact]
    public void Arm_folds_history_without_waking_on_it()
    {
        // A park that happened BEFORE the watch was armed must not be replayed as a fresh wake —
        // otherwise every supervisor restart fires on the same ancient event forever.
        Append(Backoff, Enter, Park);
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));

        Assert.Equal(3, loop.Arm());
        Assert.Null(loop.Drain());
    }

    [Fact]
    public void Arm_keeps_the_stage_context_it_folded()
    {
        // The fold is not decoration: without it the first wake after arming has no stage to name.
        Append(Enter);
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));
        loop.Arm();

        Append(Park);
        var wake = loop.Drain();

        Assert.NotNull(wake);
        Assert.Equal(WatchReason.NeedsHuman, wake!.Reason);
        Assert.Equal("S1", wake.StageId);
    }

    [Fact]
    public void Drain_reads_only_what_was_appended()
    {
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));
        loop.Arm();

        Append(Backoff);
        Assert.Null(loop.Drain());

        Append(Park);
        Assert.Equal(WatchReason.NeedsHuman, loop.Drain()?.Reason);

        // Nothing new: silence, not a repeat of the wake just handed out.
        Assert.Null(loop.Drain());
    }

    [Fact]
    public void A_torn_or_unknown_line_does_not_kill_the_watch()
    {
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));
        loop.Arm();

        Append("""{"type":"somethingFromANewerEngine","x":1,"seq":3,"ts":"2026-08-01T10:00:00Z","runId":"r"}""");
        Append("""{"type":"attentionRequested","reas""");
        Assert.Null(loop.Drain());

        Append(Park);
        Assert.Equal(WatchReason.NeedsHuman, loop.Drain()?.Reason);
    }

    [Theory]
    [InlineData(RunStatus.NeedsHuman, WatchReason.NeedsHuman)]
    [InlineData(RunStatus.AwaitingOwner, WatchReason.OwnerPark)]
    [InlineData(RunStatus.Completed, WatchReason.RunEnded)]
    [InlineData(RunStatus.Aborted, WatchReason.RunEnded)]
    public void A_run_that_is_already_parked_wakes_from_state(RunStatus status, WatchReason expected)
    {
        var wake = WatchLoop.FromState(new RunState { Status = status, AttentionReason = "already parked", CurrentStage = "S9" });

        Assert.NotNull(wake);
        Assert.Equal(expected, wake!.Reason);
        Assert.Equal("state", wake.FiredFrom);
    }

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Idle)]
    [InlineData(RunStatus.Backoff)]
    [InlineData(RunStatus.VerifyingGates)]
    [InlineData(RunStatus.Waiting)]
    [InlineData(RunStatus.Paused)]
    public void A_healthy_or_deliberately_paused_run_does_not_wake_from_state(RunStatus status)
        => Assert.Null(WatchLoop.FromState(new RunState { Status = status }));

    [Fact]
    public void A_pause_the_session_cap_imposed_does_wake()
    {
        // Paused-by-a-human is not a wake; paused-by-the-cap is, because only a human raising the cap
        // ever clears it and the run otherwise sits there looking healthy.
        var wake = WatchLoop.FromState(new RunState
        {
            Status = RunStatus.Paused,
            ParkedBySessionCap = true,
            AttentionReason = "session cap reached (40/40)",
        });

        Assert.Equal(WatchReason.NeedsHuman, wake?.Reason);
        Assert.Contains("40/40", wake!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_heartbeat_returns_timeout_and_nothing_else()
    {
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));
        loop.Arm();

        var wake = await loop.RunAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Equal(WatchReason.Timeout, wake.Reason);
        Assert.Equal("timeout", wake.FiredFrom);
    }

    [Fact]
    public async Task A_wake_beats_the_heartbeat_it_arrives_before()
    {
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));
        loop.Arm();

        var watching = loop.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Append(Park);
        var wake = await watching;

        Assert.Equal(WatchReason.NeedsHuman, wake.Reason);
        Assert.Equal("event", wake.FiredFrom);
    }

    [Fact]
    public async Task An_engine_that_vanishes_wakes_engine_gone()
    {
        // A live lock (this test process is unquestionably alive), then no lock at all — a crashed or
        // closed engine, which is otherwise indistinguishable from a quiet one.
        EngineLock.Write(_dir);
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));
        loop.Arm();
        Assert.True(loop.EngineAlive());

        var watching = loop.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        EngineLock.Delete(_dir);
        var wake = await watching;

        Assert.Equal(WatchReason.EngineGone, wake.Reason);
        Assert.Equal("liveness", wake.FiredFrom);
    }

    [Fact]
    public async Task A_run_that_ended_cleanly_reports_the_reason_not_the_symptom()
    {
        // The engine emits RunFinished and only THEN releases the lock. Reporting engine-gone for a
        // completed run would send a supervisor hunting a crash that never happened.
        EngineLock.Write(_dir);
        var loop = new WatchLoop(_dir, TimeSpan.FromMilliseconds(20));
        loop.Arm();

        var watching = loop.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Append("""{"type":"runFinished","status":"Completed","sessions":26,"checkpointsDone":24,"checkpointsTotal":24,"seq":50,"ts":"2026-08-01T11:00:00Z","runId":"r"}""");
        EngineLock.Delete(_dir);
        var wake = await watching;

        Assert.Equal(WatchReason.RunEnded, wake.Reason);
    }
}
