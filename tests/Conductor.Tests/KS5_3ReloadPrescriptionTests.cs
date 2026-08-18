using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Integrations;
using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS5.3 — the plan reload says so when the ceiling it just loaded contradicts what this run's own
/// sessions measured.
///
/// <para>The reload line has named the budget since G3.2, because it is the setting most often edited
/// mid-run. It reads the number back and stops there. A ceiling under this run's session floor is not
/// a tighter budget — it is a run that can no longer land a checkpoint in one session, and the total
/// rises rather than falls. <c>doctor</c> has been able to say that since K4.2, but doctor runs before
/// a run starts, and nobody runs it while parked at 2am with the plan file open.</para>
///
/// <para>The tests that matter are the two about silence: an agreeing reload must add nothing (a
/// warning on every reload is a warning nobody reads), and a run that cannot be measured must say it
/// cannot be measured rather than prescribe from an empty set — a floor of zero is cleared by every
/// ceiling there is, which would turn "unknown" into "fine".</para>
/// </summary>
public sealed class KS5_3ReloadPrescriptionTests : IDisposable
{
    private const string RunId = "run-ks53-0001";
    private const string PlanName = "ks53";

    // The measured shape: two sessions closed a checkpoint at 6M and 8M agent tokens, one closed
    // nothing at 4M. Floor 6M, median closer 7M, largest closer 8M — the numbers every assertion
    // below is written against.
    private const long Floor = 6_000_000;
    private const long BiggerCloser = 8_000_000;
    private const long NonCloser = 4_000_000;

    private readonly string _tmp;
    private readonly List<IDisposable> _open = [];

    public KS5_3ReloadPrescriptionTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks53-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        foreach (var d in _open) { try { d.Dispose(); } catch (ObjectDisposedException) { } }
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ the disagreements

    /// <summary>The failure this checkpoint exists for: a cap typed below the floor this run has
    /// already measured. The line quotes the floor AND the prescription, because "too low" without a
    /// number is not something an operator can act on at the boundary.</summary>
    [Fact]
    public void ReloadBelowTheMeasuredFloor_LogsTheDisagreementWithTheFloorAndThePrescription()
    {
        var rig = Rig();
        SeedMeasuredRun(rig);

        WritePlan(rig.Repo, 4_000_000, 0.7);
        rig.Loop.ApplyPlanReload();

        var line = Assert.Single(Disagreements(rig));
        Assert.Contains("cap 4M / nudge 2.8M", line, StringComparison.Ordinal);
        Assert.Contains("the cap is BELOW the measured 6M session floor", line, StringComparison.Ordinal);
        Assert.Contains("set maxSessionTokens to 12M", line, StringComparison.Ordinal);   // the prescription verdict
    }

    /// <summary>The subtler half, and the one this repo shipped: the ceiling clears the floor, and the
    /// nudge fires under the median session that finishes, so the rail interrupts everything.</summary>
    [Fact]
    public void ReloadWhoseNudgeSitsUnderTheMedianCloser_LogsTheDisagreement()
    {
        var rig = Rig();
        SeedMeasuredRun(rig);

        WritePlan(rig.Repo, 12_000_000, 0.4);         // 4.8M nudge against a 7M median closer
        rig.Loop.ApplyPlanReload();

        var line = Assert.Single(Disagreements(rig));
        Assert.Contains("cap 12M / nudge 4.8M", line, StringComparison.Ordinal);
        Assert.Contains("0.69x the 7M median closing session", line, StringComparison.Ordinal);
    }

