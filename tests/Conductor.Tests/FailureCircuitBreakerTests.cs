using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public sealed class FailureCircuitBreakerTests
{
    // ---------------------------------------------------------------- ShouldBreak: no match

    [Fact]
    public void NoPreviousSession_ReturnsFalse()
    {
        var cur = new SessionRecord { Outcome = SessionOutcome.Stalled };
        Assert.False(FailureCircuitBreaker.ShouldBreak(null, cur, null));
    }

    [Fact]
    public void DifferentOutcome_ReturnsFalse()
    {
        var prev = new SessionRecord { Outcome = SessionOutcome.Stalled };
        var cur = new SessionRecord { Outcome = SessionOutcome.GatesRed };
        Assert.False(FailureCircuitBreaker.ShouldBreak(prev, cur, null));
    }

    [Fact]
    public void SuccessfulOutcome_ReturnsFalse()
    {
        var prev = new SessionRecord { Outcome = SessionOutcome.Advanced };
        var cur = new SessionRecord { Outcome = SessionOutcome.Advanced };
        Assert.False(FailureCircuitBreaker.ShouldBreak(prev, cur, null));
    }

    // ---------------------------------------------------------------- stall / timedOut

    [Fact]
    public void TwoStalledWithNoCommitsNoOutput_Breaks()
    {
        var prev = new SessionRecord { Outcome = SessionOutcome.Stalled, NewCommits = new(), ResultSummary = "" };
        var cur = new SessionRecord { Outcome = SessionOutcome.Stalled, NewCommits = new(), ResultSummary = "" };
        Assert.True(FailureCircuitBreaker.ShouldBreak(prev, cur, null));
    }

    [Fact]
    public void TwoStalledButOneHasCommits_NoBreak()
    {
        var prev = new SessionRecord
        {
            Outcome = SessionOutcome.Stalled,
            NewCommits = new List<string> { "abc1234" },
            ResultSummary = "",
        };
        var cur = new SessionRecord { Outcome = SessionOutcome.Stalled, NewCommits = new(), ResultSummary = "" };
        Assert.False(FailureCircuitBreaker.ShouldBreak(prev, cur, null));
    }

    [Fact]
    public void TwoStalledButOneHasOutput_NoBreak()
    {
        var prev = new SessionRecord { Outcome = SessionOutcome.Stalled, NewCommits = new(), ResultSummary = "tried to recover" };
        var cur = new SessionRecord { Outcome = SessionOutcome.Stalled, NewCommits = new(), ResultSummary = "" };
        Assert.False(FailureCircuitBreaker.ShouldBreak(prev, cur, null));
    }

    [Fact]
    public void TwoTimedOutWithNoCommitsNoOutput_Breaks()
    {
        var prev = new SessionRecord { Outcome = SessionOutcome.TimedOut, NewCommits = new(), ResultSummary = "" };
        var cur = new SessionRecord { Outcome = SessionOutcome.TimedOut, NewCommits = new(), ResultSummary = "" };
        Assert.True(FailureCircuitBreaker.ShouldBreak(prev, cur, null));
    }

    // ---------------------------------------------------------------- gates

    [Fact]
    public void TwoGatesRedWithSameFailingGates_Breaks()
    {
        var prev = new SessionRecord
        {
            Outcome = SessionOutcome.GatesRed,
            GateSummary = "build:✓ · lint:✗ · test:✗",
        };
        var cur = new SessionRecord { Outcome = SessionOutcome.GatesRed, GateSummary = "build:✓ · lint:✗ · test:✗" };
        var curGates = new List<GateResult>
        {
            new("build", true, false, false, 0, TimeSpan.Zero, ""),
            new("lint", false, false, false, 1, TimeSpan.FromSeconds(1), "lint failed"),
            new("test", false, false, false, 1, TimeSpan.FromSeconds(2), "test failed"),
        };
        Assert.True(FailureCircuitBreaker.ShouldBreak(prev, cur, curGates));
    }

    [Fact]
    public void TwoGatesRedWithDifferentFailingGates_NoBreak()
    {
        var prev = new SessionRecord
        {
            Outcome = SessionOutcome.GatesRed,
            GateSummary = "build:✓ · lint:✗",
        };
        var cur = new SessionRecord { Outcome = SessionOutcome.GatesRed, GateSummary = "build:✗ · lint:✓" };
        var curGates = new List<GateResult>
        {
            new("build", false, false, false, 1, TimeSpan.Zero, "build failed"),
            new("lint", true, false, false, 0, TimeSpan.FromSeconds(1), ""),
        };
        Assert.False(FailureCircuitBreaker.ShouldBreak(prev, cur, curGates));
    }

    [Fact]
    public void TwoNoProgressWithSameFailingGates_Breaks()
    {
        var prev = new SessionRecord
        {
            Outcome = SessionOutcome.NoProgress,
            GateSummary = "build:✓ · lint:✗",
        };
        var cur = new SessionRecord { Outcome = SessionOutcome.NoProgress, GateSummary = "build:✓ · lint:✗" };
        var curGates = new List<GateResult>
        {
            new("build", true, false, false, 0, TimeSpan.Zero, ""),
            new("lint", false, false, false, 1, TimeSpan.FromSeconds(1), "lint failed"),
        };
        Assert.True(FailureCircuitBreaker.ShouldBreak(prev, cur, curGates));
    }

    [Fact]
    public void GatesRedWithSkippedGatesNotCountedAsFailing()
    {
        var prev = new SessionRecord
        {
            Outcome = SessionOutcome.GatesRed,
            GateSummary = "build:✓ · lint:✗",
        };
        var cur = new SessionRecord { Outcome = SessionOutcome.GatesRed, GateSummary = "build:✓ · lint:✗" };
        // lint was skipped (not a real failure) — the only real failure is 'extra'
        var curGates = new List<GateResult>
        {
            new("build", true, false, false, 0, TimeSpan.Zero, ""),
            new("lint", false, true, false, 0, TimeSpan.Zero, ""), // skipped
            new("extra", false, false, false, 1, TimeSpan.FromSeconds(1), "extra failed"),
        };
        // different failing gates → no break
        Assert.False(FailureCircuitBreaker.ShouldBreak(prev, cur, curGates));
    }

    // ---------------------------------------------------------------- AgentError

    [Fact]
    public void TwoAgentErrors_Breaks()
    {
        var prev = new SessionRecord { Outcome = SessionOutcome.AgentError };
        var cur = new SessionRecord { Outcome = SessionOutcome.AgentError };
        Assert.True(FailureCircuitBreaker.ShouldBreak(prev, cur, null));
    }

    // ---------------------------------------------------------------- ParseFailingGates

    [Fact]
    public void ParseFailingGates_ExtractsFailedNames()
    {
        var result = FailureCircuitBreaker.ParseFailingGates("build:✗ · lint:✓ · test:✗");
        Assert.Equal(2, result.Count);
        Assert.Contains("build", result);
        Assert.Contains("test", result);
    }

    [Fact]
    public void ParseFailingGates_AllGreen_Empty()
    {
        var result = FailureCircuitBreaker.ParseFailingGates("build:✓ · lint:✓");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseFailingGates_EmptyString_Empty()
    {
        var result = FailureCircuitBreaker.ParseFailingGates("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseFailingGates_IgnoresCase()
    {
        var result = FailureCircuitBreaker.ParseFailingGates("Build:✗ · LINT:✗");
        Assert.Equal(2, result.Count);
        Assert.Contains("Build", result);
        Assert.Contains("LINT", result);
    }
}
