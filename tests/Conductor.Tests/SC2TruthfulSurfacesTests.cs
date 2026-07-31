using Conductor.Commands;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SC2.2 — the engine says what it actually measured. Four lies died here, each one observed on a real
/// run before it was fixed:
/// <list type="number">
///   <item><description>"CONFIRMED (full battery green)" printed for stages that had no gates at all —
///   nine of thirteen on one run, including the stage that deployed a live site (sk-platform #2);</description></item>
///   <item><description>the phase-gate verdict was spelled differently from the session verdict, so a log
///   consumer grepping one grammar never saw the other (devcontext #18);</description></item>
///   <item><description>the phase-RED line and the session it queued three seconds later reported
///   different attempt numbers (devcontext #19);</description></item>
///   <item><description>"what hurt" never aged and never cleared.</description></item>
/// </list>
/// </summary>
public class SC2TruthfulSurfacesTests
{
    private static GateResult Pass(string name) => new(name, true, false, false, 0, TimeSpan.FromSeconds(1), "");
    private static GateResult Fail(string name) => new(name, false, false, false, 1, TimeSpan.FromSeconds(1), "boom");

    // ── the canonical token (devcontext #18) ──

    [Fact]
    public void Token_IsGreenRedOrNone()
    {
        Assert.Equal("gates GREEN", GateRunner.Token([Pass("build"), Pass("tests")]));
        Assert.Equal("gates RED", GateRunner.Token([Pass("build"), Fail("tests")]));
        Assert.Equal("gates NONE", GateRunner.Token([]));
    }

    /// <summary>An empty battery is vacuously "all required passed" — <see cref="GateRunner.AllRequiredPassed"/>
    /// says so, and it is right to. The token must NOT inherit that vacuous truth as the word GREEN:
    /// that is precisely the sentence sk-platform #2 caught lying.</summary>
    [Fact]
    public void Token_DoesNotCallAnEmptyBatteryGreen()
    {
        Assert.True(GateRunner.AllRequiredPassed([]));
        Assert.DoesNotContain("GREEN", GateRunner.Token([]), StringComparison.Ordinal);
    }

    /// <summary>An optional gate that failed does not turn the battery RED — the verdict engine already
    /// treats it as green (<see cref="GateResult.IsGreen"/>), and the token must agree with the verdict
    /// it labels rather than inventing a second opinion.</summary>
    [Fact]
    public void Token_AgreesWithTheVerdictItLabels()
    {
        var optionalFailure = new GateResult("lint", false, false, true, 1, TimeSpan.Zero, "");
        Assert.True(GateRunner.AllRequiredPassed([Pass("build"), optionalFailure]));
        Assert.Equal("gates GREEN", GateRunner.Token([Pass("build"), optionalFailure]));
    }

    // ── the three honest confirmation states (sk-platform #2) ──

