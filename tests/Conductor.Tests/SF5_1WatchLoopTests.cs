using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Core.Watch;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SF5.1 — the blocking half, against the store the engine actually writes: the backlog fold, the
/// already-parked check, the incremental drain and the heartbeat.
///
/// <para>These drive a real <see cref="SqliteRunStore"/> in a temp <c>.conductor</c>, not a mock and
/// not a hand-written file. That is the whole lesson of this checkpoint: the first version of these
/// tests appended to <c>events.jsonl</c>, they all passed, and the verb could never fire once against
/// a live engine — because no engine has written that file since events moved into <c>run.db</c>. A
/// test that supplies its own source of truth cannot be wrong about the source of truth.</para>
/// </summary>
public sealed class SF5_1WatchLoopTests : IDisposable
{
    private const string Plan = "sf51-watch";
    private const string Run = "sf51run";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"conductor-sf51-{Guid.NewGuid():N}");
    private readonly string _dir;
    private readonly SqliteRunStore _store;

    public SF5_1WatchLoopTests()
    {
        _dir = Path.Combine(_root, ".conductor");
        Directory.CreateDirectory(_dir);
        _store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.InitializeRun(Run, Plan, _root, null, Conductor.Core.EngineStamp.Parse(null));
        _store.SetRunId(Run);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { TestTemp.DeleteTree(_root); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private WatchLoop NewLoop() => new(_dir, Plan, TimeSpan.FromMilliseconds(20));

    /// <summary>Write events the way the engine does — through the sink — and make them durable
    /// immediately, so a test never races the 200ms drain.</summary>
    private void Emit(params ConductorEvent[] events)
    {
        foreach (var e in events) ((IRunStore)_store).AppendEvent(e);
        _store.FlushEvents();
    }

    private void SaveState(RunState state) =>
        _store.SaveRunState(Run, Plan, System.Text.Json.JsonSerializer.Serialize(state, PlanConfig.JsonOpts));

    private static AttentionRequested Park => new() { Reason = "agent asked for a human" };
    private static SessionFinished Backoff => new() { Number = 1, StageId = "S1", Outcome = "LimitBackoff" };
    private static StageEntered Enter => new() { StageId = "S1", StartHead = "abc" };

    [Fact]
    public void Arm_folds_history_without_waking_on_it()
    {
        // A park that happened BEFORE the watch was armed must not be replayed as a fresh wake —
        // otherwise every supervisor restart fires on the same ancient event forever.
        Emit(Backoff, Enter, Park);
        using var loop = NewLoop();

        Assert.Equal(3, loop.Arm());
        Assert.Null(loop.Drain());
    }

    [Fact]
    public void Arm_attaches_to_the_run_the_engine_is_writing()
    {
        // The regression that made every other test in this class meaningless: the loop must find the
        // run through the store, not assume a file path that nothing produces.
        Emit(Enter);
        using var loop = NewLoop();
        loop.Arm();

        Assert.Equal(Run, loop.RunId);
    }

    [Fact]
    public void Arm_keeps_the_stage_context_it_folded()
    {
        // The fold is not decoration: without it the first wake after arming has no stage to name.
        Emit(Enter);
        using var loop = NewLoop();
        loop.Arm();

        Emit(Park);
        var wake = loop.Drain();

        Assert.NotNull(wake);
        Assert.Equal(WatchReason.NeedsHuman, wake!.Reason);
        Assert.Equal("S1", wake.StageId);
    }

    [Fact]
    public void Drain_reads_only_what_was_appended()
    {
        using var loop = NewLoop();
        loop.Arm();

        Emit(Backoff);
        Assert.Null(loop.Drain());

        Emit(Park);
        Assert.Equal(WatchReason.NeedsHuman, loop.Drain()?.Reason);

        // Nothing new: silence, not a repeat of the wake just handed out.
        Assert.Null(loop.Drain());
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
    public async Task The_state_is_re_read_every_poll_not_only_at_arm()
    {
        // The session-cap park emits NO event (RunLoop sets Paused + ParkedBySessionCap and continues),
        // so a watch armed on a healthy run and checking state only at entry would sleep through the
        // one park that cannot clear itself.
        SaveState(new RunState { Status = RunStatus.Running });
        using var loop = NewLoop();
        loop.Arm();

        var watching = loop.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        SaveState(new RunState { Status = RunStatus.Paused, ParkedBySessionCap = true, AttentionReason = "session cap reached (2/2)" });
        var wake = await watching;

        Assert.Equal(WatchReason.NeedsHuman, wake.Reason);
        Assert.Equal("state", wake.FiredFrom);
    }

    [Fact]
    public async Task The_heartbeat_returns_timeout_and_nothing_else()
    {
        using var loop = NewLoop();
        loop.Arm();

        var wake = await loop.RunAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Equal(WatchReason.Timeout, wake.Reason);
        Assert.Equal("timeout", wake.FiredFrom);
    }

    [Fact]
    public async Task A_wake_beats_the_heartbeat_it_arrives_before()
    {
        using var loop = NewLoop();
        loop.Arm();

        var watching = loop.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Emit(Park);
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
        using var loop = NewLoop();
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
        using var loop = NewLoop();
        loop.Arm();

        var watching = loop.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Emit(new RunFinished { Status = "Completed", Sessions = 26, CheckpointsDone = 24, CheckpointsTotal = 24 });
        EngineLock.Delete(_dir);
        var wake = await watching;

        Assert.Equal(WatchReason.RunEnded, wake.Reason);
    }
}
