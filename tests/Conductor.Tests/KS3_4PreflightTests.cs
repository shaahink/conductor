using System.Text;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Core.Update;
using Conductor.Hosting;
using Conductor.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Spectre.Console.Cli;

namespace Conductor.Tests;

/// <summary>
/// KS3.4 — <c>conductor preflight</c>: the launch drill as one verb, six legs, one verdict.
///
/// <para>The drill it replaces was a checklist typed by hand, so the tests are shaped the same way
/// the drill's failures arrive: one seeded fixture per leg, each one a plan that is clean in every
/// other respect, asserting that the leg it broke is the leg the verdict names. A drill that reported
/// "something is wrong" would be no better than the checklist.</para>
///
/// <para>The escalation fixture never spells the token. It is read out of
/// <c>plan.conventions.humanToken</c> at runtime, because the match that parks a run is a plain
/// substring and a fixture carrying the literal would park the run reading it.</para>
///
/// <para>Two facts about the environment are STATED rather than inherited: the engine image the
/// rebuild leg judges (a test suite hosted in somebody else's process must not have its verdict
/// decided by where that process's exe sits) and the release feed's answer. Everything else is real —
/// a real git repo, a real tracker, doctor's real check list.</para>
/// </summary>
public sealed class KS3_4PreflightTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "conductor-ks34-" + Guid.NewGuid().ToString("N")[..10]);

    public KS3_4PreflightTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (Exception) { /* best effort */ }
    }

    // ------------------------------------------------------------------ the clean bar

    [Fact]
    public async Task CleanPlan_EveryLegClears_AndTheVerdictIsReady()
    {
        var legs = await RunAsync(CleanPlan());

        Assert.Equal(6, legs.Count);
        Assert.Equal(
            ["doctor", "journey", "compose", "version", "rebuild", "escalation"],
            legs.Select(l => l.Name).ToArray());
        Assert.Empty(Failing(legs));
    }

    // ------------------------------------------------------------------ (a) agent.command not on PATH

    [Fact]
    public async Task AgentNotOnPath_FailsTheDoctorLeg_AndOnlyThatLeg()
    {
        var plan = CleanPlan(p => p.Agent = new AgentConfig
        {
            Command = "definitely-not-a-real-agent-xyz123",
            Args = ["-p", "{prompt}"],
        });

        var legs = await RunAsync(plan);

        Assert.Equal(["doctor"], Failing(legs));
        Assert.Contains("not found on PATH", Detail(legs, "doctor"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (b) pinned model that never reaches the CLI

    [Fact]
    public async Task PinnedModelWithNoPlaceholder_FailsTheJourneyLeg_AndOnlyThatLeg()
    {
        var plan = CleanPlan(p => p.Agent = new AgentConfig
        {
            Command = "git",
            Model = "some-pinned-model",
            Args = ["-p", "{prompt}"],          // no {model}: the CLI runs its own default
        });

        var legs = await RunAsync(plan);

        Assert.Equal(["journey"], Failing(legs));
        Assert.Contains("{model}", Detail(legs, "journey"), StringComparison.Ordinal);
    }

    /// <summary>The other half of the journey leg: a workflow name nothing answers. WorkflowEngine
    /// falls back to deliver-verify for a name it does not know, silently, so the plan says one
    /// lifecycle and the run executes another.</summary>
    [Fact]
    public async Task UnknownWorkflowName_FailsTheJourneyLeg_AndNamesIt()
    {
        var plan = CleanPlan(p => p.Stages[0].Workflow = "delivr-verify");

        var legs = await RunAsync(plan);

        Assert.Equal(["journey"], Failing(legs));
        Assert.Contains("delivr-verify", Detail(legs, "journey"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (c) an unresolvable placeholder in a template

    [Fact]
    public async Task UnresolvableTemplatePlaceholder_FailsTheComposeLeg_AndOnlyThatLeg()
    {
        var plan = CleanPlan();
        plan.TemplatesDir = "templates";
        var dir = Path.Combine(plan.PlanDir, "templates");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "session.md"),
            "Deliver stage {stage} for {plan}.\n\nAnd then do {whatever_this_is}.\n", Utf8);

        var legs = await RunAsync(plan);

        Assert.Equal(["compose"], Failing(legs));
        Assert.Contains("whatever_this_is", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (d) a newer release than the running engine

    [Fact]
    public void ANewerReleaseFailsTheVersionLeg()
    {
        var current = Version("2.0.0");
        var status = UpdateStatus.Decide(current, new GithubRelease { TagName = "v99.1.0" }, null);
        Assert.True(status.Available);

        var leg = PreflightCommand.VersionLeg(new DoctorCommand.Check("update", "warn", status.Detail), status);

        Assert.Equal("version", leg.Name);
        Assert.Equal("fail", leg.State);
        Assert.Contains("v99.1.0", leg.Headline, StringComparison.Ordinal);
    }

    /// <summary>An unanswerable feed is not a failed drill. A laptop with no network must still be
    /// able to launch a run, so this leg is green whenever nothing newer was actually found.</summary>
    [Fact]
    public void AnUnreachableFeedDoesNotFailTheVersionLeg()
    {
        var status = UpdateStatus.Decide(Version("2.0.0"), null, "no network");
        var leg = PreflightCommand.VersionLeg(new DoctorCommand.Check("update", "ok", status.Detail), status);
        Assert.Equal("ok", leg.State);
        Assert.False(status.Known);
    }

    [Fact]
    public void RunningTheLatestReleaseKeepsTheVersionLegGreen()
    {
        var status = UpdateStatus.Decide(Version("2.0.0"), new GithubRelease { TagName = "v2.0.0" }, null);
        var leg = PreflightCommand.VersionLeg(new DoctorCommand.Check("update", "ok", status.Detail), status);
        Assert.Equal("ok", leg.State);
    }

    /// <summary>Fixture (d) IN the suite, live path and all: <c>VersionLegAsync</c> with the check ON,
    /// against a loopback feed serving a GitHub-shaped release document — the exact seam
    /// <c>CONDUCTOR_UPDATE_FEED</c> documents for this. The probe's six-hour memo lands in the
    /// suite's isolated state home (<c>UpdateCheckCache.Path</c> follows
    /// <c>CONDUCTOR_STATE_HOME</c>), never in the operator's real cache — and a memo written against
    /// an override feed is refused by <c>ReadFresh</c> anyway.</summary>
    [Fact]
    public async Task TheLiveVersionLegFailsWhenTheFeedServesANewerRelease()
    {
        Assert.StartsWith(TestEnvironmentIsolation.StateHomeRoot, UpdateCheckCache.Path,
            StringComparison.OrdinalIgnoreCase);

        using var feed = new FakeReleaseFeed(
            """{"tag_name":"v99.1.0","html_url":"http://127.0.0.1/releases/v99.1.0"}""");
        var prior = Environment.GetEnvironmentVariable(ReleaseClient.FeedEnvVar);
        Environment.SetEnvironmentVariable(ReleaseClient.FeedEnvVar, feed.Url);
        try
        {
            var leg = await PreflightCommand.VersionLegAsync(updateCheck: true, DateTimeOffset.UtcNow);

            Assert.Equal("version", leg.Name);
            Assert.Equal("fail", leg.State);
            Assert.Contains("v99.1.0", leg.Headline, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseClient.FeedEnvVar, prior);
        }
    }

    /// <summary>And the live path's other branch: the check switched off consults nothing and says
    /// so — the leg an offline launch reads.</summary>
    [Fact]
    public async Task TheLiveVersionLegStaysQuietWhenTheCheckIsOff()
    {
        var leg = await PreflightCommand.VersionLegAsync(updateCheck: false, DateTimeOffset.UtcNow);
        Assert.Equal("ok", leg.State);
        Assert.Contains("release feed not consulted", leg.Headline, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (e) a stale engine

    [Fact]
    public void SourcesNewerThanTheEngineImage_FailTheRebuildLeg()
    {
        var plan = CleanPlan();
        var tree = SeedEngineTree(sourceWrittenUtc: DateTime.UtcNow);
        plan.Repo = tree;

        var leg = PreflightCommand.RebuildLeg(plan, StaleImage(tree), pathBinary: null);

        Assert.Equal("rebuild", leg.Name);
        Assert.Equal("fail", leg.State);
        Assert.Contains("OLDER than the sources", leg.Headline, StringComparison.Ordinal);
        Assert.Contains("Newer.cs", string.Join(" ", leg.Detail), StringComparison.Ordinal);
        Assert.Contains("rebuild before launching", string.Join(" ", leg.Detail), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEngineNewerThanItsSourcesKeepsTheRebuildLegGreen()
    {
        var plan = CleanPlan();
        var tree = SeedEngineTree(sourceWrittenUtc: DateTime.UtcNow.AddHours(-2));
        plan.Repo = tree;

        var leg = PreflightCommand.RebuildLeg(plan, FreshImage(tree), pathBinary: null);

        Assert.Equal("ok", leg.State);
    }

    /// <summary>Silent on every ordinary repo: a plan driving somebody else's project has no engine
    /// sources that could be newer than anything, and the leg says exactly that rather than
    /// inventing a worry.</summary>
    [Fact]
    public void APlanThatDrivesAnOrdinaryRepoHasNothingToRebuild()
    {
        var plan = CleanPlan();
        var leg = PreflightCommand.RebuildLeg(plan, FreshImage(_dir), pathBinary: null);
        Assert.Equal("ok", leg.State);
        Assert.Contains("no conductor source tree", leg.Headline, StringComparison.Ordinal);
    }

    /// <summary>Trap 2 made visible: the engine answering and the <c>conductor</c> a hand-typed launch
    /// would spawn are two different files, and nothing else on any surface says so.</summary>
    [Fact]
    public void ADifferentConductorOnPathIsNamed()
    {
        var plan = CleanPlan();
        var elsewhere = Path.Combine(_dir, "installed", "conductor.exe");
        var leg = PreflightCommand.RebuildLeg(plan, FreshImage(_dir), pathBinary: elsewhere);

        Assert.Equal("warn", leg.State);
        Assert.Contains("on PATH", string.Join(" ", leg.Detail), StringComparison.Ordinal);
        Assert.Contains(elsewhere, string.Join(" ", leg.Detail), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ (f) an escalation left in the handoff

    [Fact]
    public async Task AnEscalationInTheHandoff_FailsTheEscalationLeg_AndOnlyThatLeg()
    {
        var plan = CleanPlan();
        // Never a literal: the token is whatever this plan's conventions declare, assembled here so
        // this file cannot itself park a run that reads it.
        WriteTracker(plan, handoff: plan.Conventions.HumanToken + " decide whether to keep the old ingest path");

        var legs = await RunAsync(plan);

        Assert.Equal(["escalation"], Failing(legs));
        Assert.Contains("already asks for a human", Headline(legs, "escalation"), StringComparison.Ordinal);

        // The compose leg tells the same truth the loop would enact — a park before any session —
        // without going red itself, so the seeded failure still names exactly one leg.
        Assert.Equal(
            "the next `conductor run` parks at NeedsHuman — the tracker handoff asks for a human — no session composes",
            Headline(legs, "compose"));
    }

    /// <summary>And the drill's own output may never carry the token — a preflight that printed it
    /// into a handoff would be the failure it exists to catch.</summary>
    [Fact]
    public async Task TheDrillNeverPrintsTheEscalationTokenBack()
    {
        var plan = CleanPlan();
        WriteTracker(plan, handoff: plan.Conventions.HumanToken + " decide whether to keep the old ingest path");

        var legs = await RunAsync(plan);
        var printed = string.Join("\n", legs.Select(l => l.Headline + "\n" + string.Join("\n", l.Detail)));

        Assert.DoesNotContain(plan.Conventions.HumanToken, printed, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ read-only

    /// <summary>Preflight advertises itself as read-only, so the file set under
    /// <c>plan.StateDir</c> is identical after a full drill: no <c>run.db</c>, no <c>state.json</c>,
    /// no <c>control-plane.json</c>, and nothing renamed either.</summary>
    [Fact]
    public async Task AFullDrillCreatesNothingUnderTheStateDir()
    {
        var plan = CleanPlan();
        Directory.CreateDirectory(plan.StateDir);
        await File.WriteAllTextAsync(Path.Combine(plan.StateDir, "witness.txt"), "before", Utf8);
        var before = Snapshot(plan.StateDir);

        var legs = await RunAsync(plan);
        Assert.Empty(Failing(legs));

        Assert.Equal(before, Snapshot(plan.StateDir));
    }

    /// <summary>The same promise with a legacy <c>state.json</c> left by ANOTHER plan in the way.
    /// <c>RunState.LoadOrNew</c> renames that file — correct for <c>conductor run</c>, forbidden for a
    /// drill, which is why the resume peek declines to archive it.</summary>
    [Fact]
    public async Task AForeignLegacyStateFileIsNotMovedByTheDrill()
    {
        var plan = CleanPlan();
        Directory.CreateDirectory(plan.StateDir);
        await File.WriteAllTextAsync(Path.Combine(plan.StateDir, "state.json"),
            "{\"planName\":\"some-other-plan\",\"runId\":\"\",\"sessionCounter\":0}", Utf8);
        var before = Snapshot(plan.StateDir);

        await RunAsync(plan);

        Assert.Equal(before, Snapshot(plan.StateDir));
    }

    // ------------------------------------------------------------------ what the compose leg says

    [Fact]
    public async Task TheComposeLegNamesTheSessionThatWouldRunNext()
    {
        var legs = await RunAsync(CleanPlan());
        var compose = legs.Single(l => l.Name == "compose");

        Assert.Equal("ok", compose.State);
        Assert.Contains("next session #1", compose.Headline, StringComparison.Ordinal);
        Assert.Contains("Deliver", compose.Headline, StringComparison.Ordinal);
        Assert.Contains("stage 'S1'", compose.Headline, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the leg and the loop agree

    /// <summary>The first delivery of this checkpoint answered "which stage runs next" with a rule of
    /// its own — the state's current stage, else the first the tracker does not read done. That rule
    /// cannot see a <c>dependsOn</c>, so preflight named S1 while <c>run --dry-run</c> on the same plan
    /// named S2, and the char count then measured a different stage's prompt. The rule is
    /// <see cref="StageSelection"/> now, so the leg and the loop cannot disagree; this fact is the
    /// disagreement, pinned.</summary>
    [Fact]
    public async Task ADeclaredDependencyMovesTheNextSessionToTheStageTheLoopWouldPick()
    {
        var plan = CleanPlan(p =>
        {
            p.Stages[0].DependsOn = ["S2"];
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the one that has to land first", Sessions = 1 });
        });
        WriteTracker(plan, "nothing pending.", ("S1.1", "TODO"), ("S2.1", "TODO"));

        var legs = await RunAsync(plan);

        Assert.Contains("stage 'S2'", Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.DoesNotContain("stage 'S1'", Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.Equal("S2", StageSelection.Select(plan, new RunState(), Track(plan))?.Id);
    }

    /// <summary>The second half of the same finding: a stage the owner skipped is not the next
    /// session, even when the saved state is still standing in it.</summary>
    [Fact]
    public async Task ASkippedStageIsNeverTheNextSession()
    {
        var plan = CleanPlan(p =>
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the one still open", Sessions = 1 }));
        WriteTracker(plan, "nothing pending.", ("S1.1", "TODO"), ("S2.1", "TODO"));
        SaveState(plan, s =>
        {
            s.CurrentStage = "S1";
            s.SessionCounter = 3;
            s.SkippedStages.Add("S1");
        });

        var legs = await RunAsync(plan);

        Assert.Contains("next session #4", Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("stage 'S2'", Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>And under <c>perPhaseGates</c> done-ness is CONFIRMED-ness — but a stage whose rows
    /// all read DONE while it is unconfirmed is not "the next session" either: the loop SCHEDULES THE
    /// AUDIT / full-battery phase gate for it and composes nothing. The first version of this fact
    /// blessed the wrong sentence with a substring — <c>Contains("stage 'S1'")</c> matched a
    /// <c>next session #6 is Deliver on stage 'S1'</c> headline the loop contradicts (round 2's
    /// blocking finding) — so it now pins the WHOLE headline, and
    /// <see cref="TheScheduledGateHeadlineAgreesWithWhatRunDryRunPrints"/> pins the same fixture
    /// against the real loop's own dry-run narration.</summary>
    [Fact]
    public async Task UnderPerPhaseGatesADoneButUnconfirmedStageSchedulesItsGateNotASession()
    {
        var plan = PerPhaseDoneUnconfirmedFixture();

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.Equal(
            "stage 'S1' checkpoints all read DONE but the stage is unconfirmed — the next " +
            "`conductor run` schedules the audit / full-battery phase gate — no session composes",
            Headline(legs, "compose"));
    }

    /// <summary>The same fixture through the REAL loop — ConductorHost, Orchestrator, dry run — so
    /// the agreement is measured against what `conductor run` actually narrates, not against this
    /// suite's opinion of it.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheScheduledGateHeadlineAgreesWithWhatRunDryRunPrints()
    {
        var plan = PerPhaseDoneUnconfirmedFixture();
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);

        Assert.Contains(lines, l => l.Contains(
            "DRY RUN: stage S1 checkpoints all DONE — would schedule the audit / full-battery phase gate next",
            StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("would start session #", StringComparison.Ordinal));
        Assert.DoesNotContain("composing to", Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>Round 2's other live reproduction: a queued <c>pendingPhaseGate</c> runs before
    /// anything else gets a turn, so the drill names the gate — green, because a queued gate is
    /// normal operation, and with a whole headline that promises no session and no char count.</summary>
    [Fact]
    public async Task AQueuedPhaseGateIsNamedInsteadOfASessionAndTheDrillStaysGreen()
    {
        var plan = QueuedPhaseGateFixture();

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.Equal(
            "the next `conductor run` runs the queued full-battery phase gate for stage 'S1' — no session composes",
            Headline(legs, "compose"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheQueuedGateHeadlineAgreesWithWhatRunDryRunPrints()
    {
        var plan = QueuedPhaseGateFixture();
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);

        Assert.Contains(lines, l => l.Contains(
            "DRY RUN: would run the FULL-battery phase gate for stage S1", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("would start session #", StringComparison.Ordinal));
        Assert.DoesNotContain("composing to", Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>A run already standing at <c>limits.maxSessions</c> parks at the session boundary
    /// without composing anything — the old leg promised a numbered session for a prompt the loop
    /// would never build. A park-on-launch is a launch failure, so this one is red. (The dry-run
    /// companion used to spin forever on a capped plan — the park branch was written for a live
    /// run — until round 3 modelled the persisted park itself: the cap parks on the first turn and
    /// the second turn now reports the parked status and exits, see
    /// <see cref="TheParkedStatusAgreesWithWhatRunDryRunPrints"/>.)</summary>
    [Fact]
    public async Task ARunAlreadyAtItsSessionCapFailsTheComposeLeg()
    {
        var plan = CleanPlan(p => p.Limits.MaxSessions = 3);
        SaveState(plan, s => { s.CurrentStage = "S1"; s.SessionCounter = 3; });

        var legs = await RunAsync(plan);

        Assert.Equal(["compose"], Failing(legs));
        Assert.Equal(
            "session cap reached (3/3) — the next `conductor run` parks at the session boundary — no session composes",
            Headline(legs, "compose"));
        Assert.Contains("limits.maxSessions", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ round 3: the persisted parks

    /// <summary>Round 3's blocking finding (1): a saved run whose status is Paused / NeedsHuman /
    /// AwaitingOwner is the persisted residue of an escalation — <c>RunLoop</c> idles on it at the
    /// session boundary forever, and <c>RecoverFromCrash</c> resets only a crash's statuses. The leg
    /// used to fall through to Compose and the verdict line then prescribed <c>conductor run</c>, a
    /// command that can only idle there; <c>conductor journey</c> on the identical fixture already
    /// said <c>status NeedsHuman</c>. Red, and the detail names the verb that actually continues the
    /// run: <c>conductor resume</c>.</summary>
    [Theory]
    [InlineData(RunStatus.Paused)]
    [InlineData(RunStatus.NeedsHuman)]
    [InlineData(RunStatus.AwaitingOwner)]
    public async Task AParkedSavedStatusFailsTheComposeLeg_AndNamesResume(RunStatus parked)
    {
        var plan = CleanPlan();
        SaveState(plan, s =>
        {
            s.RunId = "r1parked";
            s.CurrentStage = "S1";
            s.SessionCounter = 4;
            s.Status = parked;
            s.SetAttention("agent asked for a human in the tracker handoff");
        });

        var legs = await RunAsync(plan);

        Assert.Equal(["compose"], Failing(legs));
        Assert.Equal(
            $"the saved run is parked — state.json says status {parked} — the next `conductor run` " +
            "idles at the session boundary and spawns nothing",
            Headline(legs, "compose"));
        Assert.Contains("`conductor resume`", Detail(legs, "compose"), StringComparison.Ordinal);
        Assert.DoesNotContain("composing to", Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>The rule itself, at the source: only the three statuses the loop idles on trip the
    /// park. Aborted is CONTINUED by <c>RecoverFromCrash</c>, Waiting is the declared-wait status the
    /// loop walks straight past, and Idle is a fresh run — a drill that flagged any of them would be
    /// refusing launches that work.</summary>
    [Theory]
    [InlineData(RunStatus.Idle)]
    [InlineData(RunStatus.Waiting)]
    [InlineData(RunStatus.Aborted)]
    public void OnlyTheStatusesTheLoopIdlesOnTripThePark(RunStatus status)
    {
        var plan = CleanPlan();
        var state = new RunState { PlanName = plan.Name, RunId = "r1", CurrentStage = "S1", SessionCounter = 1, Status = status };

        var next = StageSelection.NextAction(plan, state, Track(plan));

        Assert.NotEqual(LaunchStep.ParkedStatus, next.Step);
    }

    /// <summary>The dry-run agreement for the park: the REAL loop on the same fixture narrates the
    /// idle instead of announcing a session. (Before round 3 it said <c>would start session #5</c> —
    /// the exact sentence the drill's READY verdict was built on.)</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheParkedStatusAgreesWithWhatRunDryRunPrints()
    {
        var plan = CleanPlan();
        SaveState(plan, s =>
        {
            s.RunId = "r1parked";
            s.CurrentStage = "S1";
            s.SessionCounter = 4;
            s.Status = RunStatus.NeedsHuman;
        });

        var lines = await DryRunAsync(plan);

        Assert.Contains(lines, l => l.Contains(
            "DRY RUN: saved status is NeedsHuman", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("`conductor resume`", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("would start session #", StringComparison.Ordinal));
    }

    /// <summary>Round 3's blocking finding (2): a stage that has burned its whole attempt budget
    /// (<c>sessions × limits.stageSlackFactor</c>) never composes — the loop's escalation branch runs
    /// FIRST, and with no advisor configured it parks the run at NeedsHuman deterministically. The
    /// state is reachable at launch precisely because <c>conductor resume</c> does not reset the
    /// counter (only <c>retry-stage</c> and <c>goto</c> do), so it is exactly what an operator
    /// preflights before relaunching a parked run. Red, like the session cap: a park-on-launch is a
    /// launch failure, and with an advisor configured the "launch" the old READY line prescribed
    /// would begin with a model call.</summary>
    [Fact]
    public async Task AnExhaustedAttemptBudgetFailsTheComposeLeg()
    {
        var plan = CleanPlan();                          // S1 sessions: 1, stageSlackFactor default 2
        SaveState(plan, s =>
        {
            s.RunId = "r1burned";
            s.CurrentStage = "S1";
            s.SessionCounter = 2;
            s.AttemptsThisStage = 2;
        });

        var legs = await RunAsync(plan);

        Assert.Equal(["compose"], Failing(legs));
        Assert.Equal(
            "stage 'S1' has used all 2 attempts (2/2) — the next `conductor run` escalates instead of " +
            "composing — no session composes",
            Headline(legs, "compose"));
        Assert.Contains("`conductor retry-stage`", Detail(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("limits.stageSlackFactor", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>The exhaustion rule, at the source, in the loop's own terms: the counter is reset
    /// when the loop ENTERS a stage, so exhaustion only exists on the stage the state is standing
    /// in; and a queued audit gets its turn regardless — <c>RunLoop</c>'s own <c>PendingAudit</c>
    /// guard, without which the audit that closes an exhausted stage could never run.</summary>
    [Fact]
    public void ExhaustionExistsOnlyOnTheStandingStage_AndAQueuedAuditStillGetsItsTurn()
    {
        var plan = CleanPlan(p =>
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the next one", Sessions = 1 }));
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"), ("S2.1", "TODO"));
        var burned = new RunState { PlanName = plan.Name, RunId = "r1", CurrentStage = "S1", SessionCounter = 2, AttemptsThisStage = 2 };

        // The tracker moved on: the loop enters S2 and resets the counter, so no exhaustion.
        Assert.Equal(LaunchStep.Compose, StageSelection.NextAction(plan, burned, Track(plan)).Step);

        // Standing in S1 with S1 still open: exhausted.
        WriteTracker(plan, "nothing pending.", ("S1.1", "TODO"), ("S2.1", "TODO"));
        var next = StageSelection.NextAction(plan, burned, Track(plan));
        Assert.Equal(LaunchStep.ExhaustedAttempts, next.Step);
        Assert.Equal("S1", next.StageId);

        // A queued audit outranks the budget — the loop's own guard.
        burned.PendingAudit = new PendingAudit { StageId = "S1" };
        Assert.Equal(LaunchStep.Compose, StageSelection.NextAction(plan, burned, Track(plan)).Step);
    }

    /// <summary>The dry-run agreement for the budget: the REAL loop reports the exhaustion and
    /// terminates — it does not announce a session, and it does not consult the advisor, because an
    /// advisor consult is a model call and a dry run promises to spend nothing.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheExhaustedBudgetAgreesWithWhatRunDryRunPrints()
    {
        var plan = CleanPlan();
        SaveState(plan, s =>
        {
            s.RunId = "r1burned";
            s.CurrentStage = "S1";
            s.SessionCounter = 2;
            s.AttemptsThisStage = 2;
        });

        var lines = await DryRunAsync(plan);

        Assert.Contains(lines, l => l.Contains(
            "DRY RUN: stage S1 has used all 2 attempts", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("would start session #", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("consulting advisor", StringComparison.Ordinal));
    }

    /// <summary>Round 3's companion pin: an agent-declared wait (<c>state.blockedUntilUtc</c> in the
    /// future) does not change WHAT runs next, only WHEN — the loop sleeps at the session boundary
    /// and then spawns exactly the session the leg names. So the leg stays green, still names the
    /// session, and the detail names the sleep — a drill that promises session #2 without the hours
    /// in front of it is lying by omission.</summary>
    [Fact]
    public async Task ADeclaredWaitIsNamedBesideTheSessionItDefers()
    {
        var plan = CleanPlan();
        var wakes = DateTime.UtcNow.AddHours(2);
        SaveState(plan, s =>
        {
            s.RunId = "r1waits";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.Status = RunStatus.Waiting;
            s.BlockedUntilUtc = wakes;
            s.BlockedReason = "quota window resets on the hour";
        });

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.Contains("next session #2", Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("state.blockedUntilUtc", Detail(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("quota window resets on the hour", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>And the annotation at the source: in the future it rides on the decision; once the
    /// instant has passed the loop clears the wait and walks on, so the annotation must vanish with
    /// it — the clock is a stated parameter for exactly this assertion.</summary>
    [Fact]
    public void TheWaitAnnotationRidesTheDecisionOnlyWhileTheInstantIsAhead()
    {
        var plan = CleanPlan();
        var wakes = new DateTime(2026, 8, 14, 6, 0, 0, DateTimeKind.Utc);
        var state = new RunState { PlanName = plan.Name, RunId = "r1", CurrentStage = "S1", SessionCounter = 1, BlockedUntilUtc = wakes };

        var before = StageSelection.NextAction(plan, state, Track(plan), nowUtc: wakes.AddHours(-1));
        var after = StageSelection.NextAction(plan, state, Track(plan), nowUtc: wakes.AddHours(1));

        Assert.Equal(LaunchStep.Compose, before.Step);
        Assert.Equal(wakes, before.SleepUntilUtc);
        Assert.Equal(LaunchStep.Compose, after.Step);
        Assert.Null(after.SleepUntilUtc);
    }

    /// <summary>The clean fixture's agreement, to the character: the dry-run narrates the session
    /// number, kind and stage, then prints the composed prompt itself — so the char count in the
    /// drill's headline must equal the printed prompt's exact length. A substring can bless a wrong
    /// sentence; an equality on the whole headline cannot.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheComposeHeadlineAgreesWithRunDryRunToTheCharacter()
    {
        var plan = CleanPlan();
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);
        var announce = lines.FindIndex(l => l.Contains("DRY RUN: would start session #", StringComparison.Ordinal));
        Assert.True(announce >= 0, "the dry run never announced a session:\n" + string.Join("\n", lines));
        Assert.Contains("would start session #1 (Deliver, stage S1)", lines[announce], StringComparison.Ordinal);

        var prompt = lines[announce + 1];
        Assert.Equal(
            $"next session #1 is Deliver on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    /// <summary>A plan whose remaining stages are all blocked or skipped does not launch: the loop
    /// parks at NeedsHuman before spawning anything, having spent nothing and looking started. That is
    /// a launch failure and this is the only surface that can see it before it happens.</summary>
    [Fact]
    public async Task AStageGraphWithNothingRunnableFailsTheComposeLeg()
    {
        var plan = CleanPlan(p =>
        {
            p.Stages[0].DependsOn = ["S2"];
            p.Stages.Add(new StageConfig { Id = "S2", Title = "waits on the first", Sessions = 1, DependsOn = ["S1"] });
        });
        WriteTracker(plan, "nothing pending.", ("S1.1", "TODO"), ("S2.1", "TODO"));

        var legs = await RunAsync(plan);

        Assert.Equal(["compose"], Failing(legs));
        Assert.Contains("no stage is runnable", Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>The length reported is the length that goes into the argv: <c>RunLoop</c>'s dry-run
    /// branch appends <c>BatterySection</c> after building the prompt, and a leg that carries doctor's
    /// 8191-char <c>argv</c> check must measure the same string. It did not, and on a resumed run with
    /// a recorded gate failure the count understated the real prompt by a whole section.</summary>
    [Fact]
    public async Task TheMeasuredLengthIncludesTheBatterySectionTheLoopAppends()
    {
        var plan = CleanPlan();
        plan.Batteries = new BatteriesConfig { Lessons = false, RecentFailure = true, Ledger = false, Bugs = false };
        var state = SaveState(plan, s =>
        {
            s.SessionCounter = 2;
            s.CurrentStage = "S1";
            s.History.Add(new SessionRecord
            {
                Number = 2,
                Stage = "S1",
                Outcome = SessionOutcome.GatesRed,
                GateSummary = "engine-fast:OK engine-full:FAIL",
                ResultSummary = "two tests red in the store migration",
            });
        });

        var prompts = new PromptBuilder(plan);
        var battery = prompts.BatterySection(state, store: null);
        Assert.NotEqual("", battery);
        var bare = prompts.Deliver(plan.Stages[0], 3, 1, Math.Max(1, plan.Stages[0].Sessions * plan.Limits.StageSlackFactor));
        var expected = bare.TrimEnd().Length + 2 + battery.Length;

        var legs = await RunAsync(plan);

        Assert.Contains($"composing to {expected} chars", Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.True(expected > bare.Length, "the seeded battery must actually lengthen the prompt");
    }

    /// <summary>The one part of the real prompt this drill will not open: the ledger and open-bug
    /// batteries live in <c>run.db</c>, and opening that store creates and migrates it. Rather than
    /// break the read-only promise or quietly understate the number, the leg states the ceiling.</summary>
    [Fact]
    public async Task TheUnopenedKnowledgeBatteriesAreReportedAsACeiling()
    {
        var plan = CleanPlan();
        // A real store, made by the TEST — a run that has history is the only run whose ledger and
        // open bugs could add anything the drill did not measure.
        using (new SqliteRunStore(plan.RunDbPath, NullLogger<SqliteRunStore>.Instance)) { }
        SaveState(plan, s => { s.RunId = "run-abc"; s.SessionCounter = 1; s.CurrentStage = "S1"; });

        var legs = await RunAsync(plan);

        var bare = new PromptBuilder(plan).Deliver(plan.Stages[0], 2, 1,
            Math.Max(1, plan.Stages[0].Sessions * plan.Limits.StageSlackFactor));
        // The cap covers the section as a whole, plus at most two characters of truncation tail —
        // and it is taken off the BARE prompt, so a measured battery is never counted twice.
        Assert.Contains($"at most {bare.TrimEnd().Length + 2 + 2048 + 2} chars", Detail(legs, "compose"),
            StringComparison.Ordinal);
        Assert.Contains("batteries.maxBytes 2048", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>Silent on a fresh run — there is no store and no run to have learned anything.</summary>
    [Fact]
    public async Task AFreshRunGetsNoBatteryCaveat()
    {
        var legs = await RunAsync(CleanPlan());
        Assert.DoesNotContain("batteries.maxBytes", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the verb pins

    /// <summary>Adding a top-level verb is three edits and two of them are enforced elsewhere
    /// (<see cref="K7_2DocsVerbCoverageTests"/>, <c>B11_2Tests</c>). This is the third proof the
    /// contract asks for by name: the generated completion scripts offer it.</summary>
    [Fact]
    public void CompletionOffersThePreflightVerb()
    {
        Assert.Contains("preflight", CompletionCommand.GeneratePowerShell(), StringComparison.Ordinal);
        Assert.Contains("preflight", CompletionCommand.GenerateBash(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the verb's own exit code

    /// <summary>The verdict and the exit code through the REAL freshly-built CLI in its own process —
    /// Program.cs routing, plan load, six legs, one verdict line. Its own state home and
    /// <c>CONDUCTOR_PLAN</c> cleared: the drill registers a machine-catalogue row wherever the state
    /// home points, and a test may not write the operator's.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThePreflightVerbExitsZeroAndSaysReadyOnACleanPlan()
    {
        var plan = CleanPlan();
        await WritePlanFileAsync(plan);

        var r = SpawnPreflight(plan);

        Assert.True(r.ExitCode == 0, $"expected READY/0, got {r.ExitCode}:\n{r.Output}\n{r.StdErr}");
        Assert.Contains("READY", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThePreflightVerbExitsOneAndNamesTheLegOnASeededEscalation()
    {
        var plan = CleanPlan();
        WriteTracker(plan, handoff: plan.Conventions.HumanToken + " decide whether to keep the old ingest path");
        await WritePlanFileAsync(plan);

        var r = SpawnPreflight(plan);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("NOT READY", r.Output, StringComparison.Ordinal);
        Assert.Contains("escalation", r.Output, StringComparison.Ordinal);
    }

    /// <summary>And <c>ExecuteAsync</c> in-process for the two paths a spawned drill cannot pin as
    /// cheaply: a plan that does not load is a finding and exit 1, not a stack trace; a seeded leg
    /// failure is exit 1 through the verb, not just through <c>RunLegsAsync</c>.</summary>
    [Fact]
    public async Task ExecuteAsyncReturnsOneWhenThePlanDoesNotLoad()
    {
        var settings = new PreflightSettings
        {
            Plan = Path.Combine(_dir, "does-not-exist.plan.json"),
            NoAuthCheck = true,
            NoUpdateCheck = true,
        };

        var exit = await new PreflightCommand().ExecuteAsync(TestContext(), settings);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ExecuteAsyncReturnsOneOnASeededEscalation()
    {
        var plan = CleanPlan();
        WriteTracker(plan, handoff: plan.Conventions.HumanToken + " decide whether to keep the old ingest path");
        await WritePlanFileAsync(plan);
        var settings = new PreflightSettings { Plan = plan.PlanFilePath, NoAuthCheck = true, NoUpdateCheck = true };

        var exit = await new PreflightCommand().ExecuteAsync(TestContext(), settings);

        Assert.Equal(1, exit);
    }

    // ------------------------------------------------------------------ fixtures

    private Task<List<PreflightCommand.Leg>> RunAsync(PlanConfig plan)
        => PreflightCommand.RunLegsAsync(plan, authCheck: false, updateCheck: false, image: FreshImage(_dir));

    /// <summary>Round 2's reproduction (2): perPhase, every S1 row DONE, S1 not confirmed, nothing
    /// pending — the loop schedules S1's audit / phase gate, it does not compose a session.</summary>
    private PlanConfig PerPhaseDoneUnconfirmedFixture()
    {
        var plan = CleanPlan(p =>
        {
            p.GatePolicy = "perPhase";
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the next one", Sessions = 1 });
        });
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"), ("S2.1", "TODO"));
        SaveState(plan, s => s.SessionCounter = 5);
        return plan;
    }

    /// <summary>Round 2's reproduction (1): a queued <c>pendingPhaseGate</c> for S1 — the loop's very
    /// first branch, before completion, before stage selection, before any compose.</summary>
    private PlanConfig QueuedPhaseGateFixture()
    {
        var plan = CleanPlan(p => p.GatePolicy = "perPhase");
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"));
        SaveState(plan, s =>
        {
            s.CurrentStage = "S1";
            s.SessionCounter = 7;
            s.PendingPhaseGate = new PendingPhaseGate { StageId = "S1", StageStartHead = "abc1234" };
        });
        return plan;
    }

    /// <summary>The REAL loop over the fixture — ConductorHost, Orchestrator, dry run — returning
    /// everything it narrated. The other half of every agreement fact: the drill may only say what
    /// this would do. Run it AFTER the drill; a dry run writes state the drill must not see.</summary>
    private static async Task<List<string>> DryRunAsync(PlanConfig plan)
    {
        var sink = new RecordingSink();
        var state = RunState.LoadOrNew(Path.Combine(plan.StateDir, "state.json"), plan.Name);
        if (state.RunId.Length == 0) state.RunId = Guid.NewGuid().ToString("N");
        using var host = ConductorHost.Build(plan, state, sink,
            new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        Assert.Equal(0, code);
        return [.. sink.Lines];
    }

    /// <summary>The fixture plan as a loadable FILE — the in-memory object serialised where
    /// <see cref="CleanPlan"/> left its placeholder — for the drills that go through plan load.</summary>
    private static async Task WritePlanFileAsync(PlanConfig plan)
        => await File.WriteAllTextAsync(plan.PlanFilePath,
            System.Text.Json.JsonSerializer.Serialize(plan, PlanConfig.JsonOpts), Utf8);

    /// <summary>The freshly-built CLI, in its own process, against its own scratch state home.</summary>
    private ProcResult SpawnPreflight(PlanConfig plan)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "conductor.exe");
        Assert.True(File.Exists(exe), $"the freshly-built CLI must sit beside the test assembly: {exe}");
        var home = Path.Combine(_dir, "spawn-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        return Conductor.Core.ProcessRunner.Run(exe,
            ["preflight", "-p", plan.PlanFilePath, "--no-auth-check", "--no-update-check"],
            plan.Repo, TimeSpan.FromMinutes(2), CancellationToken.None,
            env: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CONDUCTOR_STATE_HOME"] = home,
                ["CONDUCTOR_PLAN"] = "",
            });
    }

    private static CommandContext TestContext() => new([], new NoRemaining(), "preflight", null);

    private sealed class NoRemaining : IRemainingArguments
    {
        public IReadOnlyList<string> Raw { get; } = [];
        public ILookup<string, string?> Parsed { get; } =
            Array.Empty<string>().ToLookup(x => x, x => (string?)null, StringComparer.Ordinal);
    }

    /// <summary>A loopback release feed serving one GitHub-shaped document — the stand-in
    /// <c>CONDUCTOR_UPDATE_FEED</c> exists for (see <see cref="ReleaseClient"/>'s own doc).</summary>
    private sealed class FakeReleaseFeed : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();

        public string Url { get; }

        public FakeReleaseFeed(string body)
        {
            var port = FreePort();
            var root = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Url = root + "/latest";
            _listener.Prefixes.Add(root + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    System.Net.HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                    catch (Exception) { return; }   // listener stopped — the exit condition
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
            });
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }

    private static string[] Failing(IEnumerable<PreflightCommand.Leg> legs)
        => legs.Where(l => l.State == "fail").Select(l => l.Name).ToArray();

    private static string Headline(IEnumerable<PreflightCommand.Leg> legs, string name)
        => legs.Single(l => l.Name == name).Headline;

    private static string Detail(IEnumerable<PreflightCommand.Leg> legs, string name)
        => string.Join(" | ", legs.Single(l => l.Name == name).Detail);

    private static SemVer Version(string text)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        return v!;
    }

    /// <summary>A file set, as a comparable string: names, sizes and write times. Anything created,
    /// removed, renamed or rewritten changes it.</summary>
    private static string Snapshot(string dir)
        => string.Join("\n", Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p):O}"
                : $"{p}|dir"));

    private PlanConfig CleanPlan(Action<PlanConfig>? tweak = null)
    {
        var repo = Path.Combine(_dir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        Git.Exec(repo, "init");

        var plan = new PlanConfig
        {
            Name = "ks34-fixture",
            Repo = repo,
            Tracker = "TRACKER.md",
            PlanFilePath = Path.Combine(repo, "fixture.plan.json"),
            Agent = new AgentConfig { Command = "git", Args = ["-p", "{prompt}"] },
        };
        // Offline by construction: the DNS/API probes are doctor's, not preflight's, and a drill's
        // verdict must not depend on whether this machine can reach github.com right now.
        plan.Limits.DnsHealthCheck = new DnsHealthCheckConfig { Enabled = false };
        plan.Stages.Add(new StageConfig { Id = "S1", Title = "the only stage", Sessions = 1 });
        tweak?.Invoke(plan);

        WriteTracker(plan, handoff: "nothing pending.");
        File.WriteAllText(plan.PlanFilePath, "{}", Utf8);
        return plan;
    }

    /// <summary>A tracker with a handoff block and one row per named checkpoint. The rows default to a
    /// single open S1.1, which is what every leg except the stage-selection ones needs.</summary>
    private static void WriteTracker(PlanConfig plan, string handoff, params (string Id, string Status)[] rows)
    {
        if (rows.Length == 0) rows = [("S1.1", "TODO")];
        var table = new StringBuilder()
            .Append("# fixture\n\n").Append(plan.Conventions.HandoffMarker).Append("\n\n")
            .Append(handoff).Append("\n\n## Checkpoints\n\n")
            .Append("| # | Checkpoint | Status | Commit | Evidence |\n")
            .Append("|---|---|---|---|---|\n");
        foreach (var (id, status) in rows)
            table.Append("| ").Append(id).Append(" | the ").Append(id).Append(" row | ")
                 .Append(status).Append(" | - | - |\n");
        File.WriteAllText(plan.TrackerPath, table.ToString(), Utf8);
    }

    /// <summary>Seeds the run's saved state — written by the TEST, never by the drill, which is the
    /// point of <see cref="AFullDrillCreatesNothingUnderTheStateDir"/>.</summary>
    private static RunState SaveState(PlanConfig plan, Action<RunState> seed)
    {
        var state = new RunState { PlanName = plan.Name };
        seed(state);
        state.Save(Path.Combine(plan.StateDir, "state.json"));
        return state;
    }

    /// <summary>The tracker as the engine reads it — the same provider the compose leg uses.</summary>
    private static TrackerSnapshot Track(PlanConfig plan)
        => ProgressProviderFactory.Create(plan).Read(plan, CancellationToken.None);

    /// <summary>A directory shaped like the engine's own repository — the solution at the root and
    /// the engine project under <c>src/</c> — holding one source file written when asked.</summary>
    private string SeedEngineTree(DateTime sourceWrittenUtc)
    {
        var tree = Path.Combine(_dir, "engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tree, "src", "Conductor"));
        File.WriteAllText(Path.Combine(tree, PreflightCommand.EngineSolutionFile), "<Solution />", Utf8);
        var project = Path.Combine(tree, "src", "Conductor", "Conductor.csproj");
        File.WriteAllText(project, "<Project />", Utf8);
        // Everything except the one file is old, so the finding can only name the file that changed.
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddDays(-7));
        var source = Path.Combine(tree, "src", "Conductor", "Newer.cs");
        File.WriteAllText(source, "// the fix nobody rebuilt\n", Utf8);
        File.SetLastWriteTimeUtc(source, sourceWrittenUtc);
        return tree;
    }

    /// <summary>An engine image written an hour ago — older than a source written now.</summary>
    private static PreflightCommand.EngineImage StaleImage(string near)
        => new(Path.Combine(near, "conductor.exe"),
            new DateTimeOffset(DateTime.UtcNow.AddHours(-1), TimeSpan.Zero), null, "abc1234", Dirty: false);

    private static PreflightCommand.EngineImage FreshImage(string near)
        => new(Path.Combine(near, "conductor.exe"),
            new DateTimeOffset(DateTime.UtcNow.AddMinutes(1), TimeSpan.Zero), null, "abc1234", Dirty: false);
}
