using System.Text;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Core.Update;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>And under <c>perPhaseGates</c> done-ness is CONFIRMED-ness: a stage whose rows all read
    /// done has not been through its gate yet, so it is still the next session — which the tracker
    /// alone would have said was finished.</summary>
    [Fact]
    public async Task UnderPerPhaseGatesAStageIsOnlyFinishedOnceItIsConfirmed()
    {
        var plan = CleanPlan(p =>
        {
            p.GatePolicy = "perPhase";
            p.Stages.Add(new StageConfig { Id = "S2", Title = "the next one", Sessions = 1 });
        });
        WriteTracker(plan, "nothing pending.", ("S1.1", "DONE"), ("S2.1", "TODO"));
        SaveState(plan, s => s.SessionCounter = 5);

        var legs = await RunAsync(plan);

        Assert.Contains("stage 'S1'", Headline(legs, "compose"), StringComparison.Ordinal);
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

    // ------------------------------------------------------------------ fixtures

    private Task<List<PreflightCommand.Leg>> RunAsync(PlanConfig plan)
        => PreflightCommand.RunLegsAsync(plan, authCheck: false, updateCheck: false, image: FreshImage(_dir));

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
