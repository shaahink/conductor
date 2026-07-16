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

/// <summary>G1.1 wire contract: <c>POST /plan/import</c> routes freeform prose through the plan's
/// advisor model (a local fake here — zero spend), returns the diff for preview, applies exactly the
/// previewed parse (cached — the model is never consulted twice for one prompt), and rejects an
/// import that would make the plan invalid without writing anything.</summary>
public sealed class ControlPlaneServerPromptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-prompt-{Guid.NewGuid():N}");
    private readonly string _planPath;
    private readonly string _replyPath;
    private readonly PlanConfig _plan;
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public ControlPlaneServerPromptTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-prompt");

        _replyPath = Path.Combine(_dir, "advisor-reply.json");
        _planPath = Path.Combine(_dir, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "prompt-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "S1", Title = "Stage One", Sessions = 2, Kind = "deliver" }],
            Gates = [new GateConfig { Name = "build", Command = "dotnet build", Tier = "fast", TimeoutMinutes = 10 }],
            // The "advisor model" is a local file-cat — the real prose→advisor→diff path, no spend.
            Advisor = OperatingSystem.IsWindows()
                ? new AdvisorConfig { Enabled = true, Command = "cmd", Args = ["/c", "type", "advisor-reply.json"], Output = "text", TimeoutMinutes = 1 }
                : new AdvisorConfig { Enabled = true, Command = "/bin/sh", Args = ["-c", "cat advisor-reply.json"], Output = "text", TimeoutMinutes = 1 },
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

    private (ControlPlaneServer server, int port) StartServer()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var state = new RunState { RunId = "run-prompt" };
        var server = new ControlPlaneServer(_plan, state, _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind");
        return (server, port);
    }

    [Fact]
    public async Task FreeformPrompt_PreviewsDiff_ThenAppliesTheCachedParse()
    {
        await File.WriteAllTextAsync(_replyPath,
            """{"stages":[],"gates":[{"name":"lint","command":"dotnet format --verify-no-changes","tier":"fast","timeoutMinutes":5}]}""");
        var (server, port) = StartServer();
        try
        {
            // The design gate, verbatim: a freeform prompt returns a non-empty diff with the gate.
            var preview = await PostAsync(port, "/plan/import",
                """{"source":"add a lint gate that runs dotnet format","apply":false}""");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using (var doc = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
                var gate = Assert.Single(doc.RootElement.GetProperty("diff").GetProperty("addedGates").EnumerateArray());
                Assert.Equal("lint", gate.GetProperty("name").GetString());
                // The result names what interpreted the prose (here the fake advisor command).
                Assert.Equal(_plan.Advisor!.Command, doc.RootElement.GetProperty("interpreter").GetString());
            }
            var v0 = PlanConfig.Load(_planPath).PlanVersion;
            Assert.DoesNotContain(PlanConfig.Load(_planPath).Gates, g => g.Name == "lint"); // preview didn't write

            // Deleting the fake advisor's reply proves apply reuses the previewed parse —
            // the model is not consulted (or billed) a second time for the same prompt.
            File.Delete(_replyPath);

            var applied = await PostAsync(port, "/plan/import",
                """{"source":"add a lint gate that runs dotnet format","apply":true}""");
            Assert.Equal(HttpStatusCode.Accepted, applied.StatusCode);
            using (var doc = JsonDocument.Parse(await applied.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("applied").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("planVersion").GetInt32() > v0);
            }
            var reloaded = PlanConfig.Load(_planPath);
            var lint = Assert.Single(reloaded.Gates, g => g.Name == "lint");
            Assert.Equal("dotnet format --verify-no-changes", lint.Command);
            Assert.True(reloaded.PlanVersion > v0);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task FreeformPrompt_InvalidResult_RejectedWithoutWriting()
    {
        // The advisor answers with a stage depending on a stage that doesn't exist — the diff
        // previews fine, but applying it must fail the atomic re-validation and write nothing.
        await File.WriteAllTextAsync(_replyPath,
            """{"stages":[{"id":"S9","title":"Bad stage","sessions":1,"dependsOn":["NOPE"]}],"gates":[]}""");
        var (server, port) = StartServer();
        try
        {
            var preview = await PostAsync(port, "/plan/import", """{"source":"split delivery into a new stage","apply":false}""");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

            var before = await File.ReadAllTextAsync(_planPath);
            var apply = await PostAsync(port, "/plan/import", """{"source":"split delivery into a new stage","apply":true}""");
            Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);
            using var doc = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
            Assert.Contains("invalid", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await File.ReadAllTextAsync(_planPath)); // nothing written
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task FreeformPrompt_AdvisorReturnsNoJson_Rejected()
    {
        await File.WriteAllTextAsync(_replyPath, "I cannot help with that.");
        var (server, port) = StartServer();
        try
        {
            var resp = await PostAsync(port, "/plan/import", """{"source":"do something vague","apply":false}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Contains("could not derive", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    private async Task<HttpResponseMessage> PostAsync(int port, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"http://127.0.0.1:{port}{path}", content);
    }
}
