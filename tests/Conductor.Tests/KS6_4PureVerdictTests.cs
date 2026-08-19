using System.Text.RegularExpressions;
using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS6.4 — the decision table, driven WITHOUT the loop. Not one test in this file constructs a
/// RunContext, a store, a repository or an agent; before the extraction, every branch asserted here
/// could only be reached by standing up all four and running a session to completion, which is why
/// most of them had never been asserted at all.
///
/// Two properties are load-bearing beyond the table itself. <see cref="AnAdvisoryRowNeverChangesAVerdict"/>
/// is KS4.5's seam stated negatively — a judgement joins the evidence and cannot move the verdict —
/// and <see cref="TheVerdictFunctionNamesNothingImpure"/> is what stops the function drifting back
/// into the loop it was cut out of.
/// </summary>
public class KS6_4PureVerdictTests
{
    /// <summary>A healthy delivery session that has not been graded yet: every row at its quiet value,
    /// so a test that sets one row is testing exactly that row.</summary>
    private static SessionEvidence Baseline => new()
    {
        Kind = SessionKind.Deliver,
        SessionNumber = 7,
        MaxResumesPerSession = 2,
        StallBackoffMinutes = 10,
        VerifierThreshold = 80,
    };

    // ── control triage: everything settled here is a gate battery the run does not buy ──

    [Fact]
    public void KilledByUserPausesTheRunAndGradesNothing()
    {
        // Every other signal screams failure. A kill is still not a judgement about the work.
        var d = SessionVerdict.Decide(Baseline with
        {
            KilledByUser = true,
            Stalled = true,
            TimedOut = true,
            AgentErrored = true,
            CircuitBreakerEnabled = true,
            SameFailurePattern = true,
        });

        Assert.Equal(VerdictDisposition.PauseKilled, d.Disposition);
        Assert.Equal(SessionOutcome.KilledByUser, d.Outcome);
        Assert.Equal(AttemptEffect.Unchanged, d.Attempts);
        Assert.False(d.ReturnToIdle);
        Assert.Null(d.Backoff);
    }

    [Fact]
    public void AStalledSessionInsideItsResumeBudgetResumesAndLengthensTheBackoff()
    {
        var d = SessionVerdict.Decide(Baseline with
        {
            Stalled = true,
            ResumeCount = 1,
            PriorStallBackoffMultiplier = 2,
        });

        Assert.Equal(VerdictDisposition.Resume, d.Disposition);
        Assert.Equal(SessionOutcome.Stalled, d.Outcome);
        Assert.Equal(AttemptEffect.Increment, d.Attempts);
        Assert.Equal("session stalled (no output)", d.Reason);
        Assert.Equal(new StallBackoffPlan(3, 30, TouchesUntil: true), d.Backoff);
        Assert.True(d.ReturnToIdle);
    }

    [Fact]
    public void AStalledSessionOutOfResumeBudgetConsultsTheAdvisorDefaultingToRetry()
    {
        var d = SessionVerdict.Decide(Baseline with { Stalled = true, ResumeCount = 2, MaxResumesPerSession = 2 });

        Assert.Equal(VerdictDisposition.ConsultAdvisor, d.Disposition);
        Assert.Equal(AdvisorAction.Retry, d.AdvisorDefault);
        Assert.Equal("resume budget exhausted after stall/timeout", d.Reason);
        Assert.Equal(AttemptEffect.Increment, d.Attempts);
    }

    [Fact]
    public void ATimeoutClearsTheBackoffInstantWhereAStallExtendsIt()
    {
        var stall = SessionVerdict.Decide(Baseline with { Stalled = true, PriorStallBackoffMultiplier = 4 });
        var timeout = SessionVerdict.Decide(Baseline with { TimedOut = true, PriorStallBackoffMultiplier = 4 });

        Assert.Equal(new StallBackoffPlan(5, 50, TouchesUntil: true), stall.Backoff);
        // A timeout is not a stall: the multiplier collapses and the instant is cleared, not extended.
        Assert.Equal(new StallBackoffPlan(1, null, TouchesUntil: true), timeout.Backoff);
        Assert.Equal(SessionOutcome.TimedOut, timeout.Outcome);
        Assert.Equal("session hit the hard timeout", timeout.Reason);
    }

