using System.Text;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Update;
using Conductor.Models;

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

    private static void WriteTracker(PlanConfig plan, string handoff)
        => File.WriteAllText(plan.TrackerPath,
            "# fixture\n\n" + plan.Conventions.HandoffMarker + "\n\n" + handoff + "\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n" +
            "|---|---|---|---|---|\n" +
            "| S1.1 | the only row | TODO | - | - |\n", Utf8);

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