    /// <summary>A budget that agrees with the measurement is not news. The reload line itself is
    /// unchanged, and nothing is added beside it.</summary>
    [Fact]
    public void ReloadThatAgreesWithTheMeasurement_AddsNothing()
    {
        var rig = Rig();
        SeedMeasuredRun(rig);

        WritePlan(rig.Repo, 12_000_000, 0.75);        // 9M nudge, clears the 7M median and the 6M floor
        rig.Loop.ApplyPlanReload();

        Assert.Empty(Disagreements(rig));
        Assert.Empty(Unmeasurable(rig));
        Assert.Contains(Log(rig), l => l.Contains("plan reloaded at session boundary", StringComparison.Ordinal)
                                    && l.Contains("12M tokens/session", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ what cannot be measured

    /// <summary>Nothing has closed a checkpoint, so there is no floor. The honest answer is to say so;
    /// prescribing from an empty set would be a guess wearing a measurement's clothes, and reporting
    /// agreement would be worse — a floor of zero is cleared by every ceiling there is.</summary>
    [Fact]
    public void ReloadOnARunThatHasClosedNothing_SaysThereIsNoFloor_RatherThanPrescribing()
    {
        var rig = Rig();
        rig.Ctx.EnsureRunRow();                        // a run row, and not one session under it

        WritePlan(rig.Repo, 4_000_000, 0.7);
        rig.Loop.ApplyPlanReload();

        var line = Assert.Single(Unmeasurable(rig));
        Assert.Contains("no floor to measure", line, StringComparison.Ordinal);
        Assert.DoesNotContain("set maxSessionTokens", line, StringComparison.Ordinal);
        Assert.Empty(Disagreements(rig));
    }

    /// <summary>A reload must never fail, delay or park because the measurement could not be taken.
    /// With no store there is no database to read, and the reload does its whole job in silence.</summary>
    [Fact]
    public void ReloadWithNoStore_StillReloads_AndSaysNothingExtra()
    {
        var rig = Rig(withStore: false);

        WritePlan(rig.Repo, 4_000_000, 0.7);
        rig.Loop.ApplyPlanReload();

        Assert.Contains(Log(rig), l => l.Contains("plan reloaded at session boundary", StringComparison.Ordinal));
        Assert.Empty(Disagreements(rig));
        Assert.Empty(Unmeasurable(rig));
    }

    /// <summary>The unit end of the same rule: no archive is "cannot measure", which is neither a
    /// disagreement nor agreement. A database that will not open reads the same way.</summary>
    [Fact]
    public void WithNothingToRead_TheVerdictIsCannotMeasure_NotAgreement()
    {
        Assert.Null(BudgetDisagreement.MeasureRun(null, RunId));
        Assert.Null(BudgetDisagreement.MeasureForPlan(null, PlanName));
        Assert.Null(RunArchive.TryOpen(Path.Combine(_tmp, "no-such.db")));

        var verdict = BudgetDisagreement.Compare(4_000_000, 0.7, null, measurable: false);

        Assert.Equal(BudgetAgreement.CannotMeasure, verdict.Agreement);
        Assert.False(verdict.Disagrees);
        Assert.Equal("ok", verdict.DoctorState);
        Assert.Contains("no history yet", verdict.Sentence, StringComparison.Ordinal);
    }

    /// <summary>No ceiling, nothing to disagree with — and the reload keeps its "no per-session cap"
    /// line rather than gaining a second sentence about a budget that does not exist.</summary>
    [Fact]
    public void ReloadWithNoCeiling_SaysNothingAboutTheFloor()
    {
        var rig = Rig();
        SeedMeasuredRun(rig);

        WritePlan(rig.Repo, sessionTokenCap: null, ratio: 0.7);
        rig.Loop.ApplyPlanReload();

        Assert.Empty(Disagreements(rig));
        Assert.Empty(Unmeasurable(rig));
        Assert.Contains(Log(rig), l => l.Contains("no per-session cap", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ one reload, one line

    /// <summary>A parked run turns the loop about every 800ms, and <c>ApplyPlanReload</c> is called
    /// from the top of it — BEFORE the idle check. The disagreement is tied to an actual reload, not to
    /// the boundary: after the swap the stamp matches the file again, so every following turn's
    /// boundary check answers false and the line is not repeated.</summary>
    [Fact]
    public void TheDisagreementFiresOncePerReload_NotOnEveryIdleTurn()
    {
        var rig = Rig();
        SeedMeasuredRun(rig);

        WritePlan(rig.Repo, 4_000_000, 0.7);
        rig.Loop.ApplyPlanReload();

        // Five turns of a parked loop. The reload re-stamped from the file it read, so the boundary
        // check has nothing to report and ApplyPlanReload is never reached again.
        for (var turn = 0; turn < 5; turn++)
            Assert.False(rig.Loop.PlanFileChangedOnDisk(), "an idle turn must not re-trigger the reload");

        Assert.Single(Disagreements(rig));
    }

    // ------------------------------------------------------------------ one function, two surfaces

    /// <summary>Doctor and the reload say the SAME sentence about the same numbers, because they call
    /// the same function. Asserted as equality against doctor's own message rather than as two
    /// hand-written strings: edit <see cref="BudgetDisagreement.Compare"/> and both ends move
    /// together, which is the only version of this test that cannot rot.</summary>
    [Fact]
    public void DoctorAndTheReloadSpeakOneSentence()
    {
        var rig = Rig();
        SeedMeasuredRun(rig);

        var planPath = WritePlan(rig.Repo, 4_000_000, 0.7);
        rig.Loop.ApplyPlanReload();

        // Doctor, over the same database — the repo-local state pointer the rig writes is what aims it
        // there, so this resolution never reaches (or grows) the machine's catalogue.
        var check = DoctorCommand.CheckTokenBudget(PlanConfig.Load(planPath));

        Assert.Equal("warn", check.State);
        var line = Assert.Single(Disagreements(rig));
        Assert.EndsWith(check.Message, line, StringComparison.Ordinal);
    }

    /// <summary>And the agreeing arm of the same claim: doctor's "ok" wording is the verdict's
    /// sentence too, so the two surfaces cannot drift on the quiet path either.</summary>
    [Fact]
    public void DoctorsOkMessageIsTheSameVerdictSentence()
    {
        var rig = Rig();
        SeedMeasuredRun(rig);
        var planPath = WritePlan(rig.Repo, 12_000_000, 0.75);

        var check = DoctorCommand.CheckTokenBudget(PlanConfig.Load(planPath));
        var archive = RunArchive.TryOpen(rig.DbPath);
        var verdict = BudgetDisagreement.Compare(
            12_000_000, 0.75, BudgetDisagreement.MeasureForPlan(archive, PlanName), measurable: true);

        Assert.Equal(BudgetAgreement.Agrees, verdict.Agreement);
        Assert.Equal(verdict.Sentence, check.Message);
        Assert.Equal(verdict.DoctorState, check.State);
    }

    // ------------------------------------------------------------------ the rig

    private const string DisagreeMarker = "the reloaded budget disagrees with this run's own sessions:";
    private const string UnmeasurableMarker = "the reloaded budget cannot be checked yet:";

    private static IReadOnlyList<string> Log(LoopRig rig)
    {
        var path = Path.Combine(rig.Repo, StateHome.ScratchDirName, "conductor.log");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private static List<string> Disagreements(LoopRig rig) =>
        Log(rig).Where(l => l.Contains(DisagreeMarker, StringComparison.Ordinal)).ToList();

    private static List<string> Unmeasurable(LoopRig rig) =>
        Log(rig).Where(l => l.Contains(UnmeasurableMarker, StringComparison.Ordinal)).ToList();

    /// <summary>The measured shape, written through the store's own API so the read-only archive sees
    /// exactly what a live run would have left behind. Tokens go in as AGENT rows on purpose: the
    /// session ceiling governs the agent stream, and <c>ArchivedSession.CapTokens</c> counts nothing
    /// else (KS5.2), so a lane row here would move a floor it is never compared against.</summary>
    private static void SeedMeasuredRun(LoopRig rig)
    {
        rig.Ctx.EnsureRunRow();
        var started = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        Session(rig, 1, Floor, "S1.1", started);
        Session(rig, 2, BiggerCloser, "S1.2", started.AddHours(2));
        Session(rig, 3, NonCloser, null, started.AddHours(4));
    }

    private static void Session(LoopRig rig, int number, long tokens, string? closed, DateTime startedUtc)
    {
        rig.Store.RecordSession(RunId, "S1", number, "delivery", startedUtc, startedUtc.AddHours(1),
            outcome: "Completed", agentSessionId: null, resumeCount: 0, attempt: 1,
            gateSummary: null, resultSummary: null, commitCount: 1, newlyDone: closed);
        rig.Store.RecordCost(RunId, number, "agent", tokens, 0, 0, 0, 1.00m, 60_000);
    }

    private sealed record LoopRig(string Repo, string DbPath, SqliteRunStore Store, RunContext Ctx, RunLoop Loop);

    /// <summary>A run loop wired for the boundary and nothing else — KS1.1's rig shape.
    /// <para><c>SessionRunner</c> and <c>VerdictEngine</c> go in null on purpose: the reload path must
    /// not reach either of them, and passing nulls makes that an assertion rather than a claim.</para></summary>
    private LoopRig Rig(bool withStore = true)
    {
        var repo = NewRepo();
        var dbPath = Path.Combine(repo, "run.db");
        // The repo-local pointer BEFORE anything resolves state: without it StateHome.Resolve derives a
        // path under the machine's state home AND upserts a catalogue entry, so a test that only meant
        // to read would grow the operator's catalogue.
        File.WriteAllText(
            Path.Combine(repo, StateHome.ScratchDirName, StateHome.PointerFileName),
            $$"""{"runDb": {{JsonSerializer.Serialize(dbPath)}}}""", new UTF8Encoding(false));

        var planPath = WritePlan(repo, 12_000_000, 0.75);
        var plan = PlanConfig.Load(planPath);

        SqliteRunStore? store = null;
        if (withStore)
        {
            store = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
            _open.Add(store);
            store.SetRunId(RunId);
        }
        IEventSink events = (IEventSink?)store ?? NullEventSink.Instance;

        var state = new RunState { RunId = RunId, PlanName = plan.Name };
        var sink = new PlainSink();
        var lessons = new LessonsManager(plan.StateDir);
        var qa = new Conductor.Planning.DefaultQaPolicy();
        var webhooks = new WebhookNotifier(plan, NullLogger<WebhookNotifier>.Instance);
        _open.Add(webhooks);

        var ctx = new RunContext(
            plan, state, new RunOptions(DryRun: true, Once: true, MaxSessions: 0),
            sink, events, new PromptBuilder(plan, new PersonaRegistry(plan), lessons, qa),
            lessons, new CheckpointPlanner(), ProgressProviderFactory.Create(plan),
            AgentProviderFactory.Create(plan.Agent), store,
            processSupervisor: null, controlInbox: null,
            new NoOpRunNotifier(), webhooks,
            workflowResolver: null, NullLogger<KS5_3ReloadPrescriptionTests>.Instance);

        var dispatcher = new ControlDispatcher(plan, state, sink, events, log: _ => { }, save: () => { },
            deleteControlFile: () => { }, skipStage: (_, _) => { },
            approveAwaitingOwner: (_, _) => Task.CompletedTask);
        var loop = new RunLoop(ctx, sessions: null!, verdicts: null!,
            new GateOrchestrator(plan, state, events, store),
            new LaneCoordinator(plan, state, sink, events, _ => { }),
            dispatcher, saveAndReport: () => { });

        return new LoopRig(repo, dbPath, store!, ctx, loop);
    }

    private string NewRepo()
    {
        var repo = Path.Combine(_tmp, "repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(repo, StateHome.ScratchDirName));
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n" +
            "|---|---|---|---|---|\n| S1.1 | one | TODO | | |\n", new UTF8Encoding(false));
        return repo;
    }

    private static string WritePlan(string repo, long? sessionTokenCap, double ratio)
    {
        var plan = new PlanConfig
        {
            Name = PlanName,
            Repo = repo.Replace('\\', '/'),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "S1", Title = "one", Sessions = 1 }],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "echo", "{prompt}"], Provider = "opencode" },
        };
        plan.Limits.MaxSessionTokens = sessionTokenCap;
        plan.Limits.SoftBreakRatio = ratio;
        var path = Path.Combine(repo, "ks53.plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
