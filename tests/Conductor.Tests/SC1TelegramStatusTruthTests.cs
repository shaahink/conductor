using Conductor.Http;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SC1.2 regression. SC1.1 fixed a feature that was dead for its entire life; what let it stay dead
/// was that every surface reporting on it reported a PREFIX of the truth. <c>configured</c> was true,
/// <c>hasToken</c> was true, the Face's Test button was green, and not one of them was a claim about
/// delivery. So these tests are about the claims, and they are made against the real wire:
/// a live <see cref="TelegramService"/> plus a real <see cref="ControlPlaneServer"/> over HTTP, with a
/// stub standing in for api.telegram.org. Nothing is mocked, and nothing is asserted from source.
///
/// The load-bearing one is <see cref="PostTelegramTest_TravelsTheRealSendQueue_NotAParallelPath"/>:
/// "routes through the real send queue" is proved by holding the queue's single reader busy and
/// showing the test message cannot overtake it — the property a bypassing implementation, which is
/// exactly what shipped before, cannot have.
/// </summary>
public sealed class SC1TelegramStatusTruthTests : IDisposable
{
    private const string ChatId = "515151";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sc12-{Guid.NewGuid():N}");
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly List<IDisposable> _disposables = new();
    private readonly List<TelegramService> _services = new();

