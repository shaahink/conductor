using System.Text;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS1.4 — the seven plan-semantics lints. Every one of them exists because the failure it catches
/// has already happened here and cost a run: a gate whose program was not installed, a hook that
/// exited nonzero into a log nobody read, a tracker row the provider silently dropped, a plan edited
/// while a run was executing the old one, a prompt longer than the command line that carries it, a
/// typo'd brace in a template, and the escalation token left in prose where a session reads it back.
///
/// <para>Each lint gets a trap and a clean case, driven through the internal <c>Check*</c> method the
/// way <see cref="DoctorCommandTests"/> does — no Spectre rendering, no CLI plumbing. The last test
/// is the one that keeps the bar honest: this repo's own plan, the worked example, must be green
/// under all seven.</para>
///
/// <para>The escalation token is never written literally in this file. It is assembled at runtime
/// from its parts, because the match that parks a run is a plain substring and a fixture carrying
/// the literal would park the run reading it.</para>
/// </summary>
public sealed class KS1_4DoctorPlanLintsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "conductor-ks14-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly List<IDisposable> _open = [];
    private readonly List<string> _held = [];

    public KS1_4DoctorPlanLintsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var stateDir in _held) EngineLock.Delete(stateDir);
        foreach (var d in _open) { try { d.Dispose(); } catch (ObjectDisposedException) { } }
        SqliteConnection.ClearAllPools();
        try { TestTemp.DeleteTree(_dir); } catch (Exception) { /* best effort */ }
    }

    // ------------------------------------------------------------------ 1. gate-command path probe

    [Fact]
    public void GatePathProbe_Green_WhenEveryGateResolves()
    {
        var plan = CleanPlan(p => p.Gates.Add(new GateConfig { Name = "build", Command = "git status", Tier = "fast" }));
        var check = DoctorCommand.CheckGatePaths(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void GatePathProbe_Red_AndNamesTheGateAndTheProgram()
    {
        var plan = CleanPlan(p => p.Gates.Add(new GateConfig
        {
            Name = "build",
            Command = "definitely-not-a-real-gate-xyz123 --version",
        }));
        var check = DoctorCommand.CheckGatePaths(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("'build'", check.Message, StringComparison.Ordinal);
        Assert.Contains("definitely-not-a-real-gate-xyz123", check.Message, StringComparison.Ordinal);
    }

    /// <summary>The probe RESOLVES, it never RUNS (bug #16: a gate that rebuilt the engine mid-run).
    /// The command handed to it is one that RESOLVES — <c>cmd</c> is on PATH — and would leave a mark
    /// on disk if it were executed, so the probe reports this gate ok and the marker's absence is the
    /// whole finding: the difference between "the program is there" and "the program has run" is
    /// exactly what this lint may not blur.</summary>
    [Fact]
    public void GatePathProbe_NeverExecutesTheGate()
    {
        var marker = Path.Combine(_dir, "the-gate-ran.txt");
        var plan = CleanPlan(p => p.Gates.Add(new GateConfig
        {
            Name = "destructive",
            Command = $"cmd /c echo ran > \"{marker}\"",
        }));
        var check = DoctorCommand.CheckGatePaths(plan);
        if (OperatingSystem.IsWindows()) Assert.Equal("ok", check.State);   // cmd is on PATH: it resolved
        Assert.False(File.Exists(marker));                                  // and that is all it did
    }

    [Fact]
    public void GatePathProbe_NamesTheUnknownShell()
    {
        var plan = CleanPlan(p => p.Gates.Add(new GateConfig { Name = "build", Command = "git status", Shell = "zsh" }));
        var check = DoctorCommand.CheckGatePaths(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("zsh", check.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 2. hook dry-run

    [Fact]
    public void HookDryRun_Green_WhenEveryHookResolves()
    {
        var plan = CleanPlan(p => p.Setup = new HookConfig { Command = "git status" });
        var check = DoctorCommand.CheckHooks(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void HookDryRun_Red_AndNamesTheHookAndTheProgram()
    {
        var plan = CleanPlan(p =>
        {
            p.Stages[0].PostHook = new HookConfig { Command = "definitely-not-a-real-hook-xyz123 -x" };
        });
        var check = DoctorCommand.CheckHooks(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("stage 'S1' post-hook", check.Message, StringComparison.Ordinal);
        Assert.Contains("definitely-not-a-real-hook-xyz123", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HookDryRun_NeverExecutesTheHook()
    {
        var marker = Path.Combine(_dir, "the-hook-ran.txt");
        var plan = CleanPlan(p => p.Setup = new HookConfig { Command = $"cmd /c echo ran > \"{marker}\"" });
        DoctorCommand.CheckHooks(plan);
        Assert.False(File.Exists(marker));
    }

    // ------------------------------------------------------------------ 3. checkpoint id vs tracker

    [Fact]
    public void CheckpointIds_Green_WhenEveryRowParses()
    {
        var check = DoctorCommand.CheckCheckpointIds(CleanPlan());
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void CheckpointIds_Red_WhenATrackerRowDoesNotParse()
    {
        var plan = CleanPlan();
        File.AppendAllText(plan.TrackerPath, "| S1-2 | a row the regex drops | TODO | - | - |\n", Utf8);
        var check = DoctorCommand.CheckCheckpointIds(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("S1-2", check.Message, StringComparison.Ordinal);
        Assert.Contains("stageIdPattern", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointIds_Red_WhenAnIdIsDeclaredTwice()
    {
        var plan = CleanPlan();
        File.AppendAllText(plan.TrackerPath, "| S1.1 | the same id again | TODO | - | - |\n", Utf8);
        var check = DoctorCommand.CheckCheckpointIds(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("'S1.1'", check.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 4. plan drift

    [Fact]
    public void PlanDrift_Green_WhenTheRunLoadedTheVersionOnDisk()
    {
        var plan = SeedDriftRig(loadedVersion: 3, fileVersion: 3, runStatus: "running");
        var check = DoctorCommand.CheckPlanDrift(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void PlanDrift_Red_WhenAnUnfinishedRunIsBehindTheFile()
    {
        var plan = SeedDriftRig(loadedVersion: 2, fileVersion: 5, runStatus: "running");
        HoldTheEngineLock(plan);
        Assert.True(RunLiveness.StoreLooksLive(plan.ResolveState().RunDbPath, plan.Repo));

        var check = DoctorCommand.CheckPlanDrift(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("v2", check.Message, StringComparison.Ordinal);
        Assert.Contains("v5", check.Message, StringComparison.Ordinal);
        Assert.Contains("plan reload", check.Message, StringComparison.Ordinal);
    }

    /// <summary>A finished run under an older version is history, not drift — the file has simply
    /// been edited since. Saying otherwise would make the lint red on every repo the day after a
    /// run ends, and a check that is always red is a check nobody reads.</summary>
    [Fact]
    public void PlanDrift_Quiet_WhenTheRunHasFinished()
    {
        var plan = SeedDriftRig(loadedVersion: 2, fileVersion: 5, runStatus: "completed");
        var check = DoctorCommand.CheckPlanDrift(plan);
        Assert.Equal("ok", check.State);
    }

    /// <summary>The row this lint would have believed. A run that was killed leaves <c>runs.status</c>
    /// saying <c>running</c> for ever — nobody was left alive to write the correction, which is the
    /// whole of FU-F1-06 and the reason KS1.3 put <see cref="RunLiveness"/> one commit earlier in this
    /// same lane. Nothing is scheduling from that document, so nothing is drifting from it, and a lint
    /// that failed here would be permanently red on every repo whose last run crashed, with no
    /// <c>plan reload</c> able to clear it.</summary>
    [Fact]
    public void PlanDrift_Quiet_WhenTheUnfinishedRunHasNoEngineBehindIt()
    {
        var plan = SeedDriftRig(loadedVersion: 2, fileVersion: 5, runStatus: "running");

        // No lock, no tracked pid: the store is orphaned, exactly as the verifier's rig found it.
        Assert.False(RunLiveness.StoreLooksLive(plan.ResolveState().RunDbPath, plan.Repo));

        var check = DoctorCommand.CheckPlanDrift(plan);
        Assert.Equal("ok", check.State);
        Assert.Contains(RunLiveness.Orphaned, check.Message, StringComparison.Ordinal);
    }

    /// <summary>And the lint follows the shared rule rather than a copy of it: the same store, the
    /// same stale row, answered both ways by nothing but whether an engine is holding it.</summary>
    [Fact]
    public void PlanDrift_TracksTheSharedLivenessRule_NotTheStoredStatus()
    {
        var plan = SeedDriftRig(loadedVersion: 2, fileVersion: 5, runStatus: "running");
        Assert.Equal("ok", DoctorCommand.CheckPlanDrift(plan).State);

        HoldTheEngineLock(plan);
        Assert.Equal("fail", DoctorCommand.CheckPlanDrift(plan).State);

        EngineLock.Delete(Path.Combine(plan.Repo, StateHome.ScratchDirName));
        Assert.Equal("ok", DoctorCommand.CheckPlanDrift(plan).State);
    }

    // ------------------------------------------------------------------ 5. composed-prompt argv length

    [Fact]
    public void ArgvLength_Green_OnAnOrdinaryPrompt()
    {
        var check = DoctorCommand.CheckArgvLength(CleanPlan());
        Assert.Equal("ok", check.State);
        Assert.Contains("ceiling", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgvLength_Red_AndStatesMeasuredVersusCeiling()
    {
        var plan = CleanPlan(p => p.PromptExtra = new string('x', DoctorCommand.CreateProcessCommandLineCeiling + 1000));
        var check = DoctorCommand.CheckArgvLength(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("stage 'S1'", check.Message, StringComparison.Ordinal);
        Assert.Contains(DoctorCommand.CreateProcessCommandLineCeiling.ToString(Invariant), check.Message, StringComparison.Ordinal);
    }

    /// <summary>Bug #15's actual shape: the prompt is fine for CreateProcess and fatal through a
    /// <c>.cmd</c> shim, which is what an npm-installed agent CLI is on Windows. The ceiling the
    /// lint quotes has to follow how the agent is spawned, or it is a number rather than a warning.</summary>
    [Fact]
    public void ArgvLength_UsesTheCmdShimCeiling_WhenTheAgentIsAShim()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shim = Path.Combine(_dir, "agent-shim.cmd");
        File.WriteAllText(shim, "@echo off\r\n", Utf8);
        var plan = CleanPlan(p =>
        {
            p.Agent = new AgentConfig { Command = shim, Args = ["-p", "{prompt}"] };
            p.PromptExtra = new string('x', DoctorCommand.CmdExeCommandLineCeiling + 1000);
        });
        var check = DoctorCommand.CheckArgvLength(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains(DoctorCommand.CmdExeCommandLineCeiling.ToString(Invariant), check.Message, StringComparison.Ordinal);
    }

    /// <summary>Clearing CreateProcess' ceiling is not clearing THE ceiling: the same argv is fatal
    /// through a <c>.cmd</c> shim, and which of the two applies is decided by how somebody installed
    /// the agent CLI. A lint that said nothing until it was on the shim machine would only ever warn
    /// where the warning is too late, so the lower ceiling is reported as a warn wherever it is
    /// already exceeded.</summary>
    [Fact]
    public void ArgvLength_Warns_WhenOnlyTheShimCeilingIsExceeded()
    {
        var plan = CleanPlan(p => p.PromptExtra = new string('x', DoctorCommand.CmdExeCommandLineCeiling + 1000));
        var check = DoctorCommand.CheckArgvLength(plan, (DoctorCommand.CreateProcessCommandLineCeiling, "CreateProcess"));
        Assert.Equal("warn", check.State);
        Assert.Contains(DoctorCommand.CmdExeCommandLineCeiling.ToString(Invariant), check.Message, StringComparison.Ordinal);
        Assert.Contains("shim", check.Message, StringComparison.Ordinal);
    }

    /// <summary>The measurement is the runtime's own quoting, not a guess: an argument with a space
    /// is quoted, so it costs two characters more than its own length.</summary>
    [Fact]
    public void ArgvLength_MeasuresTheQuotedCommandLine()
    {
        Assert.Equal("git".Length + 1 + "status".Length, DoctorCommand.CommandLineLength("git", ["status"]));
        Assert.Equal("git".Length + 1 + "a b".Length + 2, DoctorCommand.CommandLineLength("git", ["a b"]));
    }

    // ------------------------------------------------------------------ 6. brace sweep

    [Fact]
    public async Task BraceSweep_Green_OnRealPlaceholdersAndDoubledBraces()
    {
        var plan = CleanPlan();
        WriteTemplate(plan, "session.md", "You are a DELIVER session for {planName}.\n\nRepo {repo}, stage {stage}.\nA literal {{brace}} is prose.\n");
        var check = await DoctorCommand.CheckTemplateBracesAsync(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public async Task BraceSweep_Red_AndNamesTheFileAndTheToken()
    {
        var plan = CleanPlan();
        WriteTemplate(plan, "session.md", "You are a DELIVER session for {planNam}.\n");
        var check = await DoctorCommand.CheckTemplateBracesAsync(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("session.md", check.Message, StringComparison.Ordinal);
        Assert.Contains("{planNam}", check.Message, StringComparison.Ordinal);
    }

    /// <summary>The sweep reaches the files <see cref="DoctorCommand.CheckPrompt"/> never renders —
    /// a pack is loaded into every prompt and nothing validated it.</summary>
    [Fact]
    public async Task BraceSweep_ReachesFilesTheRendererNeverOpens()
    {
        var plan = CleanPlan();
        WriteTemplate(plan, Path.Combine("packs", "house.md"), "House style. Never write {sessionNumbr}.\n");
        var check = await DoctorCommand.CheckTemplateBracesAsync(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("packs/house.md", check.Message, StringComparison.Ordinal);
        Assert.Contains("{sessionNumbr}", check.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 7. escalation-token sweep

    [Fact]
    public async Task EscalationSweep_Green_WhenTheTokenIsNowhereASessionReads()
    {
        var plan = CleanPlan();
        WriteTemplate(plan, "session.md", "You are a DELIVER session for {planName}.\nEscalate in prose, never by spelling the token.\n");
        var check = await DoctorCommand.CheckEscalationTokenAsync(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public async Task EscalationSweep_Red_WhenAStageNoteSpellsTheToken()
    {
        var plan = CleanPlan(p => p.Stages[0].Notes = "If you are stuck, write " + Escalation + " in the handoff.");
        var check = await DoctorCommand.CheckEscalationTokenAsync(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("stage 'S1' notes", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscalationSweep_Red_WhenATemplateSpellsTheToken()
    {
        var plan = CleanPlan();
        WriteTemplate(plan, "fix.md", "You are a FIX session.\nAsk with " + Escalation + " when blocked.\n");
        var check = await DoctorCommand.CheckEscalationTokenAsync(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("fix.md", check.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the worked example

    /// <summary>The plans this repo ships are the worked examples, and this era's own plan is the one
    /// driving the run that writes these lints. A rule it trips is a rule that would have to be
    /// explained away rather than fixed. CH1.2: the plan file names its repo RELATIVE to itself, so
    /// what is loaded here is this checkout wherever it sits — these three tests used to re-point
    /// <c>plan.Repo</c> by hand after a Load that could not have succeeded off this one machine.
    /// <para>The argv ceiling is STATED here, not resolved. Doctor resolves it, correctly: whether
    /// this machine's <c>agent.command</c> lands on a native binary or an npm <c>.cmd</c> shim decides
    /// which of the two Windows ceilings the engine will hit. But that is a fact about the box, not
    /// about the plan, and a gate whose verdict flips because a developer installed the agent CLI a
    /// different way is a gate that reports the weather. What this pins is the plan-side fact:
    /// composed and quoted, the longest argv this plan builds clears CreateProcess' ceiling.</para></summary>
    [Fact]
    public async Task DoctorIsGreenOnThisReposOwnPlan()
    {
        var root = RepoRoot();
        if (root is null) return; // not in a full checkout — soft skip, as ShippedPlans does
        var planPath = Path.Combine(root, "plans", "karvansara", "core.plan.json");
        if (!File.Exists(planPath)) return;

        var plan = PlanConfig.Load(planPath);

        var checks = new List<DoctorCommand.Check>
        {
            DoctorCommand.CheckGatePaths(plan),
            DoctorCommand.CheckHooks(plan),
            DoctorCommand.CheckCheckpointIds(plan),
            DoctorCommand.CheckPlanDrift(plan),
            DoctorCommand.CheckArgvLength(plan, (DoctorCommand.CreateProcessCommandLineCeiling, "CreateProcess")),
            await DoctorCommand.CheckTemplateBracesAsync(plan),
            await DoctorCommand.CheckEscalationTokenAsync(plan),
        };

        var failed = checks.Where(c => c.State == "fail").Select(c => $"{c.Name}: {c.Message}").ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        Assert.Equal(7, checks.Count);
    }

    /// <summary>...and the half the pinned case above deliberately does not inherit is still measured.
    /// This plan's packs push its longest argv well past cmd.exe's ceiling, so on a machine whose
    /// agent CLI is the npm shim it is already over — bug #21, live, one <c>agent.command</c> away.
    /// The lint has to SAY that on the machines where it is not yet fatal, or the only warning arrives
    /// where it is too late to act on.</summary>
    [Fact]
    public void ThisReposOwnPlanIsAlreadyOverTheCmdShimCeiling()
    {
        var root = RepoRoot();
        if (root is null) return;
        var planPath = Path.Combine(root, "plans", "karvansara", "core.plan.json");
        if (!File.Exists(planPath)) return;

        var plan = PlanConfig.Load(planPath);

        var check = DoctorCommand.CheckArgvLength(plan, (DoctorCommand.CreateProcessCommandLineCeiling, "CreateProcess"));
        Assert.Equal("warn", check.State);
        Assert.Contains(DoctorCommand.CmdExeCommandLineCeiling.ToString(Invariant), check.Message, StringComparison.Ordinal);
        Assert.Contains("shim", check.Message, StringComparison.Ordinal);
    }

    /// <summary>The same measurement under the shim ceiling is a FAIL, not a warn — the warn is the
    /// early word, not a softer verdict. Stated ceilings both times, so this says something about the
    /// plan on every machine rather than about the agent install on one.</summary>
    [Fact]
    public void TheShimCeilingIsAFailWhenItIsTheOneThatApplies()
    {
        var root = RepoRoot();
        if (root is null) return;
        var planPath = Path.Combine(root, "plans", "karvansara", "core.plan.json");
        if (!File.Exists(planPath)) return;

        var plan = PlanConfig.Load(planPath);

        var check = DoctorCommand.CheckArgvLength(
            plan, (DoctorCommand.CmdExeCommandLineCeiling, "claude.CMD is a command-interpreter shim"));
        Assert.Equal("fail", check.State);
        Assert.Contains(DoctorCommand.CmdExeCommandLineCeiling.ToString(Invariant), check.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ fixtures

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>The escalation token, assembled rather than written. See the class doc-comment.</summary>
    private static readonly string Escalation = "HUMAN" + ":";

    /// <summary>A plan that every lint is green on: a real repo directory, a tracker whose one row
    /// parses under the default conventions, and an agent that exists on every machine this builds on.</summary>
    private PlanConfig CleanPlan(Action<PlanConfig>? tweak = null)
    {
        var repo = Path.Combine(_dir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# t\n\n## Handoff\n\nnothing pending.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n" +
            "|---|---|---|---|---|\n" +
            "| S1.1 | the only row | TODO | - | - |\n", Utf8);

        var plan = new PlanConfig
        {
            Name = "ks14-fixture",
            Repo = repo,
            Tracker = "TRACKER.md",
            PlanFilePath = Path.Combine(repo, "fixture.plan.json"),
            Agent = new AgentConfig { Command = "git", Args = ["-p", "{prompt}"] },
        };
        plan.Stages.Add(new StageConfig { Id = "S1", Title = "the only stage", Sessions = 1 });
        tweak?.Invoke(plan);
        File.WriteAllText(plan.PlanFilePath, "{}", Utf8);
        return plan;
    }

    private static void WriteTemplate(PlanConfig plan, string relative, string body)
    {
        plan.TemplatesDir = "templates";
        var path = Path.Combine(plan.PlanDir, "templates", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body, Utf8);
    }

    /// <summary>A store holding one run of this plan whose event log records loading
    /// <paramref name="loadedVersion"/>, with the file on disk at <paramref name="fileVersion"/>. The
    /// state pointer in the repo is what aims <c>plan.ResolveState()</c> at it — no environment
    /// variable, so the fixture cannot leak into another test running beside it.</summary>
    private PlanConfig SeedDriftRig(int loadedVersion, int fileVersion, string runStatus)
    {
        var plan = CleanPlan(p => p.PlanVersion = fileVersion);
        var runId = "run-ks14-" + Guid.NewGuid().ToString("N")[..8];
        var dbPath = Path.Combine(plan.Repo, "state", "run.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var store = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
        _open.Add(store);
        store.SetRunId(runId);
        store.InitializeRun(runId, plan.Name, plan.Repo, null, EngineStamp.Parse("ks14"));
        store.Emit(new PlanReloaded { PlanVersion = loadedVersion, Stages = 1, Gates = 0 });
        store.FlushEvents();
        if (!string.Equals(runStatus, "running", StringComparison.Ordinal)) store.RecordRunEnd(runId, runStatus);
        store.Dispose();
        SqliteConnection.ClearAllPools();

        StatePointer.TryWrite(Path.Combine(plan.Repo, StateHome.ScratchDirName, StateHome.PointerFileName), dbPath, plan.Name);
        return plan;
    }

    /// <summary>Puts a live engine on this plan's store, the only way that is not a lie: the lock file
    /// an engine writes under the repo's <c>.conductor</c>, naming THIS process and its real start
    /// time — the <see cref="KS1_3LivenessReconciliationTests"/> idiom, so the fixture and the rule
    /// agree by construction. Released in <see cref="Dispose"/>: a lock left behind would make the
    /// next store in this class look driven.</summary>
    private void HoldTheEngineLock(PlanConfig plan)
    {
        var stateDir = Path.Combine(plan.Repo, StateHome.ScratchDirName);
        Directory.CreateDirectory(stateDir);
        EngineLock.Write(stateDir);
        _held.Add(stateDir);
    }

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "plans"))) return d.FullName;
        return null;
    }
}
