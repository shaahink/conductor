using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Conductor.Core.Integrations;
using Conductor.Core.Watch;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF5.3 — remote supervision: the wake leaves the machine.
///
/// <para>What is worth being sure of here is not "an HTTP POST happens". It is the four decisions that
/// make a remote wake trustworthy at 3am:</para>
/// <list type="number">
/// <item>THE PAYLOAD IS THE BRIEF — byte for byte the document the local supervisor reads on stdin. A
/// ping that says "something happened" makes the remote reader go and look, which is the polling cost
/// this whole stage exists to delete, now paid over a network.</item>
/// <item>SECRETS STAY OUT OF THE PLAN — headers expand from the environment, and a variable that is not
/// set drops the header and SAYS SO, because posting a literal <c>${TOKEN}</c> earns a 401 whose cause
/// is invisible from the far end.</item>
/// <item>THE TWO FUSES DO NOT SILENCE EACH OTHER — a local supervisor that has burnt its hourly cap is
/// exactly the situation a human off the box needs to hear about.</item>
/// <item>A DEAD ENDPOINT IS REPORTED, NOT THROWN — a watch that crashes because a webhook is down turns
/// one parked run into two outages.</item>
/// </list>
/// </summary>
public sealed class SF5_3RemoteTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "sf53-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dir;
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public SF5_3RemoteTests()
    {
        _dir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }

    private PlanConfig Plan(SupervisorRemote? remote, TelegramConfig? telegram = null) => new()
    {
        Name = "sf53",
        Repo = _repo,
        Telegram = telegram,
        Supervisor = remote is null ? null : new SupervisorConfig { Command = "", Remote = remote },
    };

    private static JsonObject Brief(string reason = "budget-park") => new()
    {
        ["reason"] = reason,
        ["detail"] = "run parked: cost cap reached",
        ["plan"] = "sf53",
        ["stage"] = "T1",
        ["attempt"] = 2,
        ["checkpoints"] = "3/8",
        ["spendUsd"] = 12.5,
        ["costCapUsd"] = 12.0,
        ["standingOrders"] = "escalate anything that spends money",
        ["suggest"] = new JsonArray("conductor status", "conductor approve"),
    };

    private static string Json(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private string RemoteFires => Path.Combine(_dir, SupervisorPolicy.RemoteFiresFile);
    private string SupervisorFires => Path.Combine(_dir, SupervisorPolicy.FiresFile);

    // ── 1. The payload is the brief ──

    [Fact]
    public async Task Webhook_receives_the_brief_verbatim_as_json()
    {
        using var sink = new StubEndpoint();
        var brief = Brief();
        var text = Json(brief);

        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = sink.Root + "/wake" }), brief, text, null, Now);

        Assert.Null(d.Skipped);
        Assert.True(d.AnyDelivered, d.Deliveries.FirstOrDefault()?.Detail ?? "(nothing attempted)");
        var got = Assert.Single(sink.Received);
        Assert.Equal("POST", got.Method);
        Assert.StartsWith("application/json", got.ContentType, StringComparison.OrdinalIgnoreCase);
        // Byte for byte: whatever a cloud session picks up is what the local supervisor read on stdin.
        Assert.Equal(text, got.Body);
        Assert.Equal("budget-park", JsonNode.Parse(got.Body)!["reason"]!.ToString());
        Assert.Equal("escalate anything that spends money",
            JsonNode.Parse(got.Body)!["standingOrders"]!.ToString());
    }

    [Fact]
    public async Task The_status_line_names_the_host_and_never_the_url()
    {
        using var sink = new StubEndpoint();
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = sink.Root + "/hooks/s3cr3t-path" }),
            Brief(), Json(Brief()), null, Now);

        var row = Assert.Single(d.Deliveries);
        Assert.Contains("127.0.0.1", row.Detail, StringComparison.Ordinal);
        Assert.Contains("200", row.Detail, StringComparison.Ordinal);
        // A webhook URL routinely carries its own secret in the path; stderr is a log someone pastes.
        Assert.DoesNotContain("s3cr3t-path", row.Detail, StringComparison.Ordinal);
    }

    // ── 2. Secrets stay out of the plan ──

    [Fact]
    public async Task A_header_expands_from_the_environment()
    {
        var name = "SF53_WAKE_TOKEN_" + Guid.NewGuid().ToString("N")[..6];
        Environment.SetEnvironmentVariable(name, "sekrit-value");
        try
        {
            using var sink = new StubEndpoint();
            await WatchRemote.DispatchAsync(
                Plan(new SupervisorRemote
                {
                    WebhookUrl = sink.Root + "/wake",
                    Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Authorization"] = "Bearer ${" + name + "}",
                    },
                }), Brief(), Json(Brief()), null, Now);

            Assert.Equal("Bearer sekrit-value", Assert.Single(sink.Received).Headers["Authorization"]);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task An_unset_variable_drops_the_header_and_says_which_one()
    {
        using var sink = new StubEndpoint();
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote
            {
                WebhookUrl = sink.Root + "/wake",
                Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = "Bearer ${SF53_DEFINITELY_NOT_SET_ANYWHERE}",
                    ["Accept"] = "application/json",
                },
            }), Brief(), Json(Brief()), null, Now);

        var got = Assert.Single(sink.Received);
        Assert.False(got.Headers.ContainsKey("Authorization"));
        Assert.Equal("application/json", got.Headers["Accept"]);
        // The far end answers 401 and says nothing useful. This line is where the cause lives.
        Assert.Contains("dropped header(s) Authorization", Assert.Single(d.Deliveries).Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bearer ${SF53_ENVCASE}", "Bearer abc")]
    [InlineData("%SF53_ENVCASE%", "abc")]
    [InlineData("no refs at all", "no refs at all")]
    public void ExpandEnv_handles_both_spellings(string input, string expected)
    {
        Environment.SetEnvironmentVariable("SF53_ENVCASE", "abc");
        try { Assert.Equal(expected, WatchRemote.ExpandEnv(input)); }
        finally { Environment.SetEnvironmentVariable("SF53_ENVCASE", null); }
    }

    [Fact]
    public void ExpandEnv_returns_null_for_an_unset_variable() =>
        Assert.Null(WatchRemote.ExpandEnv("Bearer ${SF53_DEFINITELY_NOT_SET_ANYWHERE}"));

    // ── 3. The fuses ──

    [Fact]
    public async Task A_dispatch_is_counted_in_the_remote_ledger_and_not_the_supervisors()
    {
        using var sink = new StubEndpoint();
        await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = sink.Root + "/wake" }), Brief(), Json(Brief()), null, Now);

        Assert.Single(await File.ReadAllLinesAsync(RemoteFires));
        // Sharing SF5.2's ledger would mean each remote wake eats a local supervisor invocation, and
        // vice versa: two independent budgets quietly spending each other.
        Assert.False(File.Exists(SupervisorFires));
    }

    [Fact]
    public async Task The_remote_fuse_stops_a_plan_dispatch_and_says_so()
    {
        await File.WriteAllLinesAsync(RemoteFires, Enumerable.Range(0, 3).Select(i =>
            Now.AddMinutes(-5 - i).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

        using var sink = new StubEndpoint();
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = sink.Root + "/wake", MaxPerHour = 3 }),
            Brief(), Json(Brief()), null, Now);

        Assert.Empty(sink.Received);
        Assert.Contains("rate limited", d.Skipped!, StringComparison.Ordinal);
        Assert.False(d.Attempted);
    }

    [Fact]
    public async Task An_hour_old_fire_no_longer_counts()
    {
        await File.WriteAllLinesAsync(RemoteFires,
            [Now.AddMinutes(-61).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)]);

        using var sink = new StubEndpoint();
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = sink.Root + "/wake", MaxPerHour = 1 }),
            Brief(), Json(Brief()), null, Now);

        Assert.Null(d.Skipped);
        Assert.Single(sink.Received);
    }

    [Fact]
    public async Task Notify_override_beats_the_plan_and_is_not_bound_by_the_fuse()
    {
        await File.WriteAllLinesAsync(RemoteFires, Enumerable.Range(0, 20).Select(i =>
            Now.AddMinutes(-1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

        using var planSink = new StubEndpoint();
        using var oneOff = new StubEndpoint();
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = planSink.Root + "/wake", MaxPerHour = 1 }),
            Brief(), Json(Brief()), oneOff.Root + "/one-off", Now);

        Assert.True(d.AnyDelivered);
        Assert.Single(oneOff.Received);
        Assert.Empty(planSink.Received);
        // An operator typing a URL at a live run is making a deliberate one-off decision; it must not
        // spend the plan's budget either.
        Assert.Equal(20, (await File.ReadAllLinesAsync(RemoteFires)).Length);
    }

    [Fact]
    public async Task Notify_override_replaces_the_whole_block_including_the_phone()
    {
        SecretsStore.WriteTelegramToken(_dir, "sf53-test-token");
        using var bot = new StubEndpoint { TelegramMode = true };
        using var oneOff = new StubEndpoint();

        var plan = Plan(new SupervisorRemote { Telegram = true },
            new TelegramConfig { AllowedChatIds = { "1234" }, ApiBaseUrl = bot.Root });
        var d = await WatchRemote.DispatchAsync(plan, Brief(), Json(Brief()), oneOff.Root + "/one-off", Now);

        Assert.Single(oneOff.Received);
        // An operator aiming one wake at one URL has not asked to also ring the owner at 3am.
        Assert.Empty(bot.Received);
        Assert.Single(d.Deliveries);
    }

    [Fact]
    public async Task A_plan_with_no_remote_block_does_nothing_and_says_nothing()
    {
        var d = await WatchRemote.DispatchAsync(Plan(null), Brief(), Json(Brief()), null, Now);

        Assert.False(d.Attempted);
        Assert.Null(d.Skipped);
        Assert.False(File.Exists(RemoteFires));
    }

    [Fact]
    public async Task A_disabled_block_is_a_skip_with_a_reason_not_silence()
    {
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { Enabled = false, WebhookUrl = "https://example.invalid/wake" }),
            Brief(), Json(Brief()), null, Now);

        Assert.False(d.Attempted);
        Assert.Contains("disabled", d.Skipped!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_block_naming_no_target_says_that_rather_than_pretending_to_send()
    {
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote()), Brief(), Json(Brief()), null, Now);

        Assert.Contains("no webhookUrl", d.Skipped!, StringComparison.Ordinal);
    }

    // ── 4. Failure is reported, not thrown ──

    [Fact]
    public async Task A_dead_endpoint_is_reported_and_still_spends_a_fire()
    {
        int deadPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = $"http://127.0.0.1:{deadPort}/wake", TimeoutSeconds = 5 }),
            Brief(), Json(Brief()), null, Now);

        var row = Assert.Single(d.Deliveries);
        Assert.False(row.Delivered);
        Assert.Contains("failed", row.Detail, StringComparison.Ordinal);
        // Counted anyway: a fuse that only counts successes does not bound a webhook failing every wake.
        Assert.Single(await File.ReadAllLinesAsync(RemoteFires));
    }

    [Fact]
    public async Task A_non_2xx_answer_is_a_failed_delivery_not_a_delivered_one()
    {
        using var sink = new StubEndpoint { Status = 500 };
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = sink.Root + "/wake" }), Brief(), Json(Brief()), null, Now);

        var row = Assert.Single(d.Deliveries);
        Assert.False(row.Delivered);
        Assert.Contains("500", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_url_that_is_not_a_url_is_reported_rather_than_thrown()
    {
        var d = await WatchRemote.DispatchAsync(
            Plan(new SupervisorRemote { WebhookUrl = "not a url" }), Brief(), Json(Brief()), null, Now);

        Assert.False(Assert.Single(d.Deliveries).Delivered);
    }

    // ── 5. The Telegram half ──

    [Fact]
    public async Task Telegram_push_carries_the_wake_reason_the_stage_and_the_verbs()
    {
        SecretsStore.WriteTelegramToken(_dir, "sf53-test-token");
        using var bot = new StubEndpoint { TelegramMode = true };

        var plan = Plan(new SupervisorRemote { Telegram = true },
            new TelegramConfig { AllowedChatIds = { "1234" }, ApiBaseUrl = bot.Root });
        var d = await WatchRemote.DispatchAsync(plan, Brief(), Json(Brief()), null, Now);

        Assert.True(d.AnyDelivered, Assert.Single(d.Deliveries).Detail);
        var sent = JsonNode.Parse(Assert.Single(bot.Received).Body)!;
        Assert.Equal("1234", sent["chat_id"]!.ToString());
        var text = sent["text"]!.ToString();
        Assert.Contains("budget-park", text, StringComparison.Ordinal);
        Assert.Contains("T1", text, StringComparison.Ordinal);
        Assert.Contains("conductor approve", text, StringComparison.Ordinal);
        // The wake is sent by the WATCH process, so it still arrives when the engine is what died —
        // which is the one failure the engine's own push path can never report.
        Assert.Contains("chat 1234", Assert.Single(d.Deliveries).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_without_a_token_is_a_named_failure_not_a_silent_one()
    {
        var plan = Plan(new SupervisorRemote { Telegram = true },
            new TelegramConfig { AllowedChatIds = { "1234" }, ApiBaseUrl = "http://127.0.0.1:1/" });

        // Deliberately no secrets file. An env token would win here, so assert on the shape either way:
        // the delivery must fail loudly, and never look like a send that happened.
        var d = await WatchRemote.DispatchAsync(plan, Brief(), Json(Brief()), null, Now);

        var row = Assert.Single(d.Deliveries);
        Assert.False(row.Delivered);
        Assert.NotEmpty(row.Detail);
    }

    [Fact]
    public void The_phone_line_is_short_enough_to_be_read_on_a_lock_screen()
    {
        var text = WatchRemote.TelegramText(Brief("circuit-breaker"));

        Assert.Contains("conductor wake: circuit-breaker", text, StringComparison.Ordinal);
        Assert.Contains("spend $12.5 of $12", text, StringComparison.Ordinal);
        Assert.Contains("checkpoints 3/8", text, StringComparison.Ordinal);
        Assert.True(text.Length < 600, $"phone line is {text.Length} chars: {text}");
    }

    [Fact]
    public void A_huge_detail_is_truncated_rather_than_rejected_by_telegram()
    {
        var brief = Brief();
        brief["detail"] = new string('x', 9000);

        Assert.Equal(WatchRemote.TelegramMaxChars, WatchRemote.TelegramText(brief).Length);
    }

    [Fact]
    public void A_brief_missing_the_optional_fields_still_renders_a_phone_line()
    {
        var text = WatchRemote.TelegramText(new JsonObject
        {
            ["reason"] = "engine-gone",
            ["plan"] = "sf53",
            ["stage"] = null,
            ["detail"] = null,
        });

        Assert.Contains("conductor wake: engine-gone", text, StringComparison.Ordinal);
    }

    /// <summary>A stand-in for whatever is on the other end of the wake — a webhook receiver, or
    /// api.telegram.org — that records exactly what left the process.</summary>
    private sealed class StubEndpoint : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<Capture> _received = [];

        public string Root { get; }
        public int Status { get; init; } = 200;
        public bool TelegramMode { get; init; }

        public IReadOnlyList<Capture> Received
        {
            get { lock (_gate) return [.. _received]; }
        }

        public StubEndpoint()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Root = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}";
                probe.Stop();
            }

            _listener.Prefixes.Add(Root + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
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
            try
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in ctx.Request.Headers.AllKeys)
                    if (key is not null) headers[key] = ctx.Request.Headers[key] ?? "";

                lock (_gate)
                    _received.Add(new Capture(ctx.Request.HttpMethod, ctx.Request.ContentType ?? "", body, headers));

                // Never records the URL: in telegram mode the path carries the bot token.
                var payload = TelegramMode ? """{"ok":true,"result":{"message_id":1}}""" : """{"ok":true}""";
                var bytes = Encoding.UTF8.GetBytes(payload);
                ctx.Response.StatusCode = Status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch (Exception) { /* a stub that throws on teardown must not fail the test */ }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
        }

        internal sealed record Capture(string Method, string ContentType, string Body,
            Dictionary<string, string> Headers);
    }
}
