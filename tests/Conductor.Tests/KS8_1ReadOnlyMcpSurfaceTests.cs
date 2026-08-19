using System.Text.Json;
using System.Text.RegularExpressions;

using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS8.1 — the outward-facing MCP surface serves resources and has no tools.
///
/// <para>The falsifiable exit is two claims and both are measured here rather than read off the ADR:
/// a client LISTS RUNS and quotes the RECONCILED status (not the stored word), and NO WRITE TOOL
/// EXISTS on the surface. The second is proved against the sixteen tool names scanned out of
/// <c>McpTaskServer.cs</c> — the agent-facing server's real list — so a seventeenth tool added there
/// tomorrow is automatically part of this battery instead of quietly outside it.</para>
///
/// <para>The reconciliation claim needs a run that LIES: a row stored as <c>running</c> whose store no
/// engine is holding. The stored word is <c>running</c>, the true word is <c>orphaned</c>, and a
/// surface that just echoed the column would pass a weaker test and fail this one.</para>
/// </summary>
public sealed class KS8_1ReadOnlyMcpSurfaceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;

    public KS8_1ReadOnlyMcpSurfaceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks81-" + Guid.NewGuid().ToString("N")[..10]);
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

    /// <summary>A real run through the real writer, then catalogued — the K3.2 rig's discipline: what
    /// the surface reads is what the engine actually stores, not a hand-rolled table.</summary>
    private string SeedRun(string repoName, string plan, string runId, string status, decimal cost = 2.5m)
    {
        var repo = Path.Combine(_tmp, repoName);
        Directory.CreateDirectory(repo);
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan, repo, "master", Conductor.Core.EngineStamp.Parse("0.4.1+test"));
            store.SetRunId(runId);
            store.InitializeStage(runId, "S1", "First stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "First stage" });
            store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "work", Attempt = 1 });
            store.RecordSession(runId, "S1", 1, "work",
                new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc), "advance",
                agentSessionId: null, resumeCount: 0, attempt: 1,
                gateSummary: "ok", resultSummary: "session 1", commitCount: 1, newlyDone: null);
            store.RecordCost(runId, 1, "agent", 100, 200, 0, 300, cost, 1000);
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

    private McpObserveServer Server() => new(_root);

    private static JsonRpcResponse Ask(McpObserveServer server, string method, object? @params = null)
    {
        var req = new JsonRpcRequest
        {
            Method = method,
            Id = JsonSerializer.SerializeToElement(1),
            Params = @params is null ? null : JsonSerializer.SerializeToElement(@params),
        };
        var response = server.HandleRequest(req);
        Assert.NotNull(response);
        return response!;
    }

    private static JsonElement ResultOf(JsonRpcResponse r)
    {
        Assert.Null(r.Error);
        Assert.NotNull(r.Result);
        return r.Result!.Value;
    }

    /// <summary>The body of a <c>resources/read</c> answer, parsed.</summary>
    private JsonElement Read(string uri)
    {
        var result = ResultOf(Ask(Server(), "resources/read", new { uri }));
        var text = result.GetProperty("contents")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ------------------------------------------------------------------ no tools exist

    [Fact]
    public void Initialize_declares_resources_and_does_not_declare_tools()
    {
        var result = ResultOf(Ask(Server(), "initialize",
            new { protocolVersion = "2025-06-18", capabilities = new { } }));

        var caps = result.GetProperty("capabilities");
        Assert.True(caps.TryGetProperty("resources", out _), "the surface must declare a resources capability");
        Assert.False(caps.TryGetProperty("tools", out _),
            "declaring a tools capability is what makes a client go looking for tools - there are none");
        Assert.Equal("conductor-observe", result.GetProperty("serverInfo").GetProperty("name").GetString());
        // Negotiated, not dictated: a 2025 client is answered in its own revision.
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void ToolsList_is_empty()
    {
        var result = ResultOf(Ask(Server(), "tools/list"));
        Assert.Equal(JsonValueKind.Array, result.GetProperty("tools").ValueKind);
        Assert.Equal(0, result.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public void Every_tool_the_agent_surface_offers_is_refused_here()
    {
        var names = AgentSurfaceToolNames();
        Assert.True(names.Count >= 16,
            $"only {names.Count} tool names scanned out of McpTaskServer.cs - the scan is broken, not the surface");
        Assert.Contains("task_update", names);
        Assert.Contains("inject_instruction", names);

        var server = Server();
        foreach (var name in names)
        {
            var response = Ask(server, "tools/call", new { name, arguments = new { } });
            Assert.Null(response.Result);
            Assert.NotNull(response.Error);
            Assert.Equal(-32601, response.Error!.Code);
            Assert.Contains("read-only", response.Error.Message, StringComparison.Ordinal);
            Assert.Contains("adr/0007", response.Error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>KS4.5's rule applied to a surface instead of a verdict: the guarantee is structural, so
    /// assert on the structure. The observe server's sources may not name the type that can write.
    /// Every read it does goes through <c>RunArchive</c>'s <c>Mode=ReadOnly</c> connection, and this is
    /// what stops the next hand from wiring an <c>IRunStore</c> in "just to read one more column".</summary>
    [Fact]
    public void Observe_server_sources_never_reach_a_writable_store()
    {
        var forbidden = new[] { "IRunStore", "SqliteRunStore", "AppendEvent", "ExecuteNonQuery", "McpTaskServer.Handle" };
        foreach (var file in Directory.GetFiles(SrcDir("Conductor.Core", "Integrations"), "McpObserveServer*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unknown_methods_and_unknown_uris_are_refused_by_name()
    {
        var bad = Ask(Server(), "resources/read", new { uri = "conductor://runs/nope/delete" });
        Assert.NotNull(bad.Error);
        Assert.Contains("unknown view 'delete'", bad.Error!.Message, StringComparison.Ordinal);

        var wrongScheme = Ask(Server(), "resources/read", new { uri = "file:///etc/passwd" });
        Assert.NotNull(wrongScheme.Error);
        Assert.Contains("unknown resource", wrongScheme.Error!.Message, StringComparison.Ordinal);

        // The two halves fail differently. A catalogue holds rows whose database is gone and those
        // rows have no run id at all, so a client that walks the index and forgets to skip them asks
        // for `runs//status` - and "names a run but no view" would send it to the wrong question.
        var noRun = Ask(Server(), "resources/read", new { uri = "conductor://runs//status" });
        Assert.NotNull(noRun.Error);
        Assert.Contains("names no run", noRun.Error!.Message, StringComparison.Ordinal);

        var noView = Ask(Server(), "resources/read", new { uri = "conductor://runs/abcd1234" });
        Assert.NotNull(noView.Error);
        Assert.Contains("no view", noView.Error!.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ it lists runs, reconciled

    [Fact]
    public void History_lists_every_run_and_quotes_the_reconciled_status()
    {
        SeedRun("alpha", "core", "run-alpha-0001", "completed");
        SeedRun("beta", "edge", "run-beta-00002", "running");

        var history = Read(McpObserveServer.HistoryUri);
        Assert.Equal(2, history.GetProperty("count").GetInt32());

        var runs = history.GetProperty("runs").EnumerateArray().ToList();
        var live = runs.Single(r => r.GetProperty("runId").GetString() == "run-beta-00002");

        // The whole point. The column says running; nothing is driving that store; the surface says so.
        Assert.Equal("running", live.GetProperty("storedStatus").GetString());
        Assert.Equal("orphaned", live.GetProperty("status").GetString());

        var done = runs.Single(r => r.GetProperty("runId").GetString() == "run-alpha-0001");
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal("completed", done.GetProperty("storedStatus").GetString());
        Assert.Equal(1, done.GetProperty("checkpointsDone").GetInt32());
        Assert.Equal(2, done.GetProperty("checkpointsTotal").GetInt32());
        Assert.Equal("core", done.GetProperty("plan").GetString());
        Assert.Equal(2.5m, done.GetProperty("costUsd").GetDecimal());
    }

    /// <summary>Caught live, against the real catalogue, not by reading the type: the catalogue's
    /// <c>plan</c> is the name the entry was CREATED with, and one store holds every run of a
    /// (repo, plan) pair. Conductor's own store was catalogued as "Karvansara core" and now holds the
    /// edge run, so a surface that printed the catalogue column labelled this very run with the
    /// previous plan's name.</summary>
    [Fact]
    public void History_names_the_runs_own_plan_not_the_catalogue_entrys()
    {
        var db = SeedRun("alpha", "core", "run-alpha-0001", "completed");
        // A second run in the SAME store under a renamed plan — what a rename actually looks like.
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun("run-alpha-0002", "the renamed plan", Path.Combine(_tmp, "alpha"),
                "master", Conductor.Core.EngineStamp.Parse("0.4.1+test"));
            store.RecordRunEnd("run-alpha-0002", "completed");
        }

        var runs = Read(McpObserveServer.HistoryUri).GetProperty("runs").EnumerateArray().ToList();
        var renamed = runs.Single(r => r.GetProperty("runId").GetString() == "run-alpha-0002");
        Assert.Equal("the renamed plan", renamed.GetProperty("plan").GetString());
        Assert.Equal("core", renamed.GetProperty("cataloguedAs").GetString());
    }

    [Fact]
    public void ResourcesList_offers_history_and_a_status_and_money_resource_per_run()
    {
        SeedRun("alpha", "core", "run-alpha-0001", "completed");
        SeedRun("beta", "edge", "run-beta-00002", "running");

        var result = ResultOf(Ask(Server(), "resources/list"));
        var uris = result.GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("uri").GetString()!).ToList();

        Assert.Contains(McpObserveServer.HistoryUri, uris);
        Assert.Contains("conductor://runs/run-alph/status", uris);
        Assert.Contains("conductor://runs/run-alph/money", uris);
        Assert.Contains("conductor://runs/run-beta/status", uris);
        Assert.Contains("conductor://runs/run-beta/money", uris);
        Assert.Equal(5, uris.Count);

        var templates = ResultOf(Ask(Server(), "resources/templates/list"));
        Assert.Equal(2, templates.GetProperty("resourceTemplates").GetArrayLength());
    }

    [Fact]
    public void Status_resource_carries_both_words_and_the_state_contract()
    {
        SeedRun("beta", "edge", "run-beta-00002", "running");

        var status = Read("conductor://runs/run-beta/status");
        Assert.Equal("run-beta-00002", status.GetProperty("runId").GetString());
        Assert.Equal("running", status.GetProperty("storedStatus").GetString());
        Assert.Equal("orphaned", status.GetProperty("status").GetString());
        Assert.False(status.GetProperty("storeLooksLive").GetBoolean());

        // The Face's own projection, from the archive rather than a running engine.
        var state = status.GetProperty("state");
        Assert.Equal("edge", state.GetProperty("planName").GetString());
        Assert.Equal("orphaned", state.GetProperty("status").GetString());
        Assert.Equal(1, state.GetProperty("doneCount").GetInt32());
        Assert.Equal(2, state.GetProperty("totalCount").GetInt32());
        Assert.Equal("S1", state.GetProperty("stageId").GetString());
    }

    [Fact]
    public void Money_resource_prices_one_run_in_billed_dollars()
    {
        SeedRun("alpha", "core", "run-alpha-0001", "completed", cost: 7.25m);

        var money = Read("conductor://runs/run-alph/money");
        Assert.Equal("run-alph", money.GetProperty("scope").GetString());
        Assert.Equal(7.25m, money.GetProperty("total").GetProperty("costUsd").GetDecimal());
        var run = money.GetProperty("runs")[0];
        Assert.Equal("run-alpha-0001", run.GetProperty("runId").GetString());
        Assert.Equal("core", run.GetProperty("plan").GetString());
    }

    [Fact]
    public void A_selector_that_names_nothing_is_refused_with_a_sentence_not_a_stack()
    {
        var response = Ask(Server(), "resources/read", new { uri = "conductor://runs/zzzz9999/status" });
        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error!.Code);
        Assert.Contains("zzzz9999", response.Error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ source scan

    /// <summary>Every tool name the AGENT-facing server advertises, read off its source. Hand-typing
    /// the list is how a battery like this rots: the sixteenth tool gets added to one file and the
    /// test that was supposed to cover it never hears about it.</summary>
    private static IReadOnlyList<string> AgentSurfaceToolNames()
    {
        var text = File.ReadAllText(Path.Combine(SrcDir("Conductor.Core", "Integrations"), "McpTaskServer.cs"));
        return [.. Regex.Matches(text, @"new \{ name = ""(?<n>[a-z_]+)""", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups["n"].Value).Distinct(StringComparer.Ordinal)];
    }

    private static string SrcDir(params string[] parts) =>
        Path.Combine([RepoRoot(), "src", .. parts]);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Conductor.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
