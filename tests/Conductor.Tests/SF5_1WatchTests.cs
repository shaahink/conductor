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

/// <summary>
/// KS2.6 — <c>conductor watches</c>: what is ARMED on this machine.
///
/// <para>The two failures the checkpoint is named for are the same gap read from opposite ends: a
/// preflight blip parked a run for fourteen hours with nobody told, and a handoff mentioning the
/// escalation token told the owner two hundred times. Neither could be checked beforehand, because
/// nothing ever answered "is anything watching this run, and how loud is it allowed to be?". These
/// pin the answers themselves — every field is a sentence, and the one that must never be guessed is
/// the unreadable plan: an unreadable supervisor block is NOT "no supervisor".</para>
/// </summary>
public sealed class KS2_6WatchRosterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "conductor-ks26r-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public KS2_6WatchRosterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { }
    }

    private static PlanConfig Plan(SupervisorConfig? supervisor = null, int pushes = 1) => new()
    {
        Name = "ks26", Repo = "C:/nowhere", Tracker = "TRACKER.md",
        Supervisor = supervisor,
        Limits = new LimitsConfig { MaxPushesPerIncident = pushes },
    };

    private WatchRosterEntry Describe(PlanConfig? plan, string? stateDir = null) =>
        WatchRoster.Describe("repo", "ks26", "run-abcdef123", "Running", 4317, 42, plan, stateDir ?? _dir, Now);

    [Fact]
    public void ARunWithNoSupervisorBlockIsListedAsWatchedByNothing()
    {
        var e = Describe(Plan());

        Assert.Equal("none", e.Supervisor);
        Assert.Equal("none", e.Remote);
        Assert.Equal("-", e.Fuse);
        Assert.True(e.Unwatched);
    }

    [Fact]
    public void AnArmedSupervisorNamesItsCommandAndItsTimeout()
    {
        var e = Describe(Plan(new SupervisorConfig { Command = "claude -p night-watch", TimeoutMinutes = 7 }));

        Assert.Contains("claude -p night-watch", e.Supervisor, StringComparison.Ordinal);
        Assert.Contains("(7m)", e.Supervisor, StringComparison.Ordinal);
        Assert.False(e.Unwatched);
    }

    /// <summary>The two ways a block can be present and inert. Both must read differently from
    /// "none", because "there is a supervisor block and it does nothing" is the state an owner most
    /// needs told.</summary>
    [Fact]
    public void ADeclaredButInertSupervisorSaysWhichKindOfInert()
    {
        Assert.Equal("disabled in the plan",
            Describe(Plan(new SupervisorConfig { Enabled = false, Command = "claude -p x" })).Supervisor);
        Assert.Equal("declared, no command",
            Describe(Plan(new SupervisorConfig { Command = "  " })).Supervisor);
    }

    [Fact]
    public void TheHourlyFuseIsCountedFromTheRunsOwnLedgerAndSaysWhenItIsBurnt()
    {
        var sup = new SupervisorConfig { Command = "claude -p x", MaxPerHour = 2 };

        Assert.Equal("0/2 this hour", WatchRoster.FuseText(sup, _dir, Now));

        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-5));
        Assert.Equal("1/2 this hour", WatchRoster.FuseText(sup, _dir, Now));

        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-1));
        Assert.Equal("2/2 this hour BURNT", WatchRoster.FuseText(sup, _dir, Now));

        // An hour later the same two fires are out of the window.
        Assert.Equal("0/2 this hour", WatchRoster.FuseText(sup, _dir, Now.AddHours(2)));
    }

    [Fact]
    public void AnUncappedFuseSaysSoRatherThanShowingAFractionOfZero()
        => Assert.Equal("0/hr (uncapped)",
            WatchRoster.FuseText(new SupervisorConfig { Command = "x", MaxPerHour = 0 }, _dir, Now));

    [Fact]
    public void TheRemoteNamesEveryTargetAWakeWouldTravelTo()
    {
        var both = new SupervisorRemote { WebhookUrl = "https://example.invalid/hook", Telegram = true, MaxPerHour = 12 };
        var text = WatchRoster.RemoteText(both, _dir, Now);
        Assert.StartsWith("webhook+telegram", text, StringComparison.Ordinal);
        Assert.Contains("0/12 this hour", text, StringComparison.Ordinal);

        Assert.Equal("none", WatchRoster.RemoteText(null, _dir, Now));
        Assert.Equal("disabled in the plan",
            WatchRoster.RemoteText(new SupervisorRemote { Enabled = false, Telegram = true }, _dir, Now));
        Assert.Equal("declared, no target", WatchRoster.RemoteText(new SupervisorRemote(), _dir, Now));
    }

    /// <summary>A local supervisor that has burnt its fuse is exactly the hour a remote wake matters,
    /// so a run with only a remote is watched, not unwatched.</summary>
    [Fact]
    public void ARunWithOnlyARemoteIsStillWatched()
    {
        var e = Describe(Plan(new SupervisorConfig
        {
            Command = "",
            Remote = new SupervisorRemote { Telegram = true },
        }));

        Assert.False(e.Unwatched);
        Assert.Equal("declared, no command", e.Supervisor);
    }

    /// <summary>The park-push cap in force, read off the same key the engine's limiter reads.</summary>
    [Fact]
    public void ThePushCapInForceIsListedPerRun()
    {
        Assert.Equal("1/incident", Describe(Plan()).Pushes);
        Assert.Equal("4/incident", Describe(Plan(pushes: 4)).Pushes);
        Assert.Equal("uncapped", Describe(Plan(pushes: 0)).Pushes);
    }

    /// <summary>The one answer that must never be invented. A run whose plan cannot be read from here
    /// is still listed, saying exactly that — reporting it as "no supervisor" would be the surface
    /// claiming to know the opposite of what it knows.</summary>
    [Fact]
    public void ARunWhosePlanCannotBeReadSaysSoRatherThanClaimingNothingIsArmed()
    {
        var e = Describe(plan: null);

        Assert.Equal("plan not readable from here", e.Supervisor);
        Assert.Equal("?", e.Remote);
        Assert.Equal("?", e.Pushes);
        Assert.False(e.Unwatched);
        Assert.Equal("run-abcd", e.ShortRunId[..8]);
    }
}