    public SC1TelegramStatusTruthTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(_dir, ".conductor", "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-sc12");
    }

    public void Dispose()
    {
        // Stop before dispose: a service disposed while its poll loop is live keeps hammering a
        // listener that has already gone away, and leaves a faulted task behind for the next test.
        foreach (var s in _services)
        {
            try { s.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(15)); } catch (Exception) { }
        }
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private PlanConfig Plan(bool telegramBlock = true, bool token = true, bool chatIds = true,
        string? apiBaseUrl = null)
    {
        var plan = new PlanConfig
        {
            Name = "sc12-plan",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = { "{prompt}" } },
            Stages = { new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 } },
        };
        if (telegramBlock)
        {
            plan.Telegram = new TelegramConfig { PollIntervalSeconds = 1, EnableTwoWay = true, ApiBaseUrl = apiBaseUrl };
            if (chatIds) plan.Telegram.AllowedChatIds.Add(ChatId);
        }
        // The ambient CONDUCTOR_TELEGRAM_TOKEN is cleared for the whole test process
        // (TestEnvironmentIsolation), so a token only exists here if this fixture writes one.
        if (token) SecretsStore.WriteTelegramToken(plan.StateDir, "sc12-test-token");
        else SecretsStore.WriteTelegramToken(plan.StateDir, "");
        return plan;
    }

    private TelegramService Service(PlanConfig plan, CapturingLogger? log = null)
    {
        var svc = new TelegramService(plan, new RunState { RunId = "run-sc12" }, log ?? new CapturingLogger());
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

        var server = new ControlPlaneServer(plan, new RunState { RunId = "run-sc12" }, _store, _inbox,
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

    private async Task<(HttpStatusCode Code, JsonElement Body)> PostTestAsync(int port)
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"http://127.0.0.1:{port}/telegram/test", content);
        var body = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    // ── willDeliver: the verdict none of the old booleans made ───────────────────────────────────

    /// <summary>Every half missing in turn. Each of these states used to render as some flavour of
    /// "configured" with nothing saying delivery was impossible.</summary>
    [Fact]
    public async Task TelegramStatus_WillDeliverIsFalse_AndNamesTheMissingHalf_ForEveryMissingHalf()
    {
        // 1. no telegram block at all — the NoOpTelegramService path
        var port = StartServer(Plan(telegramBlock: false, token: false), new NoOpTelegramService());
        var status = await GetStatusAsync(port);
        Assert.False(status.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("not configured", Str(status, "willDeliverReason"), StringComparison.Ordinal);

        // 2. block, but no bot token
        var noToken = Plan(token: false);
        port = StartServer(noToken, Service(noToken));
        status = await GetStatusAsync(port);
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("no bot token", Str(status, "willDeliverReason"), StringComparison.Ordinal);

        // 3. token, but nobody to send to — "push-only to nobody"
        var noChats = Plan(chatIds: false);
        port = StartServer(noChats, Service(noChats));
        status = await GetStatusAsync(port);
        Assert.True(status.GetProperty("hasToken").GetBoolean());
        Assert.False(status.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("allowedChatIds", Str(status, "willDeliverReason"), StringComparison.Ordinal);

        // 4. everything configured — but the service was never started. THIS is the state the engine
        //    was actually in on every run before SC1.1, and the one the old status could not express:
        //    configured true, hasToken true, and not a single push would ever leave the process.
        var ready = Plan();
        port = StartServer(ready, Service(ready));
        status = await GetStatusAsync(port);
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.True(status.GetProperty("hasToken").GetBoolean());
        Assert.Single(status.GetProperty("allowedChatIds").EnumerateArray());
        Assert.False(status.GetProperty("started").GetBoolean());
        Assert.False(status.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("not running in this process", Str(status, "willDeliverReason"), StringComparison.Ordinal);
    }

    /// <summary>The positive case, and its reversal: willDeliver tracks the live service rather than
    /// a snapshot taken at startup, so it goes false again the moment the service stops.</summary>
    [Fact]
    public async Task TelegramStatus_WillDeliverIsTrue_OnlyWhileTheServiceIsActuallyRunning()
    {
        using var bot = new StubBotApi();
        var plan = Plan(apiBaseUrl: bot.Root);
        var svc = Service(plan);
        var port = StartServer(plan, svc);

        await svc.StartAsync(CancellationToken.None);
        var status = await GetStatusAsync(port);
        Assert.True(status.GetProperty("started").GetBoolean());
        Assert.True(status.GetProperty("willDeliver").GetBoolean());
        Assert.Null(Str(status, "willDeliverReason"));

        await svc.StopAsync(CancellationToken.None);
        status = await GetStatusAsync(port);
        Assert.False(status.GetProperty("willDeliver").GetBoolean());
        Assert.Contains("not running in this process", Str(status, "willDeliverReason"), StringComparison.Ordinal);
    }

    /// <summary>Anti-drift: doctor and the live status endpoint must say the same sentence about the
    /// same missing half. They used to hold two independent copies of this judgement, which is how
    /// "configured" came to mean three different things in three places.</summary>
    [Fact]
    public async Task DoctorAndTheStatusEndpoint_GiveTheSameSentence_ForTheSameMissingHalf()
    {
        var noToken = Plan(token: false);
        var port = StartServer(noToken, Service(noToken));
        var status = await GetStatusAsync(port);

        var doctor = DoctorCommand.CheckTelegram(noToken);
        Assert.Equal("warn", doctor.State);
        Assert.Equal(doctor.Message, Str(status, "willDeliverReason"));
    }

    // ── the test button: same path, or say so ────────────────────────────────────────────────────

    /// <summary>
    /// The checkpoint's hard half. "Routes through the real send queue" is proved by a property only
    /// queue routing can have: the queue has ONE reader, so while an earlier push is still in flight
    /// the test message cannot reach the wire at all. The old implementation called the Bot API
    /// directly and would have overtaken it instantly — which is precisely why its green tick meant
    /// nothing about whether a run could notify anybody.
    /// </summary>
    [Fact]
    public async Task PostTelegramTest_TravelsTheRealSendQueue_NotAParallelPath()
    {
        using var bot = new StubBotApi();
        var plan = Plan(apiBaseUrl: bot.Root);
        var svc = Service(plan);
        var port = StartServer(plan, svc);
        await svc.StartAsync(CancellationToken.None);

        // Occupy the queue's single reader with an ordinary push the stub will not answer yet.
        bot.HoldSendMessages();
        await svc.PushAsync("QUEUED-FIRST");
        Assert.True(await bot.WaitForSendMessagesAsync(1, TimeSpan.FromSeconds(10)),
            "the ordinary push never reached the wire — the send loop is not running");

        var testCall = PostTestAsync(port);

        // Give a bypassing implementation every chance to show itself: it would POST sendMessage
        // immediately, without waiting for the blocked one ahead of it.
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        Assert.Equal(1, bot.SendMessageCount);
        Assert.False(testCall.IsCompleted, "the test call answered while the send queue was blocked — it did not use the queue");

        bot.ReleaseSendMessages();
        var (code, body) = await testCall;

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("viaQueue").GetBoolean());
        Assert.Contains("live send queue", Str(body, "detail"), StringComparison.Ordinal);

        // And it really did land behind the earlier push, in queue order. Since FU-OWNER-11 every
        // outbound message is stamped at SendAsync, so an ordinary push reaches the wire as the
        // identity line and then its body. Read the stamp off the live service rather than pinning
        // its shape a second time here — FuOwner11PushIdentityTests owns that — but keep the exact
        // equality, which now also proves a plain push is attributable.
        var sent = bot.Sent;
        Assert.Equal(svc.IdentityLine + "\nQUEUED-FIRST", sent[0]);
        Assert.Contains("live push queue", sent[1], StringComparison.Ordinal);
    }

    /// <summary>When the queue genuinely is not running the test still sends — but the reply, and the
    /// message that lands on the phone, both say it proved nothing about delivery.</summary>
    [Fact]
    public async Task PostTelegramTest_SaysLoudlyWhenItBypassedTheQueue()
    {
        using var bot = new StubBotApi();
        var plan = Plan(apiBaseUrl: bot.Root);
        var port = StartServer(plan, Service(plan));   // deliberately never started

        var (code, body) = await PostTestAsync(port);

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.False(body.GetProperty("viaQueue").GetBoolean());
        var detail = Str(body, "detail");
        Assert.Contains("bypassing the send queue", detail, StringComparison.Ordinal);
        Assert.Contains("did NOT prove delivery", detail, StringComparison.Ordinal);

        Assert.Contains(bot.Sent, m => m.Contains("push queue is NOT", StringComparison.Ordinal));
    }

    /// <summary>A delivery failure inside the queue is reported to the caller instead of being
    /// swallowed by the fire-and-forget send loop — with viaQueue still true, because the message did
    /// take the real path and the real path is what failed.</summary>
    [Fact]
    public async Task PostTelegramTest_ReportsAFailureThatHappenedInsideTheQueue()
    {
        using var bot = new StubBotApi { FailSendMessages = true };
        var plan = Plan(apiBaseUrl: bot.Root);
        var log = new CapturingLogger();
        var svc = Service(plan, log);
        var port = StartServer(plan, svc);
        await svc.StartAsync(CancellationToken.None);

        var (code, body) = await PostTestAsync(port);

        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("viaQueue").GetBoolean());
        Assert.Contains("failed to deliver", Str(body, "detail"), StringComparison.Ordinal);

        // Only SendLoopAsync logs this — its presence is independent evidence that the failure
        // happened on the queue's own thread and not in a direct call from the HTTP handler.
        Assert.Contains(log.Lines, l => l.Contains("Telegram send error", StringComparison.Ordinal));
    }

    /// <summary>A test that sends nothing is not a passing test. With no allowed chat id the token is
    /// valid and the feature is still useless, and the old reply said "ok".</summary>
    [Fact]
    public async Task PostTelegramTest_DoesNotClaimSuccess_WhenThereIsNobodyToSendTo()
    {
        using var bot = new StubBotApi();
        var plan = Plan(chatIds: false, apiBaseUrl: bot.Root);
        var svc = Service(plan);
        var port = StartServer(plan, svc);
        await svc.StartAsync(CancellationToken.None);

        var (code, body) = await PostTestAsync(port);

        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("allowedChatIds", Str(body, "error"), StringComparison.Ordinal);
        Assert.Empty(bot.Sent);
    }

    // ── StartAsync speaks on both outcomes ───────────────────────────────────────────────────────

    /// <summary>The silent early return is the shape of the original bug: a process that has decided
    /// to deliver nothing for the rest of the run and says so nowhere. Both outcomes now log, and the
    /// not-started line names which half is missing.</summary>
    [Fact]
    public async Task StartAsync_LogsOnBothOutcomes_NamingTheMissingHalf()
    {
        using var bot = new StubBotApi();

        var noBlock = new CapturingLogger();
        await Service(Plan(telegramBlock: false, token: false), noBlock).StartAsync(CancellationToken.None);
        Assert.Contains(noBlock.Lines, l => l.Contains("Telegram not started", StringComparison.Ordinal)
                                         && l.Contains("not configured", StringComparison.Ordinal));

        var noToken = new CapturingLogger();
        await Service(Plan(token: false), noToken).StartAsync(CancellationToken.None);
        Assert.Contains(noToken.Lines, l => l.Contains("Warning|", StringComparison.Ordinal)
                                         && l.Contains("Telegram not started", StringComparison.Ordinal)
                                         && l.Contains("no bot token", StringComparison.Ordinal));

        // Started, but push-only to nobody: "started" alone would read as success.
        var noChats = new CapturingLogger();
        var pushToNobody = Service(Plan(chatIds: false, apiBaseUrl: bot.Root), noChats);
        await pushToNobody.StartAsync(CancellationToken.None);
        await pushToNobody.StopAsync(CancellationToken.None);
        Assert.Contains(noChats.Lines, l => l.Contains("will deliver nothing", StringComparison.Ordinal)
                                         && l.Contains("allowedChatIds", StringComparison.Ordinal));

        var ready = new CapturingLogger();
        var live = Service(Plan(apiBaseUrl: bot.Root), ready);
        await live.StartAsync(CancellationToken.None);
        await live.StopAsync(CancellationToken.None);
        Assert.Contains(ready.Lines, l => l.Contains("Telegram bot started", StringComparison.Ordinal)
                                       && l.Contains("1 allowed chat id(s)", StringComparison.Ordinal));
    }

    // ── stubs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stands in for api.telegram.org, and can hold a sendMessage open — which is how the
    /// queue-routing proof pins the single-reader behaviour.</summary>
    private sealed class StubBotApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly Lock _gate = new();
        private readonly List<string> _sent = new();
        private TaskCompletionSource? _hold;

        public string Root { get; }
        public bool FailSendMessages { get; init; }

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

        public List<string> Sent { get { lock (_gate) return new List<string>(_sent); } }
        public int SendMessageCount { get { lock (_gate) return _sent.Count; } }

        public void HoldSendMessages()
        {
            lock (_gate) _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseSendMessages()
        {
            TaskCompletionSource? h;
            lock (_gate) { h = _hold; _hold = null; }
            h?.TrySetResult();
        }

        public async Task<bool> WaitForSendMessagesAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (SendMessageCount >= count) return true;
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
            var code = HttpStatusCode.OK;

            try
            {
                if (string.Equals(method, "getMe", StringComparison.Ordinal))
                {
                    body = """{"ok":true,"result":{"id":1,"username":"sc12_stub_bot"}}""";
                }
                else if (string.Equals(method, "sendMessage", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(payload);
                    var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

                    Task? hold;
                    lock (_gate) { _sent.Add(text); hold = _hold?.Task; }
                    if (hold != null) await hold.ConfigureAwait(false);

                    if (FailSendMessages) { code = HttpStatusCode.InternalServerError; body = """{"ok":false}"""; }
                    else body = """{"ok":true,"result":{"message_id":1}}""";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = (int)code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }

        public void Dispose()
        {
            ReleaseSendMessages();
            try { _listener.Stop(); } catch (Exception) { }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }
    }

    /// <summary>Keeps every log line so "did it say anything at all?" is a testable question — which
    /// for this feature it very much needs to be.</summary>
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