    [Fact]
    public void TheCircuitBreakerOutranksTheBackoffAndLeavesTheRunStatusAlone()
    {
        var d = SessionVerdict.Decide(Baseline with
        {
            Stalled = true,
            CircuitBreakerEnabled = true,
            SameFailurePattern = true,
            ResumeCount = 0,
        });

        Assert.Equal(VerdictDisposition.ConsultAdvisor, d.Disposition);
        Assert.Equal(AdvisorAction.NeedsHuman, d.AdvisorDefault);
        Assert.Equal("identical failure pattern: 2 consecutive Stalled sessions with matching symptoms", d.Reason);
        // Both of these were accidents of statement order in the method this came out of, and both
        // are behaviour: the breaker returns before the backoff bookkeeping and without going Idle.
        Assert.Null(d.Backoff);
        Assert.False(d.ReturnToIdle);
    }

    [Fact]
    public void TheIdenticalStallParkFiresOnlyWhileTheBreakerIsOff()
    {
        var seed = Baseline with
        {
            Stalled = true,
            StallPatternTerminationEnabled = true,
            IdenticalStallPattern = true,
            SessionNumber = 6,
        };

        var parked = SessionVerdict.Decide(seed);
        Assert.Equal(VerdictDisposition.ParkForHuman, parked.Disposition);
        Assert.Equal("identical-stall: 5 sessions stalled with no commits, no output — environment or agent is broken", parked.Reason);
        Assert.False(parked.ReturnToIdle);

        // With the breaker enabled the same evidence goes to the advisor instead — the two terminators
        // are alternatives, and turning both on must not double-judge the session.
        var breakered = SessionVerdict.Decide(seed with { CircuitBreakerEnabled = true, SameFailurePattern = false });
        Assert.Equal(VerdictDisposition.Resume, breakered.Disposition);
    }

    [Fact]
    public void AWaitRequestOutranksAuditAndVerifyButNotAStall()
    {
        Assert.Equal(VerdictDisposition.HonourBlockUntil,
            SessionVerdict.Decide(Baseline with { BlockedUntilRequested = true, Kind = SessionKind.Audit }).Disposition);
        Assert.Equal(VerdictDisposition.HonourBlockUntil,
            SessionVerdict.Decide(Baseline with { BlockedUntilRequested = true, Kind = SessionKind.Verify }).Disposition);
        // SC5.1 judges a wait alongside kill and stall, not above them.
        Assert.Equal(VerdictDisposition.Resume,
            SessionVerdict.Decide(Baseline with { BlockedUntilRequested = true, Stalled = true }).Disposition);
    }

    [Fact]
    public void AStaleWaitFallsThroughToTheOrdinaryVerdict()
    {
        // The engine cannot know a window has already opened until it looks at the clock, so the caller
        // drops the row and asks again. That fall-through is the contract, and this is it.
        var asked = Baseline with { BlockedUntilRequested = true };
        Assert.Equal(VerdictDisposition.HonourBlockUntil, SessionVerdict.Decide(asked).Disposition);
        Assert.Equal(VerdictDisposition.RunGateBattery,
            SessionVerdict.Decide(asked with { BlockedUntilRequested = false }).Disposition);
    }

    [Fact]
    public void AnAuditSessionSchedulesThePhaseGateWithoutBuyingABattery()
    {
        var d = SessionVerdict.Decide(Baseline with { Kind = SessionKind.Audit });

        Assert.Equal(VerdictDisposition.AuditComplete, d.Disposition);
        Assert.Equal(SessionOutcome.Progress, d.Outcome);
        Assert.Equal(AttemptEffect.Unchanged, d.Attempts);
        Assert.True(d.ReturnToIdle);
    }

