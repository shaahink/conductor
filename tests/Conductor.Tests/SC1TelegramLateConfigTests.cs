using Conductor.Http;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Hosting;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SC1.3 regression. SC1.1 made the service start; SC1.2 made the surfaces honest about whether it
/// would deliver. What was left is the case the owner actually hits: the token and the telegram block
/// arrive AFTER the engine started. Both were resolved once, in the constructor, into readonly fields
/// — and when the plan had no telegram block at all the composition root pinned a
/// <c>NoOpTelegramService</c> for the life of the process, so a block added later reached a service
/// that could never exist. Every surface still said "saved".
///
/// These tests hold the live service to the only claim that matters: after the late configuration
/// lands, a push REACHES THE WIRE in the same process, with no restart. A boolean flipping true is
/// not evidence — the flags were all true on the dead feature too — so every case here ends at the
/// stub Bot API's received-message list.
/// </summary>
public sealed class SC1TelegramLateConfigTests : IDisposable
{
    private const string ChatId = "727272";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sc13-{Guid.NewGuid():N}");
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly List<IDisposable> _disposables = new();
    private readonly List<TelegramService> _services = new();

    public SC1TelegramLateConfigTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(_dir, ".conductor", "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-sc13");
    }

    public void Dispose()
    {
        foreach (var s in _services)
        {
            try { s.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(15)); } catch (Exception) { }
        }
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
        _http.Dispose();
        _store.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private PlanConfig Plan(bool telegramBlock = true, bool chatIds = true, string? apiBaseUrl = null,
        int pollSeconds = 1)
    {
        var plan = new PlanConfig
        {
            Name = "sc13-plan",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = { "{prompt}" } },
            Stages = { new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 } },
        };
        if (telegramBlock)
        {
            plan.Telegram = new TelegramConfig
            {
                PollIntervalSeconds = pollSeconds,
                EnableTwoWay = true,
                ApiBaseUrl = apiBaseUrl,
            };
            if (chatIds) plan.Telegram.AllowedChatIds.Add(ChatId);
        }
        return plan;
    }

    /// <summary>The token deliberately does NOT exist when the service is constructed — that is the
    /// state SC1.3 is about. <see cref="SaveTokenLate"/> puts it there afterwards, exactly as the
    /// Face's token endpoint does. (CONDUCTOR_TELEGRAM_TOKEN is cleared process-wide by
    /// TestEnvironmentIsolation, so the developer's real token cannot leak into these runs.)</summary>
    private void ClearToken(PlanConfig plan) => SecretsStore.WriteTelegramToken(plan.StateDir, "");

    private void SaveTokenLate(PlanConfig plan) => SecretsStore.WriteTelegramToken(plan.StateDir, "sc13-late-token");

    private TelegramService Service(PlanConfig plan, CapturingLogger? log = null)
    {
        var svc = new TelegramService(plan, new RunState { RunId = "run-sc13" }, log ?? new CapturingLogger());
        _services.Add(svc);
        _disposables.Add(svc);
        return svc;
    }

    private int StartServer(PlanConfig plan, ITelegramService telegram)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var probed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new ControlPlaneServer(plan, new RunState { RunId = "run-sc13" }, _store, _inbox,
            telegram, NullLogger.Instance, probed);
        Assert.True(server.Start(), "control plane failed to bind");
        _disposables.Add(server);
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        return server.Port;
    }

    private async Task<JsonElement> GetStatusAsync(int port)
    {
        var body = await _http.GetStringAsync($"http://127.0.0.1:{port}/telegram/status");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Code, JsonElement Body)> PostTokenAsync(int port, string token)
    {
        using var content = new StringContent($"{{\"token\":\"{token}\"}}", Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"http://127.0.0.1:{port}/telegram/token", content);
        var body = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    // ── the late token ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Face's own flow, end to end: an engine is running with a telegram block and no token, the
    /// owner pastes one in, and the CURRENT run starts delivering. Before this the token was read once
    /// in the constructor, so this endpoint could only ever write a file and tell the operator to
    /// restart — and it did not even do that honestly, it reported a plain success.
    /// </summary>
    [Fact]
    public async Task PostTelegramToken_StartsTheRunningService_AndARealPushReachesTheWire()
    {
        using var bot = new StubBotApi();
        var plan = Plan(apiBaseUrl: bot.Root);
        ClearToken(plan);
        var svc = Service(plan);
        var port = StartServer(plan, svc);

        // The run starts: nothing to start with, and it says so rather than pretending.
        await svc.StartAsync(CancellationToken.None);
        var before = await GetStatusAsync(port);
        Assert.False(before.GetProperty("started").GetBoolean());
        Assert.False(before.GetProperty("willDeliver").GetBoolean());
        Assert.False(before.GetProperty("restartRequired").GetBoolean());

        var (code, body) = await PostTokenAsync(port, "sc13-late-token");

        Assert.Equal(HttpStatusCode.Accepted, code);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("no restart needed", Str(body, "message"), StringComparison.Ordinal);

        var after = await GetStatusAsync(port);
        Assert.True(after.GetProperty("started").GetBoolean());
        Assert.True(after.GetProperty("willDeliver").GetBoolean());

        // The claim, not the flag: a push made after the late token lands on the wire, in this same
        // process, from the same service object that was constructed without a token.
        await svc.PushAsync("LATE-TOKEN-PUSH");
        Assert.True(await bot.WaitForAsync("LATE-TOKEN-PUSH", TimeSpan.FromSeconds(15)),
            "the push never reached the wire after the token was saved — the late token did not take effect. Sent: " + bot.Describe());
    }

    /// <summary>A token that arrives when there is still nobody to send to must not read as success.
    /// The reply says the token is saved and names what is still missing, in doctor's words.</summary>
    [Fact]
    public async Task PostTelegramToken_SaysWhatIsStillMissing_WhenTheTokenAloneIsNotEnough()
    {
        using var bot = new StubBotApi();
        var plan = Plan(chatIds: false, apiBaseUrl: bot.Root);
        ClearToken(plan);
        var svc = Service(plan);
        var port = StartServer(plan, svc);
        await svc.StartAsync(CancellationToken.None);

        var (code, body) = await PostTokenAsync(port, "sc13-late-token");

        Assert.Equal(HttpStatusCode.Accepted, code);
        Assert.True(body.GetProperty("ok").GetBoolean());          // the save itself worked
        Assert.False(body.GetProperty("willDeliver").GetBoolean()); // and it still cannot notify anybody
        Assert.Contains("allowedChatIds", Str(body, "message"), StringComparison.Ordinal);
    }

    // ── the late telegram block ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The harder half, and the one the old composition root made structurally impossible: a run
    /// STARTED WITHOUT A TELEGRAM BLOCK is configured mid-run (plan edit → reload → plan swap) and
    /// begins delivering. The plan swap is the same call the run loop makes at its session boundary.
    /// </summary>
    [Fact]
    public async Task ATelegramBlockAddedMidRun_ReachesTheLiveService_AndItDelivers()
    {
        using var bot = new StubBotApi();
        var blockless = Plan(telegramBlock: false);
        ClearToken(blockless);
        var log = new CapturingLogger();
        var svc = Service(blockless, log);
        var port = StartServer(blockless, svc);

        await svc.StartAsync(CancellationToken.None);
        Assert.False((await GetStatusAsync(port)).GetProperty("configured").GetBoolean());

        // The owner configures Telegram on the running engine: a block in the plan file, a token in
        // the secrets store. The run loop then swaps the reloaded plan into every collaborator.
        var configured = Plan(apiBaseUrl: bot.Root);
        SaveTokenLate(configured);
        await svc.ApplyPlanAsync(configured);

        var status = await GetStatusAsync(port);
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.True(status.GetProperty("started").GetBoolean());
        Assert.True(status.GetProperty("willDeliver").GetBoolean());
        Assert.False(status.GetProperty("restartRequired").GetBoolean());

        await svc.PushAsync("LATE-BLOCK-PUSH");
        Assert.True(await bot.WaitForAsync("LATE-BLOCK-PUSH", TimeSpan.FromSeconds(15)),
            "the push never reached the wire after the telegram block arrived. Sent: " + bot.Describe());

        Assert.Contains(log.Lines, l => l.Contains("Telegram bot started", StringComparison.Ordinal));
    }

    /// <summary>The reverse, which matters just as much: a block removed from the plan stops the live
    /// service instead of leaving it polling and pushing on settings the plan no longer has.</summary>
    [Fact]
    public async Task ATelegramBlockRemovedMidRun_StopsTheLiveService_AndStatusSaysSo()
    {
        using var bot = new StubBotApi();
        var configured = Plan(apiBaseUrl: bot.Root);
        SaveTokenLate(configured);
        var svc = Service(configured);
        var port = StartServer(configured, svc);
        await svc.StartAsync(CancellationToken.None);
        Assert.True((await GetStatusAsync(port)).GetProperty("willDeliver").GetBoolean());

        await svc.ApplyPlanAsync(Plan(telegramBlock: false));

        var status = await GetStatusAsync(port);
        Assert.False(status.GetProperty("started").GetBoolean());
        Assert.False(status.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("not configured", Str(status, "willDeliverReason"), StringComparison.Ordinal);
    }

    /// <summary>A changed chat id must reach the live service too — the poll and send loops were
    /// built from the block they started with, so "reload" has to mean restart, not just reassign.</summary>
    [Fact]
    public async Task ChangedChatIds_ReachTheLiveService_AndThePushGoesToTheNewChat()
    {
        using var bot = new StubBotApi();
        var plan = Plan(apiBaseUrl: bot.Root);
        SaveTokenLate(plan);
        var svc = Service(plan);
        var port = StartServer(plan, svc);
        await svc.StartAsync(CancellationToken.None);

        var moved = Plan(apiBaseUrl: bot.Root);
        moved.Telegram!.AllowedChatIds.Clear();
        moved.Telegram.AllowedChatIds.Add("909090");
        var outcome = await svc.ReloadAsync(moved);

        Assert.True(outcome.Changed);
        Assert.True(outcome.WillDeliver);
        var live = (await GetStatusAsync(port)).GetProperty("allowedChatIds")
            .EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        Assert.Equal(new List<string> { "909090" }, live);

        await svc.PushAsync("MOVED-CHAT-PUSH");
        Assert.True(await bot.WaitForAsync("MOVED-CHAT-PUSH", TimeSpan.FromSeconds(15)),
            "the push never reached the wire after the chat id changed. Sent: " + bot.Describe());
        Assert.Equal("909090", bot.LastChatId);
    }

    // ── when a restart really IS required, say so ───────────────────────────────────────────────

    /// <summary>The one state a live reload cannot fix: no Telegram service exists in this process at
    /// all, so nothing saved here can reach the current run. Both surfaces say it rather than
    /// reporting a save that will silently do nothing.</summary>
    [Fact]
    public async Task WithNoLiveService_BothSurfacesSayRestartRequired_InsteadOfClaimingItWorked()
    {
        var plan = Plan();
        var port = StartServer(plan, new NoOpTelegramService());

        var status = await GetStatusAsync(port);
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.True(status.GetProperty("restartRequired").GetBoolean());
        Assert.False(status.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("no Telegram service exists", Str(status, "willDeliverReason"), StringComparison.Ordinal);

        var (code, body) = await PostTokenAsync(port, "sc13-late-token");
        Assert.Equal(HttpStatusCode.Accepted, code);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.False(body.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("next `conductor run`", Str(body, "message"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The wiring half of the checkpoint, checked where it broke: the composition root. A plan with no
    /// telegram block used to register a NoOpTelegramService, which is a permanent answer to a
    /// question the operator can change at any moment — and the reason "add a telegram block mid-run"
    /// could not have worked no matter how good the reload was.
    /// </summary>
    [Fact]
    public void TheRunHost_RegistersARealTelegramService_EvenWhenThePlanHasNoTelegramBlock()
    {
        var plan = Plan(telegramBlock: false);
        plan.Repo = _dir;
        using var host = ConductorHost.Build(plan, new RunState { RunId = "run-sc13" }, new PlainSink(),
            new RunOptions(DryRun: true, Once: true, MaxSessions: 1), consoleSink: false);

        var telegram = host.Services.GetRequiredService<ITelegramService>();
        Assert.IsType<TelegramService>(telegram);

        // And it is on the hosted-service list SC1.1 made the run path start, so the late block lands
        // in a service that is already being started and stopped by the run.
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is TelegramService);
    }

    // ── stubs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stands in for api.telegram.org and records what actually left the process.</summary>
    private sealed class StubBotApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly Lock _gate = new();
        private readonly List<string> _sent = new();

        public string Root { get; }
        public string? LastChatId { get; private set; }

        public StubBotApi()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Root = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}";
                probe.Stop();
            }
            _listener.Prefixes.Add(Root + "/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string Describe()
        {
            lock (_gate) return _sent.Count == 0 ? "(nothing)" : string.Join(" || ", _sent);
        }

        public async Task<bool> WaitForAsync(string fragment, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_sent.Exists(m => m.Contains(fragment, StringComparison.Ordinal))) return true;
                }
                await Task.Delay(25).ConfigureAwait(false);
            }
            return false;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            // The path carries the bot token; match on the method name only and never record the URL.
            var method = (ctx.Request.Url?.AbsolutePath ?? "").Split('/')[^1];
            var body = """{"ok":true,"result":[]}""";

            try
            {
                if (string.Equals(method, "getMe", StringComparison.Ordinal))
                {
                    body = """{"ok":true,"result":{"id":1,"username":"sc13_stub_bot"}}""";
                }
                else if (string.Equals(method, "sendMessage", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(payload);
                    var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    var chat = doc.RootElement.TryGetProperty("chat_id", out var c) ? c.GetString() : null;
                    lock (_gate) { _sent.Add(text); LastChatId = chat; }
                    body = """{"ok":true,"result":{"message_id":1}}""";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }

    /// <summary>Keeps every log line so "did it say anything at all?" stays a testable question.</summary>
    private sealed class CapturingLogger : ILogger<TelegramService>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = new();

        public List<string> Lines { get { lock (_gate) return new List<string>(_lines); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _lines.Add($"{logLevel}|{formatter(state, exception)}");
        }
    }
}
