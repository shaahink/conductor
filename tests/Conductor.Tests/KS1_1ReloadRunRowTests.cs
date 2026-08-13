using System.Text;
using System.Text.Json;

using Conductor.Core;
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
/// KS1.1 — the run row tells the truth about the limits a run is governed by, and about the ones it
/// began under.
///
/// <para>K3.3 put a limits snapshot on the row and per-session snapshots beside it, and the row's
/// snapshot was written by exactly one caller: <c>InitializeRun</c>, once per PROCESS. The live plan
/// reload — the single mechanism by which limits change mid-run, and the case K3.3 was written to
/// answer — never touched the row. So a run whose cap was raised at the session boundary went on
/// reporting the cap the engine happened to start with, and the only way to see the change was to
/// read the per-session column and infer it. The row was the surface every other machine reads.</para>
///
/// <para>The tests that matter are the two about what must NOT move: a reload and a resume both write
/// the "now" value, and neither may write the launch value. A single column cannot hold both, which
/// is why v13 exists; a test that only checked the reload wrote something would pass on a schema that
/// had quietly erased where the run started.</para>
/// </summary>
public sealed class KS1_1ReloadRunRowTests : IDisposable
{
    private const string RunId = "run-ks11-0001";
    private const long LaunchCap = 24_000_000;
    private const long ReloadedCap = 32_000_000;

    private readonly string _tmp;
    private readonly List<IDisposable> _open = [];

    public KS1_1ReloadRunRowTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks11-" + Guid.NewGuid().ToString("N")[..10]);
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

    // ------------------------------------------------------------------ the reload

    [Fact]
    public void Reload_UpdatesRunRowLimits()
    {
        var rig = Rig(LaunchCap);
        rig.Ctx.EnsureRunRow();                       // what the run loop does before its first session
        Assert.Equal(Snapshot(LaunchCap), Row(rig.Store)["limits_json"]);

        WritePlan(rig.Repo, ReloadedCap);             // the Plan tab's edit, on disk
        rig.Loop.ApplyPlanReload();                   // the session boundary

        var row = Row(rig.Store);
        Assert.Equal(Snapshot(ReloadedCap), row["limits_json"]);
        Assert.Equal(1L, Convert.ToInt64(row["limits_reload_count"], Invariant));
        Assert.NotNull(row["limits_reloaded_utc"]);
        // and the swap really happened, rather than the row being written by something else
        rig.Store.FlushEvents();
        Assert.Single(rig.Store.ReadAllEvents(RunId).OfType<PlanReloaded>());
    }

    [Fact]
    public void Reload_DoesNotOverwriteAtLaunchLimits()
    {
        var rig = Rig(LaunchCap);
        rig.Ctx.EnsureRunRow();

        WritePlan(rig.Repo, ReloadedCap);
        rig.Loop.ApplyPlanReload();
        WritePlan(rig.Repo, 8_000_000);               // a second edit, in the other direction
        rig.Loop.ApplyPlanReload();

        Assert.Equal(Snapshot(LaunchCap), Row(rig.Store)["limits_json_at_launch"]);

        // and the browsing surface says both, labelled
        rig.Store.Dispose();
        SqliteConnection.ClearAllPools();
        var run = Assert.Single(RunArchive.TryOpen(rig.DbPath)!.Runs());
        Assert.Equal(LaunchCap, run.LimitsAtLaunch!.SessionTokenCap);
        Assert.Equal(8_000_000, run.Limits!.SessionTokenCap);
        Assert.True(run.LimitsChangedInFlight);
        Assert.Equal(2, run.LimitsReloads);
    }

    /// <summary>A reload that lands back on the limits the run launched with is still a reload. The
    /// count is recorded at the boundary for exactly this case — a reader diffing the two snapshots
    /// would call this run untouched, and the row would agree with it for the wrong reason.</summary>
    [Fact]
    public void ReloadBackToTheLaunchValue_StillCounts_AndReadsAsUnchanged()
    {
        var rig = Rig(LaunchCap);
        rig.Ctx.EnsureRunRow();

        WritePlan(rig.Repo, ReloadedCap);
        rig.Loop.ApplyPlanReload();
        WritePlan(rig.Repo, LaunchCap);
        rig.Loop.ApplyPlanReload();

        rig.Store.Dispose();
        SqliteConnection.ClearAllPools();
        var run = Assert.Single(RunArchive.TryOpen(rig.DbPath)!.Runs());
        Assert.False(run.LimitsChangedInFlight);       // the two ends genuinely match
        Assert.Equal(2, run.LimitsReloads);            // and two swaps genuinely happened
    }