    [Theory]
    [InlineData(81, VerdictDisposition.VerifyPassed)]
    [InlineData(80, VerdictDisposition.VerifyPassed)]   // the threshold is an inclusive floor
    [InlineData(79, VerdictDisposition.VerifyFailed)]
    public void TheVerifierThresholdIsAnInclusiveFloor(int score, VerdictDisposition expected)
    {
        var d = SessionVerdict.Decide(Baseline with
        {
            Kind = SessionKind.Verify,
            VerifierParsed = true,
            VerifierScore = score,
            VerifierThreshold = 80,
        });

        Assert.Equal(expected, d.Disposition);
        Assert.Equal(expected == VerdictDisposition.VerifyPassed ? AttemptEffect.Reset : AttemptEffect.Increment, d.Attempts);
    }

    [Fact]
    public void AFailedVerifyHandsTheFixSessionTheArithmeticItWasJudgedBy()
    {
        var d = SessionVerdict.Decide(Baseline with
        {
            Kind = SessionKind.Verify,
            VerifierParsed = true,
            VerifierScore = 61,
            VerifierThreshold = 75,
        });

        Assert.Equal("verifier score 61/100 < threshold 75", d.Reason);
        Assert.Equal(SessionOutcome.NoProgress, d.Outcome);
    }

    [Fact]
    public void AnUnparseableVerifierScoreIsAnAgentErrorNotAPass()
    {
        var d = SessionVerdict.Decide(Baseline with { Kind = SessionKind.Verify, VerifierParsed = false, VerifierScore = 100 });

        Assert.Equal(VerdictDisposition.VerifyUnparseable, d.Disposition);
        Assert.Equal(SessionOutcome.AgentError, d.Outcome);
        Assert.Equal(AttemptEffect.Increment, d.Attempts);
    }

    [Fact]
    public void NothingCheapSettlesADeliverSessionSoTheBatteryIsBought()
    {
        var d = SessionVerdict.Decide(Baseline);

        Assert.Equal(VerdictDisposition.RunGateBattery, d.Disposition);
        Assert.Null(d.Outcome);
        // The healthy path resets the multiplier and deliberately does not touch the instant.
        Assert.Equal(new StallBackoffPlan(1, null, TouchesUntil: false), d.Backoff);
    }

    // ── after the battery ──

    [Fact]
    public void CancellationDuringTheBatteryQueuesAResumeAndNoFix()
    {
        var d = SessionVerdict.Decide(Baseline with { GatesRun = true, Cancelled = true, GatesGreen = false });

        Assert.Equal(VerdictDisposition.Interrupted, d.Disposition);
        Assert.Equal(SessionOutcome.Interrupted, d.Outcome);
        // A cancelled verification burns no attempt: the session was never judged.
        Assert.Equal(AttemptEffect.Unchanged, d.Attempts);
        Assert.Equal("conductor was cancelled during gate verification", d.Reason);
    }

    [Fact]
    public void GatesInAndNothingAbortedMeansTheTrackerStillHasToBeRead()
    {
        var d = SessionVerdict.Decide(Baseline with { GatesRun = true, GatesGreen = true });

        Assert.Equal(VerdictDisposition.ReadWorkEvidence, d.Disposition);
        Assert.Null(d.Outcome);
    }

    // ── the delivery verdict ──

    [Theory]
    [InlineData(1, 0, false)]   // a commit in the primary repo or a declared satellite
    [InlineData(0, 1, false)]   // SC4.3: a checkpoint claimed through the work graph, empty git log
    [InlineData(0, 0, true)]    // W1.3: the stage finished on someone else's claim
    public void AnyOneOfTheThreeDeliverySignalsIsDelivery(int commits, int newlyDone, bool stageComplete)
    {
        var d = SessionVerdict.Decide(Graded with
        {
            WorkCommitCount = commits,
            NewlyDoneCount = newlyDone,
            StageComplete = stageComplete,
        });

        Assert.Equal(VerdictDisposition.Deliver, d.Disposition);
        Assert.True(d.ReturnToIdle);
    }