    [Fact]
    public void ConfirmationBasis_GreenBattery_NamesTheGates()
    {
        var basis = GateRunner.ConfirmationBasis(2, [Pass("build"), Pass("tests")]);
        Assert.Contains("gates GREEN", basis, StringComparison.Ordinal);
        Assert.Contains("build", basis, StringComparison.Ordinal);
        Assert.Contains("tests", basis, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmationBasis_NoGatesForThisStage_SaysSo_AndNeverClaimsABattery()
    {
        var basis = GateRunner.ConfirmationBasis(0, []);
        Assert.Contains("no gates configured for this stage", basis, StringComparison.Ordinal);
        Assert.DoesNotContain("GREEN", basis, StringComparison.Ordinal);
        Assert.DoesNotContain("battery", basis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfirmationBasis_RedBattery_NamesTheFailures_AndAdmitsTheOverride()
    {
        var basis = GateRunner.ConfirmationBasis(2, [Pass("build"), Fail("tests")]);
        Assert.Contains("gates RED", basis, StringComparison.Ordinal);
        Assert.Contains("tests", basis, StringComparison.Ordinal);
        Assert.Contains("confirmed anyway", basis, StringComparison.Ordinal);
    }

    /// <summary>The trap in fixing #2: a battery whose results are not in memory (reused on an unchanged
    /// tree, or a restart that lost them) must NOT be reported as a gateless stage. That would be a new
    /// lie in the shape of the old one.</summary>
    [Fact]
    public void ConfirmationBasis_ConfiguredButNoResultsOnRecord_IsNotMistakenForGateless()
    {
        var basis = GateRunner.ConfirmationBasis(3, null);
        Assert.DoesNotContain("no gates configured", basis, StringComparison.Ordinal);
        Assert.Contains("3 gate(s) configured", basis, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmationBasis_ReusedBattery_SaysItWasReused()
    {
        var basis = GateRunner.ConfirmationBasis(1, [Pass("build")], reused: true);
        Assert.Contains("gates GREEN", basis, StringComparison.Ordinal);
        Assert.Contains("reused", basis, StringComparison.Ordinal);
    }

    // ── stage-scoped gate coverage: the plan-level count hid the per-stage truth ──

    [Fact]
    public void ConfiguredForStage_CountsOnlyGatesScopedToThatStage()
    {
        var plan = ScopedPlan();
        Assert.Equal(1, GateRunner.ConfiguredForStage(plan, plan.Stages[0])); // S1: named by the scoped gate
        Assert.Equal(0, GateRunner.ConfiguredForStage(plan, plan.Stages[1])); // S2: named by nothing
        Assert.Equal(0, GateRunner.ConfiguredForStage(plan, plan.Stages[2])); // S3: wrong kind
    }

    /// <summary>Doctor's gate check used to answer from <c>plan.Gates.Count</c> alone, so a plan with a
    /// dozen gates scoped to two stages reported a cheerful "12 configured (fast/full)" while most of the
    /// run had no battery at all.</summary>
    [Fact]
    public void CheckGates_Warns_WhenGatesExistButSomeStagesMatchNone()
    {
        var check = DoctorCommand.CheckGates(ScopedPlan());
        Assert.Equal("warn", check.State);
        Assert.Contains("S2", check.Message, StringComparison.Ordinal);
        Assert.Contains("S3", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("S1", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGates_Ok_WhenEveryStageIsCovered()
    {
        var plan = ScopedPlan();
        plan.Gates[0].Stages = null;
        plan.Gates[0].StageKinds = null;
        var check = DoctorCommand.CheckGates(plan);
        Assert.Equal("ok", check.State);
        Assert.Contains("every stage covered", check.Message, StringComparison.Ordinal);
    }

    private static PlanConfig ScopedPlan() => new()
    {
        Name = "scoped",
        Repo = ".",
        Tracker = "T.md",
        Stages =
        {
            new StageConfig { Id = "S1", Title = "gated", Kind = "deliver" },
            new StageConfig { Id = "S2", Title = "ungated", Kind = "deliver" },
            new StageConfig { Id = "S3", Title = "docs only", Kind = "docs" },
        },
        Gates = { new GateConfig { Name = "build", Command = "exit 0", Stages = ["S1", "S3"], StageKinds = ["deliver"] } },
    };

    // ── attempt numbering (devcontext #19) ──

    /// <summary>
    /// The observed disagreement, three seconds apart on one run:
    /// <c>phase G6 full battery RED — queuing fix session (attempt 1/2)</c> then
    /// <c>session #22 start — Fix G6 attempt 2/2</c>. Both lines describe the same session. The queuing
    /// line was reading the spent-attempt counter; the session reads the counter plus one. One property
    /// now answers for both, so they cannot drift apart again.
    /// </summary>
    [Fact]
    public void NextAttemptNumber_IsWhatTheQueuedSessionWillReport()
    {
        var state = new RunState();
        Assert.Equal(1, state.NextAttemptNumber);   // nothing spent yet: the next session is attempt 1

        state.AttemptsThisStage++;                  // a phase gate just went RED and burned an attempt
        Assert.Equal(2, state.NextAttemptNumber);   // ...so the fix session it queues is attempt 2
        Assert.Equal(state.AttemptsThisStage + 1, state.NextAttemptNumber);
    }

    // ── sticky failure fields carry their age ──

    [Fact]
    public void Age_ReadsInTheLargestUsefulUnit()
    {
        Assert.Equal("3s", Staleness.Age(TimeSpan.FromSeconds(3)));
        Assert.Equal("4m", Staleness.Age(TimeSpan.FromMinutes(4)));
        Assert.Equal("2h 07m", Staleness.Age(TimeSpan.FromMinutes(127)));
        Assert.Equal("3d 04h", Staleness.Age(TimeSpan.FromHours(76)));
    }

    /// <summary>Clock skew must not print a negative age — a stamp from the future reads as brand new.</summary>
    [Fact]
    public void Age_ClampsNegativeSpans()
        => Assert.Equal("0s", Staleness.Age(TimeSpan.FromSeconds(-30)));

    [Fact]
    public void Since_CarriesAgeAndWallClock()
    {
        var now = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        var suffix = Staleness.Since(now.AddHours(-2), now);
        Assert.Contains("2h 00m ago", suffix, StringComparison.Ordinal);
        Assert.Contains("10:00:00Z", suffix, StringComparison.Ordinal);
    }

    /// <summary>A state.json written before SC2.2 has no stamp. Say nothing rather than invent a time.</summary>
    [Fact]
    public void Since_IsEmpty_WhenNoStampWasRecorded()
        => Assert.Equal("", Staleness.Since((DateTime?)null));

    [Fact]
    public void SetAttention_StampsAndClearsTogether()
    {
        var state = new RunState();
        state.SetAttention("gate build failed 3 times");
        Assert.NotNull(state.AttentionReason);
        Assert.NotNull(state.AttentionSinceUtc);

        state.SetAttention(null);
        Assert.Null(state.AttentionReason);
        Assert.Null(state.AttentionSinceUtc);
    }
}
