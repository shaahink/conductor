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
            "`conductor run` schedules the full-battery phase gate — the plan declares no gates, so that " +
            "battery is empty and confirms the stage",
            Headline(legs, "compose"));
        // Round 8: and never the definite negative again. This fixture declares no gates, so the
        // battery cannot go red — but the sentence no longer claims the launch composes nothing,
        // because after the confirmation this same run carries on to S2's session.
        Assert.DoesNotContain("no session composes", Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("carries straight on after a confirmation", Detail(legs, "compose"), StringComparison.Ordinal);
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
            "DRY RUN: stage S1 checkpoints all DONE — would schedule the full-battery phase gate",
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
            "the next `conductor run` runs the queued full-battery phase gate for stage 'S1' — the plan declares " +
            "no gates, so that battery is empty and confirms the stage",
            Headline(legs, "compose"));
        Assert.DoesNotContain("no session composes", Headline(legs, "compose"), StringComparison.Ordinal);
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

    /// <summary>Round 7's blocking finding (1), as the fact this suite was structurally blind to.
    /// Under <c>perPhaseGates</c> with a non-parallel audit enabled, a done-but-unconfirmed stage
    /// does not merely "schedule something": the loop queues the auto-fix AUDIT and re-decides inside
    /// the SAME run — no subprocess in between — and composes an <c>Audit</c> session. The drill said
    /// "no session composes" and answered READY while the launch spawned <c>Audit S1</c> with a
    /// 7114-char prompt. Every prior fact for this branch drove only the dry run, which RETURNS at
    /// the branch (<c>RunLoop</c>) and so can never see the re-decision — this one drives the real
    /// store-backed dispatch and compares the drill's whole headline against the kind the dispatch
    /// recorded and the prompt file it wrote.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AScheduledAutoFixAuditComposesTheAuditSessionTheLaunchSpawns()
    {
        var plan = ScheduledAutoFixAuditFixture();

        var legs = await RunAsync(plan);

        var (lines, state, prompt) = await LiveSessionAsync(plan);
        Assert.Contains(lines, l => l.Contains("scheduling auto-fix audit", StringComparison.Ordinal));
        var rec = state.History[^1];
        Assert.Equal(SessionKind.Audit, rec.Kind);
        Assert.Equal("S1", rec.Stage);
        Assert.Equal(
            $"next session #2 is Audit on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
        Assert.Contains("schedules the auto-fix audit", Detail(legs, "compose"), StringComparison.Ordinal);
        Assert.Empty(Failing(legs));
    }

    /// <summary>The same branch at the decision itself: the scheduling is a pure function of the plan
    /// and the saved state (<see cref="GateScheduling"/>, the one copy the loop executes), so the
    /// decision carries it THROUGH to the session it produces — the step still names the scheduling
    /// the loop must perform, and the kind, stage and attempt are the audit session's.</summary>
    [Fact]
    public void TheAutoFixAuditSchedulingIsCarriedThroughToTheSessionItProduces()
    {
        var plan = ScheduledAutoFixAuditFixture();
        var state = new RunState { PlanName = plan.Name, RunId = "r8", CurrentStage = "S1", SessionCounter = 1 };

        var next = StageSelection.NextAction(plan, state, Track(plan));

        Assert.Equal(LaunchStep.ScheduleGateOrAudit, next.Step);
        Assert.Equal(ScheduledWork.AutoFixAudit, next.Schedules);
        Assert.Equal(SessionKind.Audit, next.Kind);
        Assert.Equal("S1", next.StageId);
        Assert.Equal(1, next.AttemptNumber);
        Assert.Null(state.PendingAudit); // the decision decided; it did not mutate the caller's state
    }

    /// <summary>Round 7's blocking finding (2): "confirms completion rather than spawning a session"
    /// was a definite negative about a launch whose very next act is a gate battery. A red REQUIRED
    /// gate makes <c>ConfirmCompletionAsync</c> queue a fix and return false, and the loop re-decides
    /// into a <c>Fix</c> session in the SAME run. The battery is subprocesses, so the drill may not
    /// pick an outcome — it must name both, and it must not promise silence.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARedCompletionBatteryComposesAFixSessionAndTheDrillSaysSo()
    {
        var plan = RedBatteryFixture("r8complete");

        var legs = await RunAsync(plan);

        Assert.Equal(
            "every stage reads done — the next `conductor run` runs the completion battery BEFORE closing " +
            "the plan; what follows depends on the gates",
            Headline(legs, "compose"));
        Assert.Contains("composes a Fix session on stage 'S1'", Detail(legs, "compose"), StringComparison.Ordinal);

        var (lines, state, _) = await LiveSessionAsync(plan);
        Assert.Contains(lines, l => l.Contains("completion NOT confirmed — gates red; queuing a fix session", StringComparison.Ordinal));
        Assert.Equal(SessionKind.Fix, state.History[^1].Kind);
    }

    /// <summary>Round 7's blocking finding (3), same class at the queued phase gate: the battery runs
    /// first, and a red required gate queues a fix that this same run composes as a <c>Fix</c>
    /// session.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ARedQueuedPhaseGateComposesAFixSessionAndTheDrillSaysSo()
    {
        var plan = RedBatteryFixture("r8phasered", perPhase: true);

        var legs = await RunAsync(plan);

        Assert.Equal(
            "the next `conductor run` runs the queued full-battery phase gate for stage 'S1' BEFORE anything " +
            "composes — what follows depends on the gates",
            Headline(legs, "compose"));
        Assert.Contains("composes a Fix session on stage 'S1'", Detail(legs, "compose"), StringComparison.Ordinal);

        var (lines, state, _) = await LiveSessionAsync(plan);
        Assert.Contains(lines, l => l.Contains("queuing fix session", StringComparison.Ordinal));
        Assert.Equal(SessionKind.Fix, state.History[^1].Kind);
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
        // and it is taken off the BARE prompt, so a measured battery is never counted twice. The
        // pid-width slack rides on top (round 6): the launch's pid can render wider than the
        // drill's, and "at most" has to mean at most.
        Assert.Contains(
            $"at most {bare.TrimEnd().Length + 2 + 2048 + 2 + PreflightCommand.PidSlack} chars",
            Detail(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("batteries.maxBytes 2048", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>Silent on a fresh run — there is no store and no run to have learned anything.</summary>
    [Fact]
    public async Task AFreshRunGetsNoBatteryCaveat()
    {
        var legs = await RunAsync(CleanPlan());
        Assert.DoesNotContain("batteries.maxBytes", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ round 4: the input, not just the decision

    /// <summary>Round 4's blocking finding, seeded: the loop schedules on the WORK GRAPH
    /// (<c>RunContext.ReadWork</c> → <see cref="WorkSnapshot"/>), and an imported plan's declared
    /// statuses are frozen at TODO for the life of the run — so a compose leg fed the declared
    /// tracker promised <c>next session #7 … composing to 7601 chars</c> for a launch on which the
    /// live <c>conductor run</c> confirmed completion and spawned nothing. The leg now reads the
    /// same run.db the run would open, read-only, through the same <see cref="WorkSnapshot"/>
    /// projection. This fixture IS the divergence: declared rows all TODO, graph rows all DONE.</summary>
    [Fact]
    public async Task AGraphThatOutranTheDeclaredTrackerIsScheduledFromTheGraph()
    {
        var plan = GraphOutranTrackerFixture();

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.Equal(
            "every stage reads done — the next `conductor run` confirms completion rather than spawning a session",
            Headline(legs, "compose"));
        Assert.DoesNotContain("composing to", Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>The same fixture through the REAL STORE-BACKED loop — <c>DryRun: false</c>, the host
    /// registers the read-write store, the loop schedules on the graph — which is the surface round 4
    /// proved every dry-run-only agreement fact was structurally blind to (a dry run's host registers
    /// no store). Deterministic without an agent because the fixture declares no gates: the loop's
    /// first turn is ConfirmCompletion → an empty battery → CompletePlan, exit 0, no session.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheGraphReadAgreesWithTheLiveStoreBackedLoop()
    {
        var plan = GraphOutranTrackerFixture();
        var legs = await RunAsync(plan);

        var (lines, state) = await LiveRunAsync(plan);

        // The live loop confirmed completion and never started a session.
        Assert.Contains(lines, l => l.Contains("running the gate battery to confirm before closing the plan", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"plan '{plan.Name}' complete — 1/1 checkpoints done", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l =>
            l.Contains("session #", StringComparison.Ordinal) && l.Contains(" start — ", StringComparison.Ordinal));
        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Equal(6, state.SessionCounter);

        // And the drill said exactly that, instead of promising a numbered session.
        Assert.Equal(
            "every stage reads done — the next `conductor run` confirms completion rather than spawning a session",
            Headline(legs, "compose"));
    }

    /// <summary>And <c>run --dry-run</c> on the same fixture: with no store registered, the dry-run
    /// loop's <c>ReadWork</c> now reads the graph at rest through the same reader, so its narration
    /// agrees with the live loop and the drill instead of announcing the phantom session.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheGraphReadAgreesWithWhatRunDryRunPrints()
    {
        var plan = GraphOutranTrackerFixture();
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);

        Assert.Contains(lines, l => l.Contains("running the gate battery to confirm before closing the plan", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("would start session #", StringComparison.Ordinal));
        Assert.DoesNotContain("composing to", Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>The read-only promise now covers the store too: a full drill over an existing
    /// run.db — opened read-only for the graph and the orphan peek — leaves the store's directory
    /// byte-for-byte alone (no migration, no WAL sidecar, no mtime change). The fixture asserts its
    /// own cleanliness first: round 5 found this fact blinded by its seeding, whose pooled
    /// connection left <c>-shm</c>/<c>-wal</c> on disk BEFORE the "before" picture — so the drill's
    /// own recreation of them (Mode=ReadOnly recreates WAL sidecars; only <c>immutable=1</c> does
    /// not) could never change the snapshot.</summary>
    [Fact]
    public async Task ADrillOverAnExistingStoreLeavesTheStoreUntouched()
    {
        var plan = GraphOutranTrackerFixture();
        var storeDir = Path.GetDirectoryName(plan.RunDbPath)!;
        Assert.False(File.Exists(plan.RunDbPath + "-wal"), "the fixture's store must be cleanly closed");
        Assert.False(File.Exists(plan.RunDbPath + "-shm"), "the fixture's store must be cleanly closed");
        var before = Snapshot(storeDir);

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.Equal(before, Snapshot(storeDir));
        Assert.False(File.Exists(plan.RunDbPath + "-wal"));
        Assert.False(File.Exists(plan.RunDbPath + "-shm"));
    }

    /// <summary>The mechanism itself, at the source: opening a cleanly-closed WAL database through
    /// <see cref="SqliteRunStore.OpenReadOnly"/> answers queries and creates NOTHING beside the file —
    /// where a plain <c>Mode=ReadOnly</c> open recreates both WAL sidecars.</summary>
    [Fact]
    public void AnAtRestOpenCreatesNoWalSidecars()
    {
        var plan = GraphOutranTrackerFixture();
        Assert.False(File.Exists(plan.RunDbPath + "-wal"));

        using (var store = SqliteRunStore.OpenReadOnly(plan.RunDbPath))
        {
            Assert.Single(store.GetCheckpoints("r4graph"));
        }

        Assert.False(File.Exists(plan.RunDbPath + "-wal"));
        Assert.False(File.Exists(plan.RunDbPath + "-shm"));
    }

    /// <summary>The reader itself, at the source: at rest it answers with the graph's statuses over
    /// the declared row set — and degrades to the declared snapshot when there is no run yet,
    /// exactly as the first launch's sync over an empty graph would land.</summary>
    [Fact]
    public void WorkSnapshotAtRestReadsTheGraphAndFallsBackToDeclared()
    {
        var plan = GraphOutranTrackerFixture();

        var snap = WorkSnapshot.ReadAtRest(plan, "r4graph", () => Track(plan));
        Assert.True(snap.Checkpoints.Single(c => c.Id == "S1.1").IsDone);

        // A run id the graph has never seen → the declared rows, still TODO.
        Assert.False(WorkSnapshot.ReadAtRest(plan, "someone-else", () => Track(plan))
            .Checkpoints.Single(c => c.Id == "S1.1").IsDone);

        // No store on disk at all → declared, and no run.db is created by asking.
        var fresh = CleanPlan();
        Assert.False(WorkSnapshot.ReadAtRest(fresh, "r-none", () => Track(fresh))
            .Checkpoints.Single(c => c.Id == "S1.1").IsDone);
        Assert.False(File.Exists(fresh.RunDbPath));
    }

    // ------------------------------------------------------------------ round 5: the loop mutates its input before it reads it

    /// <summary>Round 5's blocking finding, at the source. <c>RunLoop.RunAsync</c> runs
    /// <c>SyncWorkGraphFromDeclared</c> BEFORE its first <c>ReadWork()</c>, so the loop schedules on
    /// the graph AFTER the declaration has been synced into it — neither the graph at rest (blind to
    /// every row declared since the last session, still seeing every retired one) nor the declared
    /// snapshot (round 4's bug). The at-rest reader must reproduce the sync's outcome: declared rows
    /// carrying the graph's status where the graph knows the id, the declared status where it does
    /// not, retired rows out of view.</summary>
    [Fact]
    public void TheAtRestReadModelsTheStartupSyncTheLoopRunsBeforeItsFirstRead()
    {
        var plan = DeclarationMovedBothWaysFixture();

        var snap = WorkSnapshot.ReadAtRest(plan, "r5moved", () => Track(plan));

        // A declared row the graph already finished: the graph's status wins over the frozen TODO.
        Assert.True(snap.Checkpoints.Single(c => c.Id == "S1.1").IsDone);
        // A row declared since the last session: schedulable, carrying its declared status.
        Assert.True(snap.Checkpoints.Single(c => c.Id == "S1.3").IsDone);
        // A row the declaration deleted while the stage stayed declared: the sync retires it.
        Assert.DoesNotContain(snap.Checkpoints, c => c.Id == "S1.2");
    }

    /// <summary>And the sync's other add: a plan stage with no declared and no live graph work gets
    /// its <c>{stage}.1</c> scaffold at run start, so it is schedulable — a drill (or dry run) whose
    /// read lacks the scaffold calls a launch complete that the loop will spend sessions on.</summary>
    [Fact]
    public void AStageDeclaredWithNoWorkReadsAsTheScaffoldTheSyncWillSeed()
    {
        var plan = CleanPlan(p =>
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the stage added last night", Sessions = 1 }));

        var snap = WorkSnapshot.ReadAtRest(plan, "", () => Track(plan));

        var scaffold = snap.Checkpoints.Single(c => c.StageId == "S2");
        Assert.Equal("S2.1", scaffold.Id);
        Assert.False(scaffold.IsDone);
    }

    /// <summary>Round 5's required pin, verbatim: a fixture whose DECLARED row set differs from its
    /// graph's row set in BOTH directions — one row added to the declaration since the last session,
    /// one deleted from it — driven against the REAL STORE-BACKED loop, not the dry run. The launch
    /// syncs both moves and confirms completion (2/2: the added S1.3 counted, the retired S1.2 not);
    /// a drill reading the graph at rest would have promised a session on S1.2 instead.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ADeclarationThatMovedBothWaysAgreesWithTheLiveStoreBackedLoop()
    {
        var plan = DeclarationMovedBothWaysFixture();
        var legs = await RunAsync(plan);

        var (lines, state) = await LiveRunAsync(plan);

        // The live loop synced the declaration in, then confirmed completion — no session.
        Assert.Contains(lines, l => l.Contains("work-graph sync: 1 added · 0 titles · 0 scaffolded · 1 archived · 0 revived", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"plan '{plan.Name}' complete — 2/2 checkpoints done", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l =>
            l.Contains("session #", StringComparison.Ordinal) && l.Contains(" start — ", StringComparison.Ordinal));
        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Equal(6, state.SessionCounter);

        // And the drill said exactly that.
        Assert.Empty(Failing(legs));
        Assert.Equal(
            "every stage reads done — the next `conductor run` confirms completion rather than spawning a session",
            Headline(legs, "compose"));
    }

    /// <summary>The same class in the direction that spends money: the graph at rest reads complete,
    /// but a TODO row was appended to the declaration after the last session — the launch syncs it in
    /// and spawns session #7 on it. The round-4 at-rest read told this fixture's operator
    /// "confirms completion"; the real run then spawned an agent.</summary>
    [Fact]
    public async Task ARowDeclaredAfterTheLastSessionComposesTheSessionTheLaunchWillSpawn()
    {
        var plan = RowDeclaredAfterLastSessionFixture();

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.StartsWith("next session #7 is Deliver on stage 'S1', composing to ",
            Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>And <c>run --dry-run</c> — whose storeless <c>ReadWork</c> regressed to the graph at
    /// rest in round 4's fix and printed <c>plan complete</c> for this exact fixture — announces the
    /// session again, to the character of the drill's headline.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheDeclaredRowAgreementHoldsAgainstRunDryRunToTheCharacter()
    {
        var plan = RowDeclaredAfterLastSessionFixture();
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);
        var announce = lines.FindIndex(l => l.Contains("DRY RUN: would start session #", StringComparison.Ordinal));
        Assert.True(announce >= 0, "the dry run never announced a session:\n" + string.Join("\n", lines));
        Assert.Contains("would start session #7 (Deliver, stage S1)", lines[announce], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("complete —", StringComparison.Ordinal));

        var prompt = lines[announce + 1];
        Assert.Equal(
            $"next session #7 is Deliver on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    // ------------------------------------------------------------------ round 4: the crash-recovered resume

    /// <summary>Round 4's first minor: <c>RunLoop</c> calls <c>RecoverFromCrash</c> BEFORE the first
    /// decision, so a persisted <c>Running</c> with an unfinished session decides with a queued
    /// resume — the leg read the raw saved state and named a Deliver. The recovery's state-only
    /// half is <see cref="CrashRecovery.Apply"/> now, applied by both.</summary>
    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.VerifyingGates)]
    [InlineData(RunStatus.Backoff)]
    public void CrashRecoveryQueuesTheResumeTheLoopWould(RunStatus crashed)
    {
        var state = new RunState { PlanName = "p", RunId = "r", CurrentStage = "S1", SessionCounter = 1, Status = crashed };
        state.History.Add(new SessionRecord
        {
            Number = 1, Stage = "S1", ClaudeSessionId = "s-1", StartedUtc = DateTime.UtcNow, ResumeCount = 0,
        });

        var outcome = CrashRecovery.Apply(state);

        Assert.Equal(RunStatus.Idle, state.Status);
        Assert.True(outcome.LiftedCrashStatus);
        Assert.Same(state.History[^1], outcome.Interrupted);
        Assert.Equal(SessionOutcome.Interrupted, state.History[^1].Outcome);
        Assert.NotNull(state.History[^1].EndedUtc);
        Assert.NotNull(state.PendingResume);
        Assert.Equal(1, state.PendingResume!.FromSession);
        Assert.Equal("s-1", state.PendingResume.ClaudeSessionId);
        Assert.Equal(1, state.PendingResume.ResumeCount);
    }

    /// <summary>The parked trio is deliberately NOT recovered — the loop idles on it
    /// (<see cref="LaunchStep.ParkedStatus"/>) and only <c>conductor resume</c> lifts it.</summary>
    [Theory]
    [InlineData(RunStatus.Paused)]
    [InlineData(RunStatus.NeedsHuman)]
    [InlineData(RunStatus.AwaitingOwner)]
    public void CrashRecoveryLeavesTheParkedTrioStanding(RunStatus parked)
    {
        var state = new RunState { PlanName = "p", RunId = "r", Status = parked };
        state.History.Add(new SessionRecord { Number = 1, Stage = "S1", StartedUtc = DateTime.UtcNow });

        var outcome = CrashRecovery.Apply(state);

        Assert.Equal(parked, state.Status);
        Assert.False(outcome.LiftedCrashStatus);
        Assert.Null(state.PendingResume);
    }

    /// <summary>The leg over a hard-killed run — precisely the state an operator preflights before
    /// relaunching: the next session is the RESUME the loop will queue at startup, not a Deliver.</summary>
    [Fact]
    public async Task ACrashedRunComposesTheResumeTheLoopWillQueue()
    {
        var plan = CrashedMidSessionFixture();

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.StartsWith("next session #2 is Resume on stage 'S1', composing to ",
            Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("session #1 was killed mid-flight", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>To the character, against the real loop's own narration: the dry run recovers the
    /// crash, announces the RESUME, and prints the prompt whose exact length the drill reported.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheCrashRecoveredResumeAgreesWithRunDryRunToTheCharacter()
    {
        var plan = CrashedMidSessionFixture();
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);

        Assert.Contains(lines, l => l.Contains("recovered: session #1 was interrupted — will resume its agent session", StringComparison.Ordinal));
        var announce = lines.FindIndex(l => l.Contains("DRY RUN: would start session #", StringComparison.Ordinal));
        Assert.True(announce >= 0, "the dry run never announced a session:\n" + string.Join("\n", lines));
        Assert.Contains("would start session #2 (Resume, stage S1)", lines[announce], StringComparison.Ordinal);

        var prompt = lines[announce + 1];
        Assert.Equal(
            $"next session #2 is Resume on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    // ------------------------------------------------------------------ round 5: the queued verify is a kind, not a Deliver

    /// <summary>Found live while reproducing round 5: a run stopped right after a delivery (`--once`,
    /// a crash, a cap) owes that delivery's VERIFICATION, and the loop's own kind ladder
    /// (<c>SessionRunner.PendingToKind</c>) is resume · audit · verify · fix · delivery.
    /// <see cref="StageSelection.NextAction"/> had no verify rung, so the drill and the dry run both
    /// named a Deliver for a launch that spawned <c>Verify D2</c> — wrong kind, wrong prompt, wrong
    /// measured argv.</summary>
    [Fact]
    public void AQueuedVerifyIsTheKindTheDecisionCarries()
    {
        var plan = CleanPlan();
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"));
        var state = new RunState
        {
            PlanName = plan.Name, RunId = "r5v", CurrentStage = "S1", SessionCounter = 3,
            PendingVerify = new PendingVerify { FromSession = 3, StageId = "S1" },
        };

        var next = StageSelection.NextAction(plan, state, Track(plan));

        Assert.Equal(LaunchStep.Compose, next.Step);
        Assert.Equal(SessionKind.Verify, next.Kind);
        Assert.Equal("S1", next.StageId);
    }

    /// <summary>The drill on the same state names the verify — and the whole headline, so a Deliver
    /// masquerade cannot pass on a substring.</summary>
    [Fact]
    public async Task AQueuedVerifyComposesTheVerifySessionTheLoopWillSpawn()
    {
        var plan = CleanPlan();
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"));
        SaveState(plan, s =>
        {
            s.RunId = "r5v";
            s.CurrentStage = "S1";
            s.SessionCounter = 3;
            s.PendingVerify = new PendingVerify { FromSession = 3, StageId = "S1" };
        });

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.StartsWith("next session #4 is Verify on stage 'S1', composing to ",
            Headline(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>And to the character against the loop's own narration — the dry run's prompt builder
    /// had no Verify arm either (it fell through to a Deliver prompt), so this equality pins both
    /// renderers to the loop's ladder at once.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheQueuedVerifyAgreesWithRunDryRunToTheCharacter()
    {
        var plan = CleanPlan();
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"));
        SaveState(plan, s =>
        {
            s.RunId = "r5v";
            s.CurrentStage = "S1";
            s.SessionCounter = 3;
            s.PendingVerify = new PendingVerify { FromSession = 3, StageId = "S1" };
        });
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);
        var announce = lines.FindIndex(l => l.Contains("DRY RUN: would start session #", StringComparison.Ordinal));
        Assert.True(announce >= 0, "the dry run never announced a session:\n" + string.Join("\n", lines));
        Assert.Contains("would start session #4 (Verify, stage S1)", lines[announce], StringComparison.Ordinal);

        var prompt = lines[announce + 1];
        Assert.Equal(
            $"next session #4 is Verify on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    // ------------------------------------------------------------------ round 5: the orphan only run.db remembers

    /// <summary>Round 5's second recovery half, at the source: when <c>state.json</c> remembers no
    /// crash, the loop asks the event log — an unmatched <c>SessionStarted</c> queues a resume off
    /// the state's own history record, through the same transitions the drill applies to its peeked
    /// copy (<see cref="CrashRecovery.ApplyOrphan"/>).</summary>
    [Fact]
    public void ApplyOrphanQueuesTheResumeTheLoopWould()
    {
        var plan = CleanPlan();
        SeedInterruptedSession(plan, "r5o", number: 1, agentSessionId: "sess-o1");
        var state = new RunState { PlanName = plan.Name, RunId = "r5o", CurrentStage = "S1", SessionCounter = 1 };
        state.History.Add(new SessionRecord
        {
            Number = 1, Stage = "S1", Kind = SessionKind.Deliver, Attempt = 1,
            StartedUtc = DateTime.UtcNow.AddMinutes(-10), ClaudeSessionId = "sess-o1",
        });

        using var store = SqliteRunStore.OpenReadOnly(plan.RunDbPath);
        var outcome = CrashRecovery.ApplyOrphan(state, store);

        Assert.Same(state.History[0], outcome.Resumed);
        Assert.Null(outcome.ParkedOrphanNumber);
        Assert.Equal(SessionOutcome.Interrupted, state.History[0].Outcome);
        Assert.NotNull(state.PendingResume);
        Assert.Equal("sess-o1", state.PendingResume!.ClaudeSessionId);
        Assert.Equal(RunStatus.Idle, state.Status);
    }

    /// <summary>The record the state never had is rebuilt from the log's own row — and an orphan
    /// with no agent session id cannot be resumed, so the run parks, exactly as the loop parks.</summary>
    [Fact]
    public void ApplyOrphanRebuildsFromTheLogAndParksTheUnresumable()
    {
        var plan = CleanPlan();
        SeedInterruptedSession(plan, "r5o2", number: 3, agentSessionId: "sess-o3");
        var rebuilt = new RunState { PlanName = plan.Name, RunId = "r5o2", CurrentStage = "S1", SessionCounter = 3 };
        using (var store = SqliteRunStore.OpenReadOnly(plan.RunDbPath))
        {
            var outcome = CrashRecovery.ApplyOrphan(rebuilt, store);
            Assert.NotNull(outcome.Resumed);
            Assert.Equal(3, outcome.Resumed!.Number);
            Assert.Equal("sess-o3", rebuilt.PendingResume!.ClaudeSessionId);
        }

        var parked = CleanPlan();
        SeedInterruptedSession(parked, "r5o3", number: 2, agentSessionId: null);
        var state = new RunState { PlanName = parked.Name, RunId = "r5o3", CurrentStage = "S1", SessionCounter = 2 };
        using (var store = SqliteRunStore.OpenReadOnly(parked.RunDbPath))
        {
            var outcome = CrashRecovery.ApplyOrphan(state, store);
            Assert.Null(outcome.Resumed);
            Assert.Equal(2, outcome.ParkedOrphanNumber);
            Assert.Equal(RunStatus.NeedsHuman, state.Status);
            Assert.Null(state.PendingResume);
        }
    }

    /// <summary>The drill over a run whose crash only the event log remembers — state.json reads a
    /// clean Idle, run.db holds the unmatched <c>SessionStarted</c>. The next session is the RESUME
    /// the loop's store-backed recovery queues, not the Deliver the saved state alone suggests.</summary>
    [Fact]
    public async Task AnOrphanOnlyRunDbRemembersComposesTheResumeTheLoopWillQueue()
    {
        var plan = CleanPlan();
        plan.Limits.AuthPreflight = false;
        SeedInterruptedSession(plan, "r5orphan", number: 1, agentSessionId: "sess-o1");
        SaveState(plan, s =>
        {
            s.RunId = "r5orphan";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.History.Add(new SessionRecord
            {
                Number = 1, Stage = "S1", Kind = SessionKind.Deliver, Attempt = 1,
                StartedUtc = DateTime.UtcNow.AddMinutes(-30), ClaudeSessionId = "sess-o1",
            });
        });

        var legs = await RunAsync(plan);

        Assert.Empty(Failing(legs));
        Assert.StartsWith("next session #2 is Resume on stage 'S1', composing to ",
            Headline(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("run.db's event log shows session #1 interrupted", Detail(legs, "compose"),
            StringComparison.Ordinal);
    }

    /// <summary>And the unresumable orphan is a launch failure the drill must name: the loop parks
    /// at NeedsHuman before spawning anything, and only this surface can see it coming.</summary>
    [Fact]
    public async Task AnUnresumableOrphanInRunDbFailsTheComposeLeg()
    {
        var plan = CleanPlan();
        plan.Limits.AuthPreflight = false;
        SeedInterruptedSession(plan, "r5parked", number: 4, agentSessionId: null);
        SaveState(plan, s => { s.RunId = "r5parked"; s.CurrentStage = "S1"; s.SessionCounter = 4; });

        var legs = await RunAsync(plan);

        Assert.Equal(["compose"], Failing(legs));
        Assert.Equal(
            "run.db's event log holds an orphaned session #4 with no agent session id — the next " +
            "`conductor run` parks at NeedsHuman before spawning anything",
            Headline(legs, "compose"));
        Assert.Contains("`conductor resume`", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ round 4: the attempt number on stage entry

    /// <summary>Round 4's second minor, at the source: the loop resets <c>AttemptsThisStage</c> when
    /// it ENTERS a stage, before it composes — so the decision carries attempt 1 on a stage change,
    /// whatever the counter said about the stage being left, and the saved counter + 1 only while
    /// standing still. Every renderer (SessionRunner, the dry run, the drill) reads this field.</summary>
    [Fact]
    public void TheDecisionCarriesTheAttemptNumberTheLoopRenders()
    {
        var plan = CleanPlan(p =>
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the next one", Sessions = 1 }));
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"), ("S2.1", "TODO"));

        var moved = new RunState { PlanName = plan.Name, RunId = "r1", CurrentStage = "S1", SessionCounter = 12, AttemptsThisStage = 10 };
        var entering = StageSelection.NextAction(plan, moved, Track(plan));
        Assert.Equal(LaunchStep.Compose, entering.Step);
        Assert.Equal("S2", entering.StageId);
        Assert.Equal(1, entering.AttemptNumber);

        var standing = new RunState { PlanName = plan.Name, RunId = "r1", CurrentStage = "S2", SessionCounter = 12, AttemptsThisStage = 1 };
        Assert.Equal(2, StageSelection.NextAction(plan, standing, Track(plan)).AttemptNumber);
    }

    /// <summary>And to the character against the loop: a run that burned ten attempts on a finished
    /// stage composes <c>attempt 1/2</c> on the next stage — the old leg rendered <c>attempt 11/2</c>
    /// off the un-entered counter, one character longer, so the whole-headline equality here goes red
    /// on exactly that regression.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheAttemptNumberOnAStageEntryAgreesWithRunDryRunToTheCharacter()
    {
        var plan = CleanPlan(p =>
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the next one", Sessions = 1 }));
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"), ("S2.1", "TODO"));
        SaveState(plan, s =>
        {
            s.RunId = "r4moved";
            s.CurrentStage = "S1";
            s.SessionCounter = 12;
            s.AttemptsThisStage = 10;
        });
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);
        var announce = lines.FindIndex(l => l.Contains("DRY RUN: would start session #", StringComparison.Ordinal));
        Assert.True(announce >= 0, "the dry run never announced a session:\n" + string.Join("\n", lines));
        Assert.Contains("would start session #13 (Deliver, stage S2)", lines[announce], StringComparison.Ordinal);

        var prompt = lines[announce + 1];
        Assert.Contains("attempt 1/2", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt 11/", prompt, StringComparison.Ordinal);
        Assert.Equal(
            $"next session #13 is Deliver on stage 'S2', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    // ------------------------------------------------------------------ round 6: the far side of the decision

    /// <summary>Round 6's blocking finding (1), as the agreement fact the suite was missing: a
    /// persisted <c>parallelAuditOutcome</c> with HIGH findings makes the launch's first turn queue a
    /// fix (<c>RunLoop</c> consumes the outcome BEFORE any session composes), so the next composed
    /// session is a Fix — and no dry-run fact can see it, because the dry run returns before that
    /// branch. This one drives the REAL store-backed loop through an ACTUAL dispatch and compares the
    /// drill's whole headline against the kind the dispatch recorded and the prompt file it wrote.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AHighParallelAuditOutcomeAgreesWithTheLiveLoopThroughARealDispatch()
    {
        var plan = CleanPlan(p => p.Limits.AuthPreflight = false);
        SaveState(plan, s =>
        {
            s.RunId = "r6pahigh";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.ParallelAuditOutcome = new ParallelAuditOutcome
            {
                StageId = "S1",
                MaxSeverity = AuditFindingSeverity.High,
                Findings = "HIGH: the seeded parallel audit found a high severity issue",
                Completed = true,
            };
        });
        var legs = await RunAsync(plan);

        var (lines, state, prompt) = await LiveSessionAsync(plan);

        Assert.Contains(lines, l => l.Contains("queuing fix session", StringComparison.Ordinal));
        var rec = state.History[^1];
        Assert.Equal(SessionKind.Fix, rec.Kind);
        Assert.Equal("S1", rec.Stage);
        Assert.Equal(
            $"next session #2 is {rec.Kind} on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    /// <summary>The same rung at the decision itself: a completed HIGH outcome takes the fix's rung
    /// of the ladder (resume · audit · verify · fix · workflow), the materialization is the
    /// decision's flag rather than a loop-private re-decision, and a fix already queued suppresses
    /// it exactly as the loop's own guard does.</summary>
    [Fact]
    public void ACompletedHighAuditOutcomeTakesTheFixRungOfTheLadder()
    {
        var plan = CleanPlan();
        var high = new ParallelAuditOutcome { StageId = "S1", MaxSeverity = AuditFindingSeverity.High, Findings = "HIGH: x", Completed = true };

        var state = new RunState { PlanName = plan.Name, RunId = "r6", CurrentStage = "S1", SessionCounter = 1, ParallelAuditOutcome = high };
        var next = StageSelection.NextAction(plan, state, Track(plan));
        Assert.Equal(LaunchStep.Compose, next.Step);
        Assert.Equal(SessionKind.Fix, next.Kind);
        Assert.True(next.QueuesParallelAuditFix);

        // A queued verify still outranks the fix — the loop queues the fix and composes the verify.
        state.PendingVerify = new PendingVerify { FromSession = 1, StageId = "S1" };
        var verifyFirst = StageSelection.NextAction(plan, state, Track(plan));
        Assert.Equal(SessionKind.Verify, verifyFirst.Kind);
        Assert.True(verifyFirst.QueuesParallelAuditFix);

        // A fix already queued suppresses the materialization (the loop's own PendingFix == null guard).
        state.PendingVerify = null;
        state.PendingFix = new PendingFix { FromSession = 1 };
        var already = StageSelection.NextAction(plan, state, Track(plan));
        Assert.Equal(SessionKind.Fix, already.Kind);
        Assert.False(already.QueuesParallelAuditFix);
    }

    /// <summary>And the whole surface chain on the same state: the drill's headline against what
    /// <c>run --dry-run</c> prints, to the character — the dry run composes through the same
    /// composer now, so a Deliver masquerade cannot pass on either side.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheParallelAuditFixAgreesWithRunDryRunToTheCharacter()
    {
        var plan = CleanPlan(p => p.Limits.AuthPreflight = false);
        SaveState(plan, s =>
        {
            s.RunId = "r6padry";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.ParallelAuditOutcome = new ParallelAuditOutcome
            {
                StageId = "S1", MaxSeverity = AuditFindingSeverity.High,
                Findings = "HIGH: the seeded parallel audit found a high severity issue", Completed = true,
            };
        });
        var legs = await RunAsync(plan);
        Assert.Contains("queues the fix composed here", Detail(legs, "compose"), StringComparison.Ordinal);

        var lines = await DryRunAsync(plan);
        var announce = lines.FindIndex(l => l.Contains("DRY RUN: would start session #", StringComparison.Ordinal));
        Assert.True(announce >= 0, "the dry run never announced a session:\n" + string.Join("\n", lines));
        Assert.Contains("would start session #2 (Fix, stage S1)", lines[announce], StringComparison.Ordinal);

        var prompt = lines[announce + 1];
        Assert.Equal(
            $"next session #2 is Fix on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    /// <summary>Round 6's blocking finding (3): a persisted <c>workflowStepIndices</c> mid-chain —
    /// the default deliver-verify's recorded step 1 — resolves the next session to a VERIFY, through
    /// the same <c>ResolveStartKind</c> the runner consults (a recorded index is consumed without
    /// advancing). Decision, drill, and the real store-backed dispatch, all one answer.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AMidChainWorkflowIndexAgreesWithTheLiveLoopThroughARealDispatch()
    {
        var plan = CleanPlan(p => p.Limits.AuthPreflight = false);
        SaveState(plan, s =>
        {
            s.RunId = "r6wfmid";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.WorkflowStepIndices["S1"] = 1; // deliver-verify, step 1 = verify
        });
        var legs = await RunAsync(plan);

        var (lines, state, prompt) = await LiveSessionAsync(plan);

        var rec = state.History[^1];
        Assert.Equal(SessionKind.Verify, rec.Kind);
        Assert.Contains(lines, l => l.Contains("session #2 start — Verify S1", StringComparison.Ordinal));
        Assert.Equal(
            $"next session #2 is Verify on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
    }

    /// <summary>Round 6's blocking finding (2), the one with NO seeded state at all: a declared
    /// custom workflow whose step 0 is an audit makes the very FIRST launch of a plainly-authored
    /// plan an Audit session. The drill and the dry run both said Deliver — the sentence in
    /// StageSelection claiming "step 0 resolves to Deliver in every shipped workflow" was only true
    /// of the BUILT-IN workflows, and <c>plan.workflows</c> is a shipped authoring feature.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ACustomWorkflowsFirstLaunchAgreesWithTheLiveLoopThroughARealDispatch()
    {
        var plan = AuditFirstWorkflowFixture();
        var legs = await RunAsync(plan);

        var lines = await DryRunAsync(plan);
        var announce = lines.FindIndex(l => l.Contains("DRY RUN: would start session #", StringComparison.Ordinal));
        Assert.True(announce >= 0, "the dry run never announced a session:\n" + string.Join("\n", lines));
        Assert.Contains("would start session #1 (Audit, stage S1)", lines[announce], StringComparison.Ordinal);
        Assert.Equal(
            $"next session #1 is Audit on stage 'S1', composing to {lines[announce + 1].Length} chars (nothing spawned)",
            Headline(legs, "compose"));

        var (liveLines, state, prompt) = await LiveSessionAsync(plan);
        var rec = state.History[^1];
        Assert.Equal(SessionKind.Audit, rec.Kind);
        Assert.Contains(liveLines, l => l.Contains("session #1 start — Audit S1", StringComparison.Ordinal));
        Assert.Equal(lines[announce + 1].Length, prompt.Length);
    }

    /// <summary>A recorded index belongs to the stage that recorded it: the loop clears it on stage
    /// ENTRY (a new stage starts its workflow over), and the decision models the same clear on a
    /// copy — so a stale index on a stage about to be entered does not name a Verify nothing will
    /// spawn, while the same index on the STANDING stage does.</summary>
    [Fact]
    public void AStageEntryDropsTheRecordedWorkflowIndexBeforeTheKindResolves()
    {
        var plan = CleanPlan(p =>
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the next one", Sessions = 1 }));
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"), ("S2.1", "TODO"));

        var entering = new RunState { PlanName = plan.Name, RunId = "r6ix", CurrentStage = "S1", SessionCounter = 2 };
        entering.WorkflowStepIndices["S2"] = 1;
        Assert.Equal(SessionKind.Deliver, StageSelection.NextAction(plan, entering, Track(plan)).Kind);

        var standing = new RunState { PlanName = plan.Name, RunId = "r6ix", CurrentStage = "S2", SessionCounter = 2 };
        standing.WorkflowStepIndices["S2"] = 1;
        Assert.Equal(SessionKind.Verify, StageSelection.NextAction(plan, standing, Track(plan)).Kind);
    }

    /// <summary>Round 6's MAJOR: the measured length is the length that spawns — the LOW/MEDIUM
    /// parallel-audit findings section sits OUTSIDE <c>batteries.maxBytes</c> and was composed but
    /// never measured (drill 7592, launch 10094). Now the drill, the dry run and the real dispatch
    /// hand back the same count, and the battery caveat's ceiling actually bounds the spawn.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AMediumAuditOutcomesFindingsAreMeasuredAndTheLaunchSpawnsThatLength()
    {
        var plan = CleanPlan(p => p.Limits.AuthPreflight = false);
        var findings = "MEDIUM: " + string.Join(" ", Enumerable.Repeat("finding", 300));
        SaveState(plan, s =>
        {
            s.RunId = "r6pamed";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.ParallelAuditOutcome = new ParallelAuditOutcome
            {
                StageId = "S1", MaxSeverity = AuditFindingSeverity.Medium, Findings = findings, Completed = true,
            };
        });
        var legs = await RunAsync(plan);
        Assert.Contains("LOW/MEDIUM findings", Detail(legs, "compose"), StringComparison.Ordinal);

        var (lines, state, prompt) = await LiveSessionAsync(plan);

        var rec = state.History[^1];
        Assert.Equal(SessionKind.Deliver, rec.Kind);
        Assert.Contains("## Parallel audit findings for stage S1", prompt, StringComparison.Ordinal);
        Assert.Equal(
            $"next session #2 is Deliver on stage 'S1', composing to {prompt.Length} chars (nothing spawned)",
            Headline(legs, "compose"));
        Assert.Null(state.ParallelAuditOutcome); // the launch consumed what the drill said it would
    }

    /// <summary>Round 6's rider, disclosed: with a persisted <c>pendingParallelAudit</c> the
    /// launch's FIRST act spawns an audit LANE AGENT — real model spend — before the session the
    /// headline names. That spend is not a pure function of the saved state, so the drill cannot
    /// price it; what it CAN do is say that it will happen, at the drill, instead of silently
    /// prescribing a launch whose first act it never mentioned.</summary>
    [Fact]
    public async Task AQueuedParallelAuditLaneIsDisclosedAsLaunchSpend()
    {
        var plan = CleanPlan(p => p.Limits.AuthPreflight = false);
        SaveState(plan, s =>
        {
            s.RunId = "r6lane";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.PendingParallelAudit = new PendingParallelAudit { StageId = "S1", StageStartHead = "" };
        });

        var next = StageSelection.NextAction(plan, RunState.LoadOrNew(Path.Combine(plan.StateDir, "state.json"), plan.Name), Track(plan));
        Assert.True(next.SpawnsParallelAuditLane);

        var legs = await RunAsync(plan);
        Assert.Empty(Failing(legs));
        Assert.Contains("spawns that read-only audit lane agent", Detail(legs, "compose"), StringComparison.Ordinal);
        Assert.Contains("cannot model what the lane will spend", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>The argv guard fires on the length that would ACTUALLY spawn: a plan whose base
    /// prompt clears the ceiling while the appended findings section walks the composed argv over
    /// it. Doctor's own matrix check cannot see the tail (it renders templates, not sessions), so
    /// only the compose leg can fail this launch — and it must.</summary>
    [Fact]
    public async Task TheArgvGuardFiresOnTheComposedLengthThatWouldSpawn()
    {
        var plan = CleanPlan(p => p.Limits.AuthPreflight = false);
        var bare = new PromptBuilder(plan).Deliver(plan.Stages[0], 2, 1, 2).Length;
        // Base within ~1.2k of CreateProcess' 32767 ceiling; the 3k findings tail crosses it.
        plan.PromptExtra = new string('x', DoctorCommand.CreateProcessCommandLineCeiling - bare - 1200);
        SaveState(plan, s =>
        {
            s.RunId = "r6argv";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.ParallelAuditOutcome = new ParallelAuditOutcome
            {
                StageId = "S1", MaxSeverity = AuditFindingSeverity.Medium,
                Findings = "MEDIUM: " + new string('y', 2900), Completed = true,
            };
        });

        var legs = await RunAsync(plan);

        Assert.Contains("compose", Failing(legs));
        Assert.Contains("truncated or refused at spawn", Detail(legs, "compose"), StringComparison.Ordinal);
    }

    /// <summary>A declared custom workflow whose step 0 is an audit — round 6's reproduction (2),
    /// as a fixture: fresh repo, no saved state, nothing seeded.</summary>
    private PlanConfig AuditFirstWorkflowFixture()
        => CleanPlan(p =>
        {
            p.Limits.AuthPreflight = false;
            p.Workflows = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal)
            {
                ["audit-first"] = new WorkflowDefinition
                {
                    Name = "audit-first",
                    Repeat = true,
                    Steps =
                    [
                        new WorkflowStep { Id = "a", Kind = SessionKind.Audit, Deliver = false },
                        new WorkflowStep { Id = "d", Kind = SessionKind.Deliver, Deliver = true },
                    ],
                },
            };
            p.Stages[0].Workflow = "audit-first";
        });

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

    /// <summary>Round 7's reproduction (1): perPhase, audit ENABLED and not parallel, S1's only row
    /// DONE, S1 unconfirmed — so the scheduling branch queues an auto-fix audit and the same run
    /// composes that Audit session. No gates and no auth preflight, so the live dispatch is
    /// deterministic.</summary>
    private PlanConfig ScheduledAutoFixAuditFixture()
    {
        var plan = CleanPlan(p =>
        {
            p.GatePolicy = "perPhase";
            p.Limits.AuthPreflight = false;
            p.Audit = new AuditConfig { Enabled = true, EnableParallel = false };
        });
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"));
        SaveState(plan, s => { s.RunId = "r8audit"; s.CurrentStage = "S1"; s.SessionCounter = 1; });
        return plan;
    }

    /// <summary>Round 7's reproductions (2) and (3): every row DONE and ONE required gate that always
    /// fails (<c>git rev-parse --verify</c> on a branch that does not exist — exit 128), so the
    /// battery the launch runs before anything composes is red by construction. With
    /// <paramref name="perPhase"/> the red battery is the queued phase gate's; without it, the
    /// completion battery's. Both end in a Fix session in the same run.</summary>
    private PlanConfig RedBatteryFixture(string runId, bool perPhase = false)
    {
        var plan = CleanPlan(p =>
        {
            p.Limits.AuthPreflight = false;
            if (perPhase) p.GatePolicy = "perPhase";
            p.Gates.Add(new GateConfig
            {
                Name = "red-gate",
                Command = "git rev-parse --verify refs/heads/definitely-not-a-branch",
            });
        });
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"));
        SaveState(plan, s =>
        {
            s.RunId = runId;
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            if (perPhase) s.PendingPhaseGate = new PendingPhaseGate { StageId = "S1", StageStartHead = "abc1234" };
        });
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

    /// <summary>Round 4's live reproduction, seeded: the declared tracker still reads TODO while the
    /// work graph in run.db has every checkpoint DONE — the permanent condition of any imported
    /// (<c>plan-checkpoints</c>) plan, whose declared statuses are frozen for the life of the run.
    /// No gates and no auth preflight, so the store-backed live loop resolves it deterministically
    /// without ever needing an agent.</summary>
    private PlanConfig GraphOutranTrackerFixture()
    {
        var plan = CleanPlan();
        plan.Limits.AuthPreflight = false;
        SeedGraph(plan, "r4graph", ("S1.1", "S1", "the S1.1 row", "DONE"));
        SaveState(plan, s => { s.RunId = "r4graph"; s.CurrentStage = "S1"; s.SessionCounter = 6; });
        return plan;
    }

    /// <summary>Round 5's live reproduction, seeded — both directions at once. The declaration moved
    /// AFTER the graph last did: one row the graph finished is re-declared TODO (the graph's status
    /// must win), one row was declared since the last session (S1.3 — invisible to a graph-at-rest
    /// read), and one row the graph still holds open was deleted from the declaration (S1.2 — the
    /// startup sync retires it, but a graph-at-rest read still schedules it). The loop syncs all
    /// three before its first read; a drill that models anything less names a different launch.</summary>
    private PlanConfig DeclarationMovedBothWaysFixture()
    {
        var plan = CleanPlan();
        plan.Limits.AuthPreflight = false;
        SeedGraph(plan, "r5moved",
            ("S1.1", "S1", "the S1.1 row", "DONE"),
            ("S1.2", "S1", "the S1.2 row", "TODO"));
        WriteTracker(plan, "nothing pending.", ("S1.1", "TODO"), ("S1.3", "DONE"));
        SaveState(plan, s => { s.RunId = "r5moved"; s.CurrentStage = "S1"; s.SessionCounter = 6; });
        return plan;
    }

    /// <summary>Round 5's other live reproduction: the graph is entirely done, and ONE new TODO row
    /// was appended to the declaration after the last session. The graph at rest says "complete";
    /// the launch syncs the row in and spawns session #7 on it.</summary>
    private PlanConfig RowDeclaredAfterLastSessionFixture()
    {
        var plan = CleanPlan();
        plan.Limits.AuthPreflight = false;
        SeedGraph(plan, "r5added", ("S1.1", "S1", "the S1.1 row", "DONE"));
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"), ("S1.2", "TODO"));
        SaveState(plan, s => { s.RunId = "r5added"; s.CurrentStage = "S1"; s.SessionCounter = 6; });
        return plan;
    }

    /// <summary>A hard-killed engine: persisted <c>Running</c>, session #1 still open in the history.
    /// Exactly the state an operator preflights before relaunching.</summary>
    private PlanConfig CrashedMidSessionFixture()
    {
        var plan = CleanPlan();
        SaveState(plan, s =>
        {
            s.RunId = "r4crashed";
            s.CurrentStage = "S1";
            s.SessionCounter = 1;
            s.Status = RunStatus.Running;
            s.History.Add(new SessionRecord
            {
                Number = 1,
                Stage = "S1",
                Kind = SessionKind.Deliver,
                Attempt = 1,
                StartedUtc = DateTime.UtcNow.AddMinutes(-30),
                ClaudeSessionId = "sess-r4-1",
            });
        });
        return plan;
    }

    /// <summary>Writes graph rows into the run.db the engine would open — the write side the TEST
    /// owns, through the same store the live run registers. The pool is cleared afterwards so the
    /// database is CLEANLY CLOSED: a pooled connection outlives the <c>using</c> and keeps the WAL
    /// sidecars on disk, which is exactly the state that blinded
    /// <see cref="ADrillOverAnExistingStoreLeavesTheStoreUntouched"/> to the drill recreating
    /// them (round 5's read-only finding).</summary>
    private static void SeedGraph(PlanConfig plan, string runId,
        params (string Id, string StageId, string Title, string Status)[] rows)
    {
        using (var store = new SqliteRunStore(plan.RunDbPath, NullLogger<SqliteRunStore>.Instance))
        {
            store.SeedCheckpoints(runId, rows.Select(r => (r.Id, r.StageId, r.Title, r.Status, "-", "-")));
            store.FlushEvents();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>A run.db whose event log holds a <c>SessionStarted</c> with no matching finish —
    /// the crash evidence <c>state.json</c> may know nothing about, which only the store-backed half
    /// of startup recovery can see.</summary>
    private static void SeedInterruptedSession(PlanConfig plan, string runId, int number, string? agentSessionId)
    {
        using (var store = new SqliteRunStore(plan.RunDbPath, NullLogger<SqliteRunStore>.Instance))
        {
            store.SetRunId(runId);
            store.SeedCheckpoints(runId, [("S1.1", "S1", "the S1.1 row", "TODO", "-", "-")]);
            store.Emit(new Conductor.Core.Events.SessionStarted
            {
                Number = number,
                StageId = "S1",
                Kind = "Deliver",
                AgentSessionId = agentSessionId,
            });
            store.FlushEvents();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>The REAL loop, store-backed — <c>DryRun: false</c>, so <c>ConductorHost</c> registers
    /// the read-write store and the loop schedules on the graph exactly as a launch would. Only for
    /// fixtures that resolve WITHOUT a session (completion, a park): nothing here may spawn an agent.
    /// Returns the narration and the final state.</summary>
    private static async Task<(List<string> Lines, RunState State)> LiveRunAsync(PlanConfig plan)
    {
        var sink = new RecordingSink();
        var state = RunState.LoadOrNew(Path.Combine(plan.StateDir, "state.json"), plan.Name);
        if (state.RunId.Length == 0) state.RunId = Guid.NewGuid().ToString("N");
        using var host = ConductorHost.Build(plan, state, sink,
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        Assert.Equal(0, code);
        return ([.. sink.Lines], state);
    }

    /// <summary>The REAL store-backed loop driven through an ACTUAL SESSION DISPATCH — <c>Once:
    /// true</c> and an agent command that exits immediately (<c>git -p &lt;prompt&gt;</c> is not a git
    /// command), so the dispatch is real — the kind is resolved where the session begins, the prompt
    /// is written to <c>logs/session-NNN.prompt.md</c>, an argv is spawned — and nothing spends.
    /// Rounds 1–6's lesson is that completion/park fixtures stop at the decision; this helper exists
    /// so an agreement fact goes red when the leg and the DISPATCH disagree. Returns the narration,
    /// the final state (whose last history record carries the dispatched kind) and the prompt the
    /// session was actually handed.</summary>
    private static async Task<(List<string> Lines, RunState State, string Prompt)> LiveSessionAsync(PlanConfig plan)
    {
        var sink = new RecordingSink();
        var state = RunState.LoadOrNew(Path.Combine(plan.StateDir, "state.json"), plan.Name);
        if (state.RunId.Length == 0) state.RunId = Guid.NewGuid().ToString("N");
        using (var host = ConductorHost.Build(plan, state, sink,
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var logsDir = Path.Combine(plan.StateDir, "logs");
        var promptFile = Directory.Exists(logsDir)
            ? Directory.EnumerateFiles(logsDir, "session-*.prompt.md").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
            : null;
        Assert.False(promptFile is null, "the live loop dispatched no session:\n" + string.Join("\n", sink.Lines));
        return ([.. sink.Lines], state, await File.ReadAllTextAsync(promptFile!));
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