    [Fact]
    public void NoneOfTheThreeSignalsIsNoProgressEvenWithEveryGateGreen()
    {
        var d = SessionVerdict.Decide(Graded);

        Assert.Equal(VerdictDisposition.QueueFix, d.Disposition);
        Assert.Equal(SessionOutcome.NoProgress, d.Outcome);
        Assert.Equal(AttemptEffect.Increment, d.Attempts);
    }

    [Fact]
    public void AFlippedCheckpointGivesTheStageItsAttemptsBackAndWorkAloneDoesNot()
    {
        var advanced = SessionVerdict.Decide(Graded with { NewlyDoneCount = 1, WorkCommitCount = 3 });
        var progress = SessionVerdict.Decide(Graded with { NewlyDoneCount = 0, WorkCommitCount = 3 });

        Assert.Equal(SessionOutcome.Advanced, advanced.Outcome);
        Assert.Equal(AttemptEffect.Reset, advanced.Attempts);
        Assert.Equal(SessionOutcome.Progress, progress.Outcome);
        Assert.Equal(AttemptEffect.Unchanged, progress.Attempts);
    }

    [Theory]
    [InlineData(true, true, 4, SessionOutcome.AgentError)]    // an errored agent is an agent error whatever it committed
    [InlineData(false, true, 0, SessionOutcome.NoProgress)]   // green gates, nothing delivered anywhere
    [InlineData(false, false, 4, SessionOutcome.GatesRed)]    // work landed and the gates say it is broken
    [InlineData(true, false, 4, SessionOutcome.AgentError)]   // the agent error outranks the red gates
    public void TheRedOutcomeTaxonomyNamesWhichKindOfRed(bool agentErrored, bool gatesGreen, int commits, SessionOutcome expected)
    {
        var d = SessionVerdict.Decide(Graded with { AgentErrored = agentErrored, GatesGreen = gatesGreen, WorkCommitCount = commits });

        Assert.Equal(expected, d.Outcome);
        Assert.Equal(VerdictDisposition.QueueFix, d.Disposition);
    }

    [Fact]
    public void ANewlyBlockedCheckpointParksOnlyWhereThePlanAsksForIt()
    {
        var blocked = Graded with { NewlyBlocked = ["KS6.4"], WorkCommitCount = 2 };

        var parked = SessionVerdict.Decide(blocked with { PauseOnBlocked = true });
        Assert.Equal(VerdictDisposition.ParkForHuman, parked.Disposition);
        Assert.Equal("checkpoint(s) newly BLOCKED: KS6.4 — see tracker handoff", parked.Reason);
        // A park is not a verdict on the work: no outcome is stamped and no attempt is spent.
        Assert.Null(parked.Outcome);
        Assert.Equal(AttemptEffect.Unchanged, parked.Attempts);

        Assert.Equal(VerdictDisposition.Deliver, SessionVerdict.Decide(blocked with { PauseOnBlocked = false }).Disposition);
    }

    [Fact]
    public void TheBreakerOnTheDeliveryPathDoesReturnToIdleWhereTheStallOneDoesNot()
    {
        var d = SessionVerdict.Decide(Graded with
        {
            GatesGreen = false,
            CircuitBreakerEnabled = true,
            SameFailurePattern = true,
        });

        Assert.Equal(VerdictDisposition.ConsultAdvisor, d.Disposition);
        Assert.Equal(SessionOutcome.GatesRed, d.Outcome);
        Assert.Equal(AdvisorAction.NeedsHuman, d.AdvisorDefault);
        Assert.True(d.ReturnToIdle);
    }

