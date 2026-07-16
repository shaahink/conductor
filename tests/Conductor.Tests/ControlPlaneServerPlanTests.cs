using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>M6.3 wire contract: <c>GET /plan</c> serves the editable plan from disk, <c>POST /plan/edit</c>
/// persists field edits (rejecting invalid ones without writing), and <c>POST /plan/import</c> parses a
/// structured source deterministically and returns/applies the diff — all against a real HttpListener.</summary>
public sealed class ControlPlaneServerPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-plan-{Guid.NewGuid():N}");
    private readonly string _planPath;
    private readonly string _runDbPath;
    private readonly PlanConfig _plan;
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public ControlPlaneServerPlanTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _runDbPath = Path.Combine(stateDir, "run.db");
        _store = new SqliteRunStore(_runDbPath, NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-plan");

        _planPath = Path.Combine(_dir, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "plan-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "S1", Title = "Stage One", Sessions = 2, Kind = "deliver" }],
            Gates = [new GateConfig { Name = "build", Command = "dotnet build", Tier = "fast", TimeoutMinutes = 10 }],
        };
        File.WriteAllText(_planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _plan = PlanConfig.Load(_planPath);
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private (ControlPlaneServer server, int port) StartServer(string? currentStage = null)
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var state = new RunState { RunId = "run-plan", CurrentStage = currentStage };
        var server = new ControlPlaneServer(_plan, state, _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind");
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        // server.Port, not the probe port: Start() scans forward when a parallel fixture grabbed
        // the probed port first, and requests must follow the server, not the probe.
        return (server, server.Port);
    }

    [Fact]
    public async Task GetPlan_ReturnsStagesAndGates()
    {
        var (server, port) = StartServer();
        try
        {
            var body = await _http.GetStringAsync($"http://127.0.0.1:{port}/plan");
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("plan-test", doc.RootElement.GetProperty("name").GetString());
            var stages = doc.RootElement.GetProperty("stages");
            Assert.Equal("S1", stages[0].GetProperty("id").GetString());
            Assert.Equal("Stage One", stages[0].GetProperty("title").GetString());
            var gates = doc.RootElement.GetProperty("gates");
            Assert.Equal("build", gates[0].GetProperty("name").GetString());
            Assert.Equal("fast", gates[0].GetProperty("tier").GetString());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_PersistsFieldAndBumpsVersion()
    {
        var (server, port) = StartServer();
        try
        {
            var v0 = PlanConfig.Load(_planPath).PlanVersion;
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"stage","id":"S1","field":"title","value":"Renamed Stage"},{"target":"stage","id":"S1","field":"model","value":"opus-4-8"},{"target":"gate","id":"build","field":"tier","value":"truth"}]}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var reloaded = PlanConfig.Load(_planPath);
            Assert.Equal("Renamed Stage", reloaded.Stages[0].Title);
            Assert.Equal("opus-4-8", reloaded.Stages[0].Agent?.Model);
            Assert.Equal("truth", reloaded.Gates[0].Tier);
            Assert.True(reloaded.PlanVersion > v0);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_RejectsUnknownStageWithoutWriting()
    {
        var (server, port) = StartServer();
        try
        {
            var before = await File.ReadAllTextAsync(_planPath);
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"stage","id":"NOPE","field":"title","value":"x"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(before, await File.ReadAllTextAsync(_planPath)); // nothing written
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_AddsStageAndGate_WithDefaults()
    {
        var (server, port) = StartServer();
        try
        {
            var v0 = PlanConfig.Load(_planPath).PlanVersion;
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"stage","op":"add","id":"S2","value":"Second Stage"},{"target":"gate","op":"add","id":"lint","value":"dotnet format --verify-no-changes"}]}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var reloaded = PlanConfig.Load(_planPath);
            var s2 = Assert.Single(reloaded.Stages, s => s.Id == "S2");
            Assert.Equal("Second Stage", s2.Title);
            Assert.Equal("deliver", s2.Kind); // schema default
            var lint = Assert.Single(reloaded.Gates, g => g.Name == "lint");
            Assert.Equal("dotnet format --verify-no-changes", lint.Command);
            Assert.Equal("full", lint.Tier); // schema default
            Assert.True(reloaded.PlanVersion > v0);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_DeletesGate()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"gate","op":"delete","id":"build"}]}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            Assert.Empty(PlanConfig.Load(_planPath).Gates);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_DeleteDependedOnStage_RejectedWithoutWriting()
    {
        var (server, port) = StartServer();
        try
        {
            // Add S2 that depends on S1, then try to delete S1 — the dangling dependsOn must make the
            // whole delete fail the atomic re-validation, leaving the file untouched.
            var add = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"stage","op":"add","id":"S2","value":"Second"},{"target":"stage","id":"S2","field":"dependson","value":"S1"}]}""");
            Assert.Equal(HttpStatusCode.Accepted, add.StatusCode);

            var before = await File.ReadAllTextAsync(_planPath);
            var del = await PostAsync(port, "/plan/edit", """{"edits":[{"target":"stage","op":"delete","id":"S1"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);
            Assert.Equal(before, await File.ReadAllTextAsync(_planPath)); // nothing written
            Assert.Contains(PlanConfig.Load(_planPath).Stages, s => s.Id == "S1"); // still there
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_DeleteRunningStage_Refused()
    {
        var (server, port) = StartServer(currentStage: "S1");
        try
        {
            var before = await File.ReadAllTextAsync(_planPath);
            var resp = await PostAsync(port, "/plan/edit", """{"edits":[{"target":"stage","op":"delete","id":"S1"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Contains("running stage", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await File.ReadAllTextAsync(_planPath));
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanImport_PreviewsThenAppliesDiff()
    {
        const string markdown = """
            ### S1 — Stage One renamed by import
            - **S1.1** something
            ### S2 — Brand new stage
            - **S2.1** first
            - **S2.2** second
            """;
        var (server, port) = StartServer();
        try
        {
            // Preview: apply=false → diff only, no write.
            var before = PlanConfig.Load(_planPath).Stages.Count;
            var preview = await PostAsync(port, "/plan/import",
                JsonSerializer.Serialize(new { source = markdown, apply = false }));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using (var doc = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
                var added = doc.RootElement.GetProperty("diff").GetProperty("addedStages");
                Assert.Equal("S2", added[0].GetProperty("id").GetString());
            }
            Assert.Equal(before, PlanConfig.Load(_planPath).Stages.Count); // preview didn't write

            // Apply: persists S2 and the S1 title change, never clobbering.
            var applied = await PostAsync(port, "/plan/import",
                JsonSerializer.Serialize(new { source = markdown, apply = true }));
            Assert.Equal(HttpStatusCode.Accepted, applied.StatusCode);
            var reloaded = PlanConfig.Load(_planPath);
            Assert.Equal(2, reloaded.Stages.Count);
            Assert.Contains(reloaded.Stages, s => s.Id == "S2");
            Assert.Equal("Stage One renamed by import", reloaded.Stages[0].Title);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_EnqueuesLiveReload()
    {
        var (server, port) = StartServer();
        try
        {
            Assert.Empty(_inbox);
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"stage","id":"S1","field":"sessions","value":"3"}]}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // G3.2: a saved edit queues reload-plan so a live run swaps the plan at its next boundary.
            var cmd = Assert.Single(_inbox);
            Assert.Equal(ControlAction.ReloadPlan, cmd.Action);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_Rejected_DoesNotEnqueueReload()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"stage","id":"NOPE","field":"title","value":"x"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Empty(_inbox); // nothing saved → nothing to reload
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanImport_ApplyEnqueuesLiveReload_PreviewDoesNot()
    {
        const string markdown = """
            ### S8 — Imported stage
            - **S8.1** something
            ### S9 — Second imported stage
            - **S9.1** something else
            """;
        var (server, port) = StartServer();
        try
        {
            var preview = await PostAsync(port, "/plan/import",
                JsonSerializer.Serialize(new { source = markdown, apply = false }));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Empty(_inbox); // a preview writes nothing, so it must not reload anything

            var applied = await PostAsync(port, "/plan/import",
                JsonSerializer.Serialize(new { source = markdown, apply = true }));
            Assert.Equal(HttpStatusCode.Accepted, applied.StatusCode);
            var cmd = Assert.Single(_inbox);
            Assert.Equal(ControlAction.ReloadPlan, cmd.Action);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_LimitsFields_PersistClearAndValidate()
    {
        var (server, port) = StartServer();
        try
        {
            // G3.3: the live-tunable limits, all five fields in one atomic batch.
            var resp = await PostAsync(port, "/plan/edit",
                """
                {"edits":[
                  {"target":"limits","id":"","field":"maxsessions","value":"7"},
                  {"target":"limits","id":"","field":"maxruncostusd","value":"1.50"},
                  {"target":"limits","id":"","field":"maxruntokens","value":"250000"},
                  {"target":"limits","id":"","field":"stallminutes","value":"20"},
                  {"target":"limits","id":"","field":"sessiontimeoutminutes","value":"90"}]}
                """);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            var l = PlanConfig.Load(_planPath).Limits;
            Assert.Equal(7, l.MaxSessions);
            Assert.Equal(1.50m, l.MaxRunCostUsd);
            Assert.Equal(250_000, l.MaxRunTokens);
            Assert.Equal(20, l.StallMinutes);
            Assert.Equal(90, l.SessionTimeoutMinutes);

            // GET /plan surfaces the cap (the Face Settings reads it from here).
            using (var doc = JsonDocument.Parse(await _http.GetStringAsync($"http://127.0.0.1:{port}/plan")))
                Assert.Equal(7, doc.RootElement.GetProperty("limits").GetProperty("maxSessions").GetInt32());

            // An empty value clears a nullable cap.
            var clear = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"limits","id":"","field":"maxsessions","value":""}]}""");
            Assert.Equal(HttpStatusCode.Accepted, clear.StatusCode);
            Assert.Null(PlanConfig.Load(_planPath).Limits.MaxSessions);

            // Garbage is rejected without writing.
            var before = await File.ReadAllTextAsync(_planPath);
            var bad = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"limits","id":"","field":"maxsessions","value":"-3"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal(before, await File.ReadAllTextAsync(_planPath));
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_QaDial_RoundTripsPlanWideAndPerStage()
    {
        var (server, port) = StartServer();
        try
        {
            // P2: the plan-wide dial + its threshold, and a per-stage dial, in one atomic batch.
            var resp = await PostAsync(port, "/plan/edit",
                """
                {"edits":[
                  {"target":"qa","id":"","field":"mode","value":"phaseGate"},
                  {"target":"qa","id":"","field":"verifierthreshold","value":"90"},
                  {"target":"qa","id":"","field":"auditcoverspriorsessions","value":"false"},
                  {"target":"limits","id":"","field":"verifierthreshold","value":"85"},
                  {"target":"stage","id":"S1","field":"qamode","value":"off"}]}
                """);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            var saved = PlanConfig.Load(_planPath);
            Assert.Equal("phaseGate", saved.Pipeline?.Qa?.Mode);
            Assert.Equal(90, saved.Pipeline?.Qa?.VerifierThreshold);
            Assert.False(saved.Pipeline?.Qa?.AuditCoversPriorSessions);
            Assert.Equal(85, saved.Limits.VerifierThreshold);
            Assert.Equal("off", saved.Stages[0].Qa?.Mode);

            // The save queued a live reload — the dial steers the CURRENT run at its next boundary.
            Assert.Contains(_inbox, c => c.Action == ControlAction.ReloadPlan);

            // GET /plan surfaces both dials (the Face Settings + stage rows read from here).
            using (var doc = JsonDocument.Parse(await _http.GetStringAsync($"http://127.0.0.1:{port}/plan")))
            {
                Assert.Equal("phaseGate", doc.RootElement.GetProperty("qa").GetProperty("mode").GetString());
                Assert.Equal(90, doc.RootElement.GetProperty("qa").GetProperty("verifierThreshold").GetInt32());
                Assert.Equal("off", doc.RootElement.GetProperty("stages")[0].GetProperty("qaMode").GetString());
                Assert.Equal(85, doc.RootElement.GetProperty("limits").GetProperty("verifierThreshold").GetInt32());
            }

            // An empty mode clears the dial whole (and drops the empty pipeline block).
            var clear = await PostAsync(port, "/plan/edit",
                """
                {"edits":[
                  {"target":"qa","id":"","field":"mode","value":""},
                  {"target":"stage","id":"S1","field":"qamode","value":""}]}
                """);
            Assert.Equal(HttpStatusCode.Accepted, clear.StatusCode);
            var cleared = PlanConfig.Load(_planPath);
            Assert.Null(cleared.Pipeline);
            Assert.Null(cleared.Stages[0].Qa);

            // Garbage is rejected without writing: an unknown mode and an orphan threshold.
            var before = await File.ReadAllTextAsync(_planPath);
            var badMode = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"qa","id":"","field":"mode","value":"sometimes"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
            var orphanThreshold = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"qa","id":"","field":"verifierthreshold","value":"90"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, orphanThreshold.StatusCode);
            Assert.Equal(before, await File.ReadAllTextAsync(_planPath));
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanImport_RejectsFreeformProse()
    {
        var (server, port) = StartServer();
        try
        {
            var resp = await PostAsync(port, "/plan/import",
                JsonSerializer.Serialize(new { source = "please build me an api with auth", apply = false }));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally { server.Dispose(); }
    }

    private async Task<HttpResponseMessage> PostAsync(int port, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"http://127.0.0.1:{port}{path}", content);
    }
}
