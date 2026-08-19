using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Interop;
using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS8.2 — a run exports as an ATIF trajectory.
///
/// <para>The falsifiable exit is "an exported Karvan-core trajectory validates against the ATIF
/// schema", and the validator that settles it is Harbor's OWN pydantic model, not this file:
/// <c>harbor.models.trajectories.trajectory.Trajectory</c> is configured <c>extra="forbid"</c>, so a
/// misspelled field is a hard rejection rather than a silently dropped key. That run is recorded in
/// <c>.conductor/evidence/KS8/</c> because it needs a python environment the gate battery does not
/// have. What is pinned HERE is everything that validation cannot see: that the numbers are the
/// billed ones, that <c>prompt_tokens</c> includes the cache reads the way ATIF's own formula
/// requires, that the reconciled status rides the artifact, and that a database with no event log
/// still exports a spine.</para>
/// </summary>
public sealed class KS8_2AtifExportTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;
    private static readonly DateTimeOffset Exported = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public KS8_2AtifExportTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks82-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>A real run through the real writer: two sessions, a gate battery on each, one of
    /// them red — a trajectory of nothing but green sessions would not exercise the observation.</summary>
    private string SeedRun(string plan, string runId, string status)
    {
        var repo = Path.Combine(_tmp, "alpha");
        Directory.CreateDirectory(repo);
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan, repo, "master", Conductor.Core.EngineStamp.Parse("0.4.1+test"));
            store.SetRunId(runId);
            store.InitializeStage(runId, "S1", "First stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "First stage" });

            store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "work", Attempt = 1 });
            store.Emit(new GateFinished { Name = "build", Passed = true, ExitCode = 0, DurationMs = 1200 });
            store.Emit(new GateFinished { Name = "tests", Passed = true, ExitCode = 0, DurationMs = 45000 });
            store.RecordSession(runId, "S1", 1, "work",
                new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc), "advance",
                agentSessionId: null, resumeCount: 0, attempt: 1,
                gateSummary: "build:OK · tests:OK", resultSummary: "landed the first checkpoint",
                commitCount: 2, newlyDone: "C1");
            // in / out / think / cacheRead / cost / wallMs
            store.RecordCost(runId, 1, "agent", 1_000, 500, 200, 41_700, 3.25m, 60_000);

            store.Emit(new SessionStarted { Number = 2, StageId = "S1", Kind = "fix", Attempt = 2 });
            store.Emit(new GateFinished { Name = "tests", Passed = false, ExitCode = 1, DurationMs = 30000 });
            store.RecordSession(runId, "S1", 2, "fix",
                new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 1, 10, 20, 0, DateTimeKind.Utc), "retry",
                agentSessionId: null, resumeCount: 1, attempt: 2,
                gateSummary: "tests:FAIL", resultSummary: "could not land it", commitCount: 0, newlyDone: null);
            store.RecordCost(runId, 2, "agent", 800, 400, 0, 21_200, 1.75m, 20_000);
            store.RecordCost(runId, 2, "gate", 0, 0, 0, 0, 0.02m, 0);

            store.SeedCheckpoints(runId,
            [
                ("C1", "S1", "First checkpoint", "DONE", "abc1234", "evidence/one.md"),
                ("C2", "S1", "Second checkpoint", "TODO", "-", "-"),
            ]);
            if (status != "running") store.RecordRunEnd(runId, status);
        }
        StateCatalogue.Upsert(_root, repo, plan, db);
        return db;
    }

    /// <summary>Export whatever the catalogue holds, parsed.</summary>
    private JsonElement Export(string selector, out ArchiveView view)
    {
        view = ArchiveView.Open(_root, selector, out var refusal)
            ?? throw new InvalidOperationException(refusal);
        var archive = RunArchive.TryOpen(view.RunDbPath)!;
        var json = AtifExport.Serialize(view.Run, RunHistory.RepoLabel(view.Repo), view.Status,
            archive.Sessions(view.Run.RunId), archive.Costs(view.Run.RunId), view.Log(), Exported);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static JsonElement[] Steps(JsonElement doc) => [.. doc.GetProperty("steps").EnumerateArray()];

    private static JsonElement[] AgentSteps(JsonElement doc) =>
        [.. Steps(doc).Where(s => s.GetProperty("source").GetString() == "agent")];

    // ------------------------------------------------------------------ shape

    [Fact]
    public void The_document_is_ATIF_v1_7_and_names_the_run()
    {
        SeedRun("core", "run-alpha-0001", "completed");
        var doc = Export("run-alph", out _);

        Assert.Equal("ATIF-v1.7", doc.GetProperty("schema_version").GetString());
        Assert.Equal("run-alpha-0001", doc.GetProperty("session_id").GetString());
        Assert.Equal("run-alpha-0001", doc.GetProperty("trajectory_id").GetString());
        Assert.Equal("conductor", doc.GetProperty("agent").GetProperty("name").GetString());
        Assert.Equal("core", doc.GetProperty("agent").GetProperty("extra").GetProperty("plan").GetString());
    }

    [Fact]
    public void Steps_are_the_run_framed_by_system_and_one_agent_step_per_session()
    {
        SeedRun("core", "run-alpha-0001", "completed");
        var steps = Steps(Export("run-alph", out _));

        // brief, stage entered, session 1, session 2, run ended
        Assert.Equal(5, steps.Length);
        Assert.Equal(["system", "system", "agent", "agent", "system"],
            steps.Select(s => s.GetProperty("source").GetString() ?? "").ToArray());
        Assert.Equal([1, 2, 3, 4, 5], steps.Select(s => s.GetProperty("step_id").GetInt32()).ToArray());
        Assert.Contains("Stage S1 entered — First stage",
            steps[1].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("landed the first checkpoint", steps[2].GetProperty("message").GetString());
        Assert.Contains("Run completed after 2 sessions",
            steps[4].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>ATIF's own derivation is <c>non_cached = prompt_tokens - cached_tokens</c>, so the
    /// cache reads live INSIDE prompt_tokens. Conductor stores them beside the input tokens, and
    /// forgetting the addition is a silent 98%-of-the-corpus undercount, not a crash.</summary>
    [Fact]
    public void Prompt_tokens_include_the_cache_reads_and_the_dollars_are_the_billed_ones()
    {
        SeedRun("core", "run-alpha-0001", "completed");
        var first = AgentSteps(Export("run-alph", out _))[0].GetProperty("metrics");

        Assert.Equal(42_700, first.GetProperty("prompt_tokens").GetInt64());   // 1_000 in + 41_700 cache
        Assert.Equal(41_700, first.GetProperty("cached_tokens").GetInt64());
        Assert.Equal(700, first.GetProperty("completion_tokens").GetInt64());  // 500 out + 200 reasoning
        Assert.Equal(3.25, first.GetProperty("cost_usd").GetDouble(), 4);
        Assert.Equal(200, first.GetProperty("extra").GetProperty("reasoning_tokens").GetInt64());
    }

    [Fact]
    public void Final_metrics_total_every_cost_row_including_the_non_agent_ones()
    {
        SeedRun("core", "run-alpha-0001", "completed");
        var final = Export("run-alph", out _).GetProperty("final_metrics");

        Assert.Equal(64_700, final.GetProperty("total_prompt_tokens").GetInt64());   // 1_800 in + 62_900 cache
        Assert.Equal(62_900, final.GetProperty("total_cached_tokens").GetInt64());
        Assert.Equal(1_100, final.GetProperty("total_completion_tokens").GetInt64());
        Assert.Equal(5.02, final.GetProperty("total_cost_usd").GetDouble(), 4);      // 3.25 + 1.75 + 0.02 gate
        Assert.Equal(5, final.GetProperty("total_steps").GetInt32());
    }

    /// <summary>The gate event carries no session number. Attribution is by walking the fold in
    /// order, so this is the test that catches a rewrite that starts trusting a field instead.</summary>
    [Fact]
    public void Observations_carry_the_battery_and_each_gate_lands_under_its_own_session()
    {
        SeedRun("core", "run-alpha-0001", "completed");
        var agents = AgentSteps(Export("run-alph", out _));

        var first = agents[0].GetProperty("observation").GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(3, first.Count); // the session summary, then build and tests
        Assert.Contains("checkpoints closed: C1", first[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("commits: 2", first[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(["build", "tests"],
            first.Skip(1).Select(r => r.GetProperty("extra").GetProperty("gate").GetString() ?? "").ToArray());

        var second = agents[1].GetProperty("observation").GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, second.Count); // session 2 ran ONE gate; session 1's two must not leak in
        Assert.Contains("gate tests: FAILED", second[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.False(second[1].GetProperty("extra").GetProperty("passed").GetBoolean());
        Assert.Equal("fix", agents[1].GetProperty("extra").GetProperty("session_kind").GetString());
        Assert.Equal(2, agents[1].GetProperty("extra").GetProperty("attempt").GetInt32());
    }

    /// <summary>A shareable artifact that repeated the stored column would carry the run-that-never-
    /// ended lie further than the listing ever did. Both words ship.</summary>
    [Fact]
    public void The_artifact_carries_the_reconciled_word_beside_the_stored_one()
    {
        SeedRun("core", "run-alpha-0002", "running");
        var extra = Export("run-alph", out _).GetProperty("extra");

        Assert.Equal("orphaned", extra.GetProperty("status").GetString());
        Assert.Equal("running", extra.GetProperty("stored_status").GetString());
        Assert.Equal("conductor history export --atif", extra.GetProperty("generator").GetString());
        Assert.Equal("2026-08-19T12:00:00.0000000Z", extra.GetProperty("exported_utc").GetString());
        Assert.True(extra.GetProperty("event_log_steps").GetBoolean());
    }

    /// <summary>The v1..v10 databases predate half the event kinds. The session rows are the spine
    /// precisely so those runs still export something a reader can use — and <c>event_log_steps</c>
    /// says which of the two they are holding, so a thin trajectory reads as thin rather than as a
    /// run that ran no gates.</summary>
    [Fact]
    public void A_run_whose_log_did_not_survive_still_exports_its_spine()
    {
        SeedRun("core", "run-alpha-0001", "completed");
        var view = ArchiveView.Open(_root, "run-alph", out _)!;
        var archive = RunArchive.TryOpen(view.RunDbPath)!;

        var json = AtifExport.Serialize(view.Run, "alpha", view.Status,
            archive.Sessions(view.Run.RunId), archive.Costs(view.Run.RunId), [], Exported);
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.False(doc.GetProperty("extra").GetProperty("event_log_steps").GetBoolean());
        Assert.Equal(2, AgentSteps(doc).Length);
        // No stage title without the log — the step names the stage rather than inventing one.
        Assert.Equal("Stage S1 entered.", Steps(doc)[1].GetProperty("message").GetString());
        // And exactly one observation result: the session summary, with no gate breakdown to add.
        Assert.Single(AgentSteps(doc)[0].GetProperty("observation").GetProperty("results").EnumerateArray());
    }
}
