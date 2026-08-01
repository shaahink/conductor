using Conductor.Core.Events;
using Conductor.Core.Watch;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF5.1 — the wake set is a POLICY, so it is measured event by event rather than described in a doc
/// comment. The don't-wake half is the load-bearing half: a supervisor that wakes on a usage-limit
/// backoff or a session rollover is the polling babysitter again, only with extra steps.
/// </summary>
public class SF5_1WatchWakeSetTests
{
    private static T Stamp<T>(T evt, long seq) where T : ConductorEvent => (T)(evt with { Seq = seq, RunId = "r" });

    [Fact]
    public void Silent_set_wakes_on_nothing()
    {
        var w = new WatchWakeSet();
        ConductorEvent[] quiet =
        [
            new RunStarted { Plan = "p", Repo = "r" },
            new StageEntered { StageId = "S1", StartHead = "abc" },
            new SessionStarted { Number = 1, StageId = "S1", Kind = "Deliver" },
            // The three self-resuming outcomes, which are exactly what a real run emits most of:
            new SessionFinished { Number = 1, StageId = "S1", Outcome = "LimitBackoff" },
            new SessionFinished { Number = 2, StageId = "S1", Outcome = "RolledOver" },
            new SessionFinished { Number = 3, StageId = "S1", Outcome = "BlockedUntil" },
            new SessionFinished { Number = 4, StageId = "S1", Outcome = "Advanced" },
            new GateFinished { Name = "build", Passed = true, Scope = "session" },
            new GateFinished { Name = "build", Passed = true, Scope = "phase" },
            new CheckpointConfirmed { CheckpointId = "S1.1", StageId = "S1" },
            new StageConfirmed { StageId = "S1" },
            new TokenDelta { Input = 100, Output = 20 },
            new NoteAdded { Kind = "finding", Content = "learned something" },
            new TaskAdded { TaskId = "t1", CheckpointId = "S1.1", Title = "x", Source = "agent" },
            new TaskStatusChanged { TaskId = "t1", Status = "done" },
            new McpCallFinished { ToolName = "task_update", DurationMs = 5, Success = true },
            new OwnerApprovalGranted { StageId = "S1" },
            new SoftBreakRequested { LiveTokens = 10, TokenBudget = 20 },
            new BlockedUntilRequested { UntilUtc = DateTimeOffset.UtcNow.AddHours(1), Reason = "rate limit" },
            new RunBlockedUntil { UntilUtc = DateTimeOffset.UtcNow.AddHours(1), Reason = "rate limit" },
            new PlanReloaded { PlanVersion = 2, Stages = 8, Gates = 5 },
        ];

        long seq = 0;
        foreach (var e in quiet)
            Assert.Null(w.Observe(e with { Seq = ++seq, RunId = "r" }));
    }