    // ── properties of the function as a whole ──

    /// <summary>Every disposition the taxonomy declares is produced by some evidence here. A disposition
    /// nothing can reach is either dead code or a branch this table has stopped covering.</summary>
    [Fact]
    public void EveryDispositionIsReachableFromTheTable()
    {
        var reached = Table.Select(row => SessionVerdict.Decide(row.Evidence).Disposition).ToHashSet();
        var declared = Enum.GetValues<VerdictDisposition>().ToHashSet();

        Assert.Equal(declared.OrderBy(x => x), reached.OrderBy(x => x));
    }

    [Fact]
    public void TheDecisionIsAFunctionOfTheEvidenceAlone()
    {
        foreach (var (name, evidence) in Table)
            Assert.Equal(SessionVerdict.Decide(evidence), SessionVerdict.Decide(evidence));
    }

    /// <summary>
    /// KS4.5's seam, stated the only way that is worth anything: negatively. A second-model review
    /// joins the evidence taxonomy as an advisory row and the deterministic signals still decide, so
    /// varying that row across its whole range — absent, glowing, damning, several at once — must not
    /// move a single decision anywhere in the table.
    /// </summary>
    [Fact]
    public void AnAdvisoryRowNeverChangesAVerdict()
    {
        AdvisoryEvidence[][] variants =
        [
            [],
            [new AdvisoryEvidence("judge:claude", "pass", 100, "flawless")],
            [new AdvisoryEvidence("judge:claude", "fail", 0, "this must not ship")],
            [
                new AdvisoryEvidence("judge:a", "fail", 0, "reject"),
                new AdvisoryEvidence("judge:b", "pass", 99, "accept"),
            ],
        ];

        foreach (var (name, evidence) in Table)
        {
            var baseline = SessionVerdict.Decide(evidence with { AdvisoryRows = [] });
            foreach (var rows in variants)
            {
                Assert.Equal(baseline, SessionVerdict.Decide(evidence with { AdvisoryRows = rows }));
            }
        }
    }

    /// <summary>The dirty-tree flag is reported by the verdict-inputs line and written into the fix
    /// brief, and it has never decided anything. If that ever changes it changes here first.</summary>
    [Fact]
    public void ADirtyWorkingTreeIsReportedAndIsNotEvidence()
    {
        foreach (var (name, evidence) in Table)
        {
            Assert.Equal(
                SessionVerdict.Decide(evidence with { WorkingTreeDirty = false }),
                SessionVerdict.Decide(evidence with { WorkingTreeDirty = true }));
        }
    }

    // ── the source rules that keep the function pure ──

    private static readonly Regex Comments = new(
        @"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    /// <summary>The four files the verdict surface lives in: the function, the evidence it reads, the
    /// decision it returns and the dispositions that decision can name. Nothing else may join them.</summary>
    private static readonly string[] VerdictSurface =
        ["SessionVerdict.cs", "SessionEvidence.cs", "VerdictDecision.cs", "VerdictDisposition.cs"];

    private static string CodeOnly(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "Conductor.Core", "Orchestration", fileName);
        Assert.True(File.Exists(path), $"KS6.4: the pure verdict surface has moved or been deleted — {path}");
        return Comments.Replace(File.ReadAllText(path), " ");
    }

