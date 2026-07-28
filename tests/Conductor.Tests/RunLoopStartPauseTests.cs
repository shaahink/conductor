using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>G3.1 — the `run --paused` flag→status wiring (pure part of the start-paused path;
/// the full paused→resume→session-1 cycle is covered by HarnessTests).</summary>
public sealed class RunLoopStartPauseTests
{
    private static RunOptions Opts(bool startPaused, bool dryRun = false) =>
        new(DryRun: dryRun, Once: false, MaxSessions: 0, StartPaused: startPaused);

    [Fact]
    public void StartPaused_ParksTheRun()
    {
        var state = new RunState { Status = RunStatus.Idle };
        Assert.True(RunLoop.ApplyStartPause(state, Opts(startPaused: true)));
        Assert.Equal(RunStatus.Paused, state.Status);
    }

    [Fact]
    public void WithoutTheFlag_NothingChanges()
    {
        var state = new RunState { Status = RunStatus.Idle };
        Assert.False(RunLoop.ApplyStartPause(state, Opts(startPaused: false)));
        Assert.Equal(RunStatus.Idle, state.Status);
    }

    [Fact]
    public void DryRun_IgnoresTheFlag()
    {
        var state = new RunState { Status = RunStatus.Idle };
        Assert.False(RunLoop.ApplyStartPause(state, Opts(startPaused: true, dryRun: true)));
        Assert.Equal(RunStatus.Idle, state.Status);
    }

    [Theory]
    [InlineData(RunStatus.NeedsHuman)]
    [InlineData(RunStatus.Aborted)]
    public void AttentionStates_AreNeverMasked(RunStatus status)
    {
        var state = new RunState { Status = status, AttentionReason = "something real" };
        Assert.False(RunLoop.ApplyStartPause(state, Opts(startPaused: true)));
        Assert.Equal(status, state.Status);
        Assert.Equal("something real", state.AttentionReason);
    }
}