    [Fact]
    public void Attention_requested_wakes_needs_human()
    {
        var w = new WatchWakeSet();
        w.Observe(Stamp(new StageEntered { StageId = "S4", StartHead = "h" }, 1));
        var wake = w.Observe(Stamp(new AttentionRequested { Reason = "agent backend refused 10 times in a row" }, 2));

        Assert.NotNull(wake);
        Assert.Equal(WatchReason.NeedsHuman, wake!.Reason);
        Assert.Equal("S4", wake.StageId);
        Assert.Contains("refused 10 times", wake.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Owner_approval_requested_wakes_owner_park()
    {
        var w = new WatchWakeSet();
        var wake = w.Observe(Stamp(new OwnerApprovalRequested { StageId = "SF7" }, 1));

        Assert.NotNull(wake);
        Assert.Equal(WatchReason.OwnerPark, wake!.Reason);
        Assert.Equal("SF7", wake.StageId);
    }

    [Fact]
    public void Run_finished_wakes_run_ended()
    {
        var w = new WatchWakeSet();
        var wake = w.Observe(Stamp(new RunFinished { Status = "Completed", Sessions = 26, CheckpointsDone = 24, CheckpointsTotal = 24 }, 9));

        Assert.NotNull(wake);
        Assert.Equal(WatchReason.RunEnded, wake!.Reason);
        Assert.Contains("24/24", wake.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_identical_failures_on_one_stage_wake_the_circuit_breaker()
    {
        var w = new WatchWakeSet();
        Assert.Null(w.Observe(Stamp(new SessionFinished { Number = 5, StageId = "S2", Outcome = "GatesRed" }, 1)));
        var wake = w.Observe(Stamp(new SessionFinished { Number = 6, StageId = "S2", Outcome = "GatesRed" }, 2));

        Assert.NotNull(wake);
        Assert.Equal(WatchReason.CircuitBreaker, wake!.Reason);
        Assert.Equal("S2", wake.StageId);
        Assert.Contains("#5 and #6", wake.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_backoffs_never_wake_the_breaker()
    {
        // The exact scenario the wake set is designed around: 2 of the last 3 events on a real run
        // were usage-limit backoffs, and every one of them self-resumed.
        var w = new WatchWakeSet();
        for (var i = 1; i <= 6; i++)
            Assert.Null(w.Observe(Stamp(new SessionFinished { Number = i, StageId = "S2", Outcome = "LimitBackoff" }, i)));
    }

    [Fact]
    public void Same_failure_on_different_stages_does_not_wake()
    {
        var w = new WatchWakeSet();
        Assert.Null(w.Observe(Stamp(new SessionFinished { Number = 1, StageId = "S1", Outcome = "Stalled" }, 1)));
        Assert.Null(w.Observe(Stamp(new SessionFinished { Number = 2, StageId = "S2", Outcome = "Stalled" }, 2)));
    }

    [Fact]
    public void A_stall_pair_that_still_landed_commits_does_not_wake()
    {
        // Mirrors FailureCircuitBreaker: a Stalled/TimedOut pair is only "identical failure" when
        // neither produced work. A pair that committed is slow, not stuck.
        var w = new WatchWakeSet();
        Assert.Null(w.Observe(Stamp(new SessionFinished { Number = 1, StageId = "S1", Outcome = "Stalled", NewCommits = ["abc123"] }, 1)));
        Assert.Null(w.Observe(Stamp(new SessionFinished { Number = 2, StageId = "S1", Outcome = "Stalled", NewCommits = ["def456"] }, 2)));
    }

    [Fact]
    public void A_stall_pair_that_produced_nothing_wakes()
    {
        var w = new WatchWakeSet();
        Assert.Null(w.Observe(Stamp(new SessionFinished { Number = 1, StageId = "S1", Outcome = "Stalled" }, 1)));
        var wake = w.Observe(Stamp(new SessionFinished { Number = 2, StageId = "S1", Outcome = "Stalled" }, 2));
        Assert.Equal(WatchReason.CircuitBreaker, wake?.Reason);
    }

    [Fact]
    public void One_red_phase_battery_is_silent_and_the_second_wakes()
    {
        var w = new WatchWakeSet();
        w.Observe(Stamp(new StageEntered { StageId = "S3", StartHead = "h" }, 1));

        // First RED battery: three gates, one required failure. A normal fix loop — no wake.
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "build", Passed = true, Scope = "phase" }, 2)));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "tests", Passed = false, Scope = "phase" }, 3)));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "ratchet", Passed = true, Scope = "phase" }, 4)));
        Assert.Equal(1, w.PhaseRedsFor("S3"));

        // A fix session runs in between; its session-scoped battery is red too and still says nothing.
        Assert.Null(w.Observe(Stamp(new SessionStarted { Number = 2, StageId = "S3", Kind = "Fix" }, 5)));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "tests", Passed = false, Scope = "session" }, 6)));
        Assert.Equal(1, w.PhaseRedsFor("S3"));

        // Second RED phase battery: the fix loop is not converging.
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "build", Passed = true, Scope = "phase" }, 7)));
        var wake = w.Observe(Stamp(new GateFinished { Name = "tests", Passed = false, Scope = "phase" }, 8));

        Assert.NotNull(wake);
        Assert.Equal(WatchReason.PhaseRedTwice, wake!.Reason);
        Assert.Equal("S3", wake.StageId);
        Assert.Equal(2, w.PhaseRedsFor("S3"));
    }

    [Fact]
    public void Two_failing_gates_in_one_battery_are_one_red_not_two()
    {
        var w = new WatchWakeSet();
        w.Observe(Stamp(new StageEntered { StageId = "S3", StartHead = "h" }, 1));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "build", Passed = false, Scope = "phase" }, 2)));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "tests", Passed = false, Scope = "phase" }, 3)));
        Assert.Equal(1, w.PhaseRedsFor("S3"));
    }

    [Fact]
    public void Optional_and_skipped_gate_failures_cannot_make_a_battery_red()
    {
        var w = new WatchWakeSet();
        w.Observe(Stamp(new StageEntered { StageId = "S3", StartHead = "h" }, 1));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "lint", Passed = false, Optional = true, Scope = "phase" }, 2)));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "e2e", Passed = false, Skipped = true, Scope = "phase" }, 3)));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "lint", Passed = false, Optional = true, Scope = "phase" }, 4)));
        Assert.Equal(0, w.PhaseRedsFor("S3"));
    }

    [Fact]
    public void Phase_red_counting_is_per_stage_not_global()
    {
        var w = new WatchWakeSet();
        w.Observe(Stamp(new StageEntered { StageId = "S1", StartHead = "h" }, 1));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "tests", Passed = false, Scope = "phase" }, 2)));
        w.Observe(Stamp(new StageEntered { StageId = "S2", StartHead = "h" }, 3));
        Assert.Null(w.Observe(Stamp(new GateFinished { Name = "tests", Passed = false, Scope = "phase" }, 4)));

        Assert.Equal(1, w.PhaseRedsFor("S1"));
        Assert.Equal(1, w.PhaseRedsFor("S2"));
    }
}
