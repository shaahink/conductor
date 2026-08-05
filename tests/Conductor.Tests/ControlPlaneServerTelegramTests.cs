using Conductor.Http;
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

/// <summary>M8.2 wire contract: <c>GET /telegram/status</c>, <c>POST /telegram/test</c>, and
/// <c>POST /telegram/token</c> — the guided-setup surface the Face's Telegram tab drives, so
/// Telegram can be configured and tested entirely from the app instead of hand-editing
/// plan.json/env vars. No live Telegram API call is exercised here (no real token in CI) — the
/// "not configured" / "no token" branches are asserted instead; a live-token round trip is a
/// manual, credential-gated dogfood step (see AGENTS.md).</summary>
public sealed class ControlPlaneServerTelegramTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-telegram-{Guid.NewGuid():N}");
    private readonly string _planPath;
    private readonly PlanConfig _plan;
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public ControlPlaneServerTelegramTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-telegram");

        _planPath = Path.Combine(_dir, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "telegram-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 }],
            Telegram = new TelegramConfig { AllowedChatIds = ["12345"], PollIntervalSeconds = 4 },
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

    private (ControlPlaneServer server, int port) StartServer(ITelegramService telegram)
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var server = new ControlPlaneServer(_plan, new RunState { RunId = "run-telegram" }, _store, _inbox,
            telegram, NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind");
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        // server.Port, not the probe port: Start() scans forward when a parallel fixture grabbed
        // the probed port first, and requests must follow the server, not the probe.
        return (server, server.Port);
    }

    /// <summary>SC1.3 sharpened this: with no live Telegram service the endpoint used to report the
    /// PLAN as unconfigured, which is a different (and false) statement — this fixture's plan has a
    /// telegram block. What is missing is a service to hand it to, and that is the one state a live
    /// save cannot fix, so it is now reported as exactly that.</summary>
    [Fact]
    public async Task GetTelegramStatus_SaysRestartRequired_WhenTheProcessHasNoTelegramService()
    {
        var (server, port) = StartServer(new NoOpTelegramService());
        try
        {
            var body = await _http.GetStringAsync($"http://127.0.0.1:{port}/telegram/status");
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("hasToken").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("started").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("willDeliver").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("restartRequired").GetBoolean());
            Assert.Contains("no Telegram service exists",
                doc.RootElement.GetProperty("willDeliverReason").GetString(), StringComparison.Ordinal);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetTelegramStatus_Configured_ButNoToken()
    {
        var telegram = new TelegramService(_plan, new RunState { RunId = "run-telegram" }, NullLogger<TelegramService>.Instance);
        var (server, port) = StartServer(telegram);
        try
        {
            var body = await _http.GetStringAsync($"http://127.0.0.1:{port}/telegram/status");
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("hasToken").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("started").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("allowedChatIds").GetArrayLength());
            Assert.Equal(4, doc.RootElement.GetProperty("pollIntervalSeconds").GetInt32());
        }
        finally { server.Dispose(); telegram.Dispose(); }
    }

    [Fact]
    public async Task PostTelegramTest_FailsCleanly_WhenNoToken()
    {
        var telegram = new TelegramService(_plan, new RunState { RunId = "run-telegram" }, NullLogger<TelegramService>.Instance);
        var (server, port) = StartServer(telegram);
        try
        {
            var resp = await PostAsync(port, "/telegram/test", "{}");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("token", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { server.Dispose(); telegram.Dispose(); }
    }

    [Fact]
    public async Task PostTelegramTest_RejectsCleanly_WhenNotConfiguredAtAll()
    {
        var (server, port) = StartServer(new NoOpTelegramService());
        try
        {
            var resp = await PostAsync(port, "/telegram/test", "{}");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTelegramToken_SavesToSecretsFile_NotThePlan()
    {
        var (server, port) = StartServer(new NoOpTelegramService());
        try
        {
            var before = await File.ReadAllTextAsync(_planPath);
            var resp = await PostAsync(port, "/telegram/token", """{"token":"123456:ABC-DEF-fake-token"}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

            // The plan file (git-tracked) is untouched — the token never round-trips through it.
            Assert.Equal(before, await File.ReadAllTextAsync(_planPath));

            // It landed in the local, gitignored secrets store instead.
            var saved = SecretsStore.TryReadTelegramToken(_plan.StateDir);
            Assert.Equal("123456:ABC-DEF-fake-token", saved);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostTelegramToken_RejectsEmptyToken()
    {
        var (server, port) = StartServer(new NoOpTelegramService());
        try
        {
            var resp = await PostAsync(port, "/telegram/token", """{"token":""}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_TelegramTarget_PersistsNonSecretSettings()
    {
        var (server, port) = StartServer(new NoOpTelegramService());
        try
        {
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"telegram","field":"allowedChatIds","value":"111,222,333"},{"target":"telegram","field":"pollIntervalSeconds","value":"10"},{"target":"telegram","field":"enableTwoWay","value":"true"}]}""");
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var reloaded = PlanConfig.Load(_planPath);
            Assert.Equal(["111", "222", "333"], reloaded.Telegram!.AllowedChatIds);
            Assert.Equal(10, reloaded.Telegram.PollIntervalSeconds);
            Assert.True(reloaded.Telegram.EnableTwoWay);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PostPlanEdit_TelegramTarget_RejectsBadPollInterval()
    {
        var (server, port) = StartServer(new NoOpTelegramService());
        try
        {
            var resp = await PostAsync(port, "/plan/edit",
                """{"edits":[{"target":"telegram","field":"pollIntervalSeconds","value":"not-a-number"}]}""");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { server.Dispose(); }
    }

    private async Task<HttpResponseMessage> PostAsync(int port, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync($"http://127.0.0.1:{port}{path}", content);
    }
}