    /// <summary>The function was cut out of a method that touched a store, a git repository, a clock and
    /// an agent process. Nothing stops it drifting back except a rule that reads the source.</summary>
    [Fact]
    public void TheVerdictFunctionNamesNothingImpure()
    {
        string[] banned =
        [
            "_ctx", "RunContext", "Git.", "File.", "Directory.", "DateTime", "DateTimeOffset",
            "Store", "Process", "Console", "Random", "Environment.", "async ", "Task<", "HttpClient",
        ];
        var violations = new List<string>();

        foreach (var file in VerdictSurface)
        {
            var code = CodeOnly(file);
            foreach (var token in banned.Where(b => code.Contains(b, StringComparison.Ordinal)))
                violations.Add($"  {file} reached for {token}");

            // Model vocabulary only. A third namespace is how this starts going wrong.
            foreach (var line in code.Split('\n').Select(l => l.Trim())
                         .Where(l => l.StartsWith("using ", StringComparison.Ordinal))
                         .Where(l => l is not ("using Conductor.Models;" or "using Conductor.Planning;")))
                violations.Add($"  {file} imports {line}");
        }

        Assert.True(violations.Count == 0,
            "KS6.4: the pure verdict surface stopped being pure:\n" + string.Join("\n", violations));
    }

    /// <summary>The behavioural proof above can only sample the evidence space. This one is total: the
    /// advisory rows are named exactly once across the whole surface, by the property that declares
    /// them, and never once in the file that decides anything.</summary>
    [Fact]
    public void NothingInTheDecisionPathReadsTheAdvisoryRows()
    {
        var counts = VerdictSurface
            .Select(f => (File: f, Count: Regex.Matches(CodeOnly(f), "AdvisoryRows", RegexOptions.None, TimeSpan.FromSeconds(5)).Count))
            .ToList();
        var mentions = counts.Sum(c => c.Count);

        Assert.True(mentions == 1,
            $"KS6.4: AdvisoryRows is named {mentions} times across the verdict surface; exactly one - the " +
            "declaration - is what makes a judge evidence and never a verdict a property of the code rather " +
            "than a promise. Per file: " + string.Join(", ", counts.Select(c => $"{c.File}={c.Count}")));
        Assert.DoesNotContain("AdvisoryRows", CodeOnly("SessionVerdict.cs"), StringComparison.Ordinal);
    }

    // ── the table every whole-function property runs over ──

    /// <summary>Post-battery evidence with the tracker read: green gates, nothing delivered.</summary>
    private static SessionEvidence Graded => Baseline with { GatesRun = true, WorkEvidenceRead = true, GatesGreen = true };

    private static IReadOnlyList<(string Name, SessionEvidence Evidence)> Table =>
    [
        ("killed", Baseline with { KilledByUser = true }),
        ("stall/resume", Baseline with { Stalled = true }),
        ("stall/breaker", Baseline with { Stalled = true, CircuitBreakerEnabled = true, SameFailurePattern = true }),
        ("stall/park", Baseline with { Stalled = true, StallPatternTerminationEnabled = true, IdenticalStallPattern = true }),
        ("timeout/exhausted", Baseline with { TimedOut = true, ResumeCount = 9 }),
        ("wait", Baseline with { BlockedUntilRequested = true }),
        ("audit", Baseline with { Kind = SessionKind.Audit }),
        ("verify/pass", Baseline with { Kind = SessionKind.Verify, VerifierParsed = true, VerifierScore = 90 }),
        ("verify/fail", Baseline with { Kind = SessionKind.Verify, VerifierParsed = true, VerifierScore = 10 }),
        ("verify/unparseable", Baseline with { Kind = SessionKind.Verify }),
        ("deliver/needs-battery", Baseline),
        ("cancelled", Baseline with { GatesRun = true, Cancelled = true }),
        ("gates-in", Baseline with { GatesRun = true }),
        ("green/advanced", Graded with { NewlyDoneCount = 2, WorkCommitCount = 5 }),
        ("green/progress", Graded with { WorkCommitCount = 5 }),
        ("red/gates", Graded with { GatesGreen = false }),
        ("red/agent-error", Graded with { AgentErrored = true, WorkCommitCount = 1 }),
        ("red/breaker", Graded with { GatesGreen = false, CircuitBreakerEnabled = true, SameFailurePattern = true }),
        ("blocked/park", Graded with { NewlyBlocked = ["X1"], PauseOnBlocked = true, WorkCommitCount = 1 }),
    ];
}