    // ------------------------------------------------------------------ the resume

    [Fact]
    public void Resume_DoesNotOverwriteAtLaunchLimits()
    {
        // Two process starts against one run: `InitializeRun` is an upsert and its ON CONFLICT clause
        // refreshes everything the resuming process knows — which is right for the engine stamp and
        // for the current limits, and would be a lie for the launch value.
        var rig = Rig(LaunchCap);
        rig.Ctx.EnsureRunRow();

        var resumed = Rig(ReloadedCap, store: rig.Store, repo: rig.Repo, planName: "resume.plan.json");
        resumed.Ctx.EnsureRunRow();

        var row = Row(rig.Store);
        Assert.Equal(Snapshot(LaunchCap), row["limits_json_at_launch"]);
        Assert.Equal(Snapshot(ReloadedCap), row["limits_json"]);
        Assert.Equal(0L, Convert.ToInt64(row["limits_reload_count"], Invariant));  // a resume is not a reload
    }

    // ------------------------------------------------------------------ the reload that does not happen

    /// <summary>The two ways <c>ApplyPlanReload</c> gives up (RunLoop.Reload.cs, the early returns):
    /// there is no file to re-read, or the file does not parse. Both leave the old plan running, so
    /// both must leave the record saying what is actually running — a "reloaded to" line written for
    /// a swap that never happened is worse than no line at all.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("unparseable")]
    public void SkippedReload_WritesNothing(string how)
    {
        var rig = Rig(LaunchCap);
        rig.Ctx.EnsureRunRow();

        var planPath = rig.Ctx.Plan.PlanFilePath;
        if (how == "missing") File.Delete(planPath);
        else File.WriteAllText(planPath, "{ \"name\": \"ks11\", ", new UTF8Encoding(false));

        rig.Loop.ApplyPlanReload();

        var row = Row(rig.Store);
        Assert.Equal(Snapshot(LaunchCap), row["limits_json"]);
        Assert.Equal(Snapshot(LaunchCap), row["limits_json_at_launch"]);
        Assert.Equal(0L, Convert.ToInt64(row["limits_reload_count"], Invariant));
        Assert.Null(row["limits_reloaded_utc"]);
        rig.Store.FlushEvents();
        Assert.Empty(rig.Store.ReadAllEvents(RunId).OfType<PlanReloaded>());
    }

    // ------------------------------------------------------------------ the published payload

