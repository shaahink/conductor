using Conductor.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Commands;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>P5: the session-token rollover, surfaced. The set-rollover verb parses on every
/// ingress, the dispatcher writes ONLY run state (never the plan), and the limits edit target
/// round-trips maxSessionTokens/softBreakRatio over the wire with OFF (absent) as the default.</summary>
public sealed class P5RolloverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-rollover-{Guid.NewGuid():N}");
    private readonly HttpClient _http = new();
    private SqliteRunStore? _store;

    public void Dispose()
    {
        _http.Dispose();
        _store?.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { /* best effort */ }
    }

    // ── the verb, on the wire shape every ingress shares ──

    [Fact]
    public void ControlFileParse_MapsSetRollover_WithItsValue()
    {
        var cmd = ControlFile.Parse("""{"command":"set-rollover","value":"250000"}""");
        Assert.Equal(ControlAction.SetRollover, cmd.Action);
        Assert.Equal("250000", cmd.Value);
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("clear", true, null)]
    [InlineData("CLEAR", true, null)]
    [InlineData("off", true, 0L)]
    [InlineData("0", true, 0L)]
    [InlineData("250000", true, 250000L)]
    [InlineData("abc", false, null)]
    [InlineData("-5", false, null)]
    public void ParseRolloverValue_CoversTokensOffAndClear(string? value, bool ok, long? cap)
    {
        var (gotOk, gotCap) = ControlDispatcher.ParseRolloverValue(value);
        Assert.Equal(ok, gotOk);
        Assert.Equal(cap, gotCap);
    }

    // ── the dispatcher: run state only, the plan file never ──

    [Fact]
    public async Task SetRollover_WritesRunStateOnly_AndNeverTouchesThePlan()
    {
        var plan = new PlanConfig { Name = "p5", Repo = ".", Tracker = "TRACKER.md" };
        var state = new RunState { RunId = "r" };
        var saved = 0;
        var dispatcher = new ControlDispatcher(plan, state, new PlainSink(), new CollectingEventSink(),
            _ => { }, () => saved++, () => { }, (_, _) => { }, (_, _) => Task.CompletedTask);

        // ON at a cap — immediately, even mid-session (nothing to defer).
        await dispatcher.DispatchAsync(
            new ControlCommand(ControlAction.SetRollover, false, null, null, false, "180000"), inSession: true, CancellationToken.None);
        Assert.Equal(180000, state.MaxSessionTokensThisRun);

        // OFF this run (forced off even if the plan sets a cap).
        await dispatcher.DispatchAsync(
            new ControlCommand(ControlAction.SetRollover, false, null, null, false, "off"), inSession: false, CancellationToken.None);
        Assert.Equal(0, state.MaxSessionTokensThisRun);

        // Clear — back to whatever the plan says.
        await dispatcher.DispatchAsync(
            new ControlCommand(ControlAction.SetRollover, false, null, null, false, "clear"), inSession: false, CancellationToken.None);
        Assert.Null(state.MaxSessionTokensThisRun);

        // An unparseable value is refused and changes nothing.
        await dispatcher.DispatchAsync(
            new ControlCommand(ControlAction.SetRollover, false, null, null, false, "lots"), inSession: false, CancellationToken.None);
        Assert.Null(state.MaxSessionTokensThisRun);

        Assert.Equal(3, saved); // each applied change persists run state…
        Assert.Null(plan.Limits.MaxSessionTokens); // …and the plan's own limit is never written
    }

    // ── the limits edit target + GET /plan, over a real HttpListener ──

    [Fact]
    public async Task PlanEdit_MaxSessionTokensAndSoftBreakRatio_RoundTrip_AndOffStaysTheDefault()
    {
        var (server, port, planPath) = StartServer();
        try
        {
            // OFF by default: a fresh plan serves no maxSessionTokens.
            using (var before = JsonDocument.Parse(await _http.GetStringAsync($"http://127.0.0.1:{port}/plan")))
                Assert.False(before.RootElement.GetProperty("limits").TryGetProperty("maxSessionTokens", out _));

            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"limits","field":"maxsessiontokens","value":"250000"},{"target":"limits","field":"softbreakratio","value":"0.75"}]}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            using (var after = JsonDocument.Parse(await _http.GetStringAsync($"http://127.0.0.1:{port}/plan")))
            {
                var limits = after.RootElement.GetProperty("limits");
                Assert.Equal(250000, limits.GetProperty("maxSessionTokens").GetInt64());
                Assert.Equal(0.75, limits.GetProperty("softBreakRatio").GetDouble(), precision: 3);
            }

            // Clearing turns rollover OFF again (null, honestly absent — not zero).
            await PostAsync(port, "/plan/edit", """{"edits":[{"target":"limits","field":"maxsessiontokens","value":""}]}""");
            var saved = PlanConfig.Load(planPath);
            Assert.Null(saved.Limits.MaxSessionTokens);
        }
        finally { server.Dispose(); }
    }

    [Theory]
    [InlineData("""{"edits":[{"target":"limits","field":"maxsessiontokens","value":"-1"}]}""", "maxSessionTokens must be")]
    [InlineData("""{"edits":[{"target":"limits","field":"softbreakratio","value":"1.5"}]}""", "softBreakRatio must be")]
    [InlineData("""{"edits":[{"target":"limits","field":"softbreakratio","value":"0"}]}""", "softBreakRatio must be")]
    public async Task PlanEdit_RejectsInvalidRolloverValues(string body, string expectedError)
    {
        var (server, port, _) = StartServer();
        try
        {
            var resp = await PostAsync(port, "/plan/edit", body);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Contains(expectedError, doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    private (ControlPlaneServer server, int port, string planPath) StartServer()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-rollover");

        var planPath = Path.Combine(_dir, "p5.plan.json");
        var seed = new PlanConfig
        {
            Name = "rollover-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "P5", Title = "Rollover", Sessions = 1 }],
        };
        File.WriteAllText(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts));
        var plan = PlanConfig.Load(planPath);

        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var server = new ControlPlaneServer(plan, new RunState { RunId = "run-rollover" }, _store,
            new ConcurrentQueue<ControlCommand>(), new NoOpRunNotifier(), NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind");
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        // server.Port, not the probe port: Start() scans forward when a parallel test fixture
        // grabbed the probed port first, and requests must follow the server, not the probe.
        return (server, server.Port, planPath);
    }

    private async Task<HttpResponseMessage> PostAsync(int port, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"http://127.0.0.1:{port}{path}", content);
    }
}