    /// <summary><c>RunHistoryItemJson</c> is a contract, and its doc-comment says so. The new members
    /// are additive with defaults, which means the K3.2 and K3.3 keys keep their names AND their
    /// positions — a positional assertion because a source-generated record serialises in declaration
    /// order, so reordering the parameter list is a silent break that a name-only check would pass.</summary>
    [Fact]
    public void TheHistoryPayloadCarriesBothSnapshots_Additively()
    {
        var item = new RunHistoryItemJson(
            "r1", "C:/repo", "core", "running", "0.3.1+abc", "master",
            "2026-08-13T00:00:00Z", null, null, 3, 1, 7, 1.25m, 900,
            "C:/repo/run.db", "slug", null, Readable: true,
            "abc", false, RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = ReloadedCap }),
            RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = LaunchCap }), 1,
            "2026-08-13T01:00:00Z");

        var json = JsonSerializer.Serialize(new RunHistoryListJson([item]),
            RunHistoryJsonContext.Default.RunHistoryListJson);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("runs")[0].EnumerateObject().Select(p => p.Name).ToList();

        string[] published =
        [
            "runId", "repo", "plan", "status", "engine", "branch",
            "startedUtc", "endedUtc", "lastActivityUtc",
            "sessions", "checkpointsDone", "checkpointsTotal", "costUsd", "tokens",
            "runDb", "slug", "importedFrom", "readable",
            "engineCommit", "engineDirty", "limits",
        ];
        string[] added = ["limitsAtLaunch", "limitsReloads", "limitsReloadedUtc"];
        // KS1.3 appended two more on exactly the same terms. The assertion GROWS rather than loosens:
        // each era's keys stay pinned to their own positions, so a later checkpoint that reorders an
        // earlier one's fields still fails here.
        string[] addedByKs13 = ["storedStatus", "storeLive"];
        Assert.Equal(published, keys.Take(published.Length));
        Assert.Equal(added, keys.Skip(published.Length).Take(added.Length));
        Assert.Equal(addedByKs13, keys.Skip(published.Length + added.Length));

        var atLaunch = doc.RootElement.GetProperty("runs")[0].GetProperty("limitsAtLaunch");
        Assert.Equal(LaunchCap, atLaunch.GetProperty("sessionTokenCap").GetInt64());
        Assert.Equal(ReloadedCap,
            doc.RootElement.GetProperty("runs")[0].GetProperty("limits").GetProperty("sessionTokenCap").GetInt64());
    }

    // ------------------------------------------------------------------ the rig

    private static readonly System.Globalization.CultureInfo Invariant =
        System.Globalization.CultureInfo.InvariantCulture;

    private static string Snapshot(long cap) =>
        RunLimitsSnapshot.From(new LimitsConfig { MaxSessionTokens = cap }).ToJson();

    private static Dictionary<string, object?> Row(IRunStore store) =>
        store.Query("SELECT limits_json, limits_json_at_launch, limits_reload_count, limits_reloaded_utc " +
                    "FROM runs WHERE run_id = @id", ("@id", RunId))[0];

    private sealed record LoopRig(string Repo, string DbPath, SqliteRunStore Store, RunContext Ctx, RunLoop Loop);

    /// <summary>A run loop wired for the boundary and nothing else.
    /// <para><c>SessionRunner</c> and <c>VerdictEngine</c> go in null on purpose: the reload path must
    /// not reach either of them (it runs BETWEEN sessions, with no agent alive and no verdict pending),
    /// and passing them as nulls makes that an assertion the test would fail rather than a claim in a
    /// comment. The dispatcher is supplied so the lazy one — which does close over the verdict engine —
    /// is never built.</para></summary>
    private LoopRig Rig(long sessionTokenCap, SqliteRunStore? store = null, string? repo = null,
                        string planName = "ks11.plan.json")
    {
        repo ??= NewRepo();
        var planPath = WritePlan(repo, sessionTokenCap, planName);
        var plan = PlanConfig.Load(planPath);

        var dbPath = Path.Combine(repo, "run.db");
        if (store is null)
        {
            store = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
            _open.Add(store);
        }
        store.SetRunId(RunId);

        var state = new RunState { RunId = RunId, PlanName = plan.Name };
        var sink = new PlainSink();
        var lessons = new LessonsManager(plan.StateDir);
        var qa = new Conductor.Planning.DefaultQaPolicy();
        var webhooks = new WebhookNotifier(plan, NullLogger<WebhookNotifier>.Instance);
        _open.Add(webhooks);

        var ctx = new RunContext(
            plan, state, new RunOptions(DryRun: true, Once: true, MaxSessions: 0),
            sink, store, new PromptBuilder(plan, new PersonaRegistry(plan), lessons, qa),
            lessons, new CheckpointPlanner(), ProgressProviderFactory.Create(plan),
            AgentProviderFactory.Create(plan.Agent), store,
            processSupervisor: null, controlInbox: null,
            new NoOpTelegramService(), webhooks,
            workflowResolver: null, NullLogger<KS1_1ReloadRunRowTests>.Instance);

        var dispatcher = new ControlDispatcher(plan, state, sink, store, log: _ => { }, save: () => { },
            deleteControlFile: () => { }, skipStage: (_, _) => { },
            approveAwaitingOwner: _ => Task.CompletedTask);
        var loop = new RunLoop(ctx, sessions: null!, verdicts: null!,
            new GateOrchestrator(plan, state, store, store), new LaneCoordinator(plan, state, sink, store, _ => { }),
            dispatcher, saveAndReport: () => { });

        return new LoopRig(repo, dbPath, store, ctx, loop);
    }

    private string NewRepo()
    {
        var repo = Path.Combine(_tmp, "repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n" +
            "|---|---|---|---|---|\n| S1.1 | one | TODO | | |\n", new UTF8Encoding(false));
        return repo;
    }

    private static string WritePlan(string repo, long sessionTokenCap, string planName = "ks11.plan.json")
    {
        var plan = new PlanConfig
        {
            Name = "ks11",
            Repo = repo.Replace('\\', '/'),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "S1", Title = "one", Sessions = 1 }],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "echo", "{prompt}"], Provider = "opencode" },
        };
        plan.Limits.MaxSessionTokens = sessionTokenCap;
        var path = Path.Combine(repo, planName);
        File.WriteAllText(path, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
