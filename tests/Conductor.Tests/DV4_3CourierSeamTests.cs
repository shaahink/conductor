using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Conductor.Core.Courier;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;
using Conductor.Http;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV4.3 / findings §6.5, §6.9 and §1.4-B — the run↔courier seam, driven over a real loopback socket.
///
/// <para>Everything here runs on a scratch state home under the temp directory, on a port taken from
/// the OS rather than the named one: a rig that bound <see cref="CourierEndpoint.DefaultPort"/> would
/// starve a real courier on this machine of the socket its runs are dialling, which is trap 3's rule
/// about ports and state dirs applied to a new listener.</para>
///
/// <para>The two falsifiable exits the checkpoint names are
/// <see cref="The_hello_is_not_exempt_from_the_shared_secret"/> — a loopback endpoint with no auth is
/// the thing §6.5 exists to prevent — and
/// <see cref="A_courier_less_machine_is_byte_identical"/>, which is the KS11.1 standard applied to
/// this seam: with no courier written down, nothing about an old-shape plan changes.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DV4_3CourierSeamTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ScratchToken = "111111:dv43-scratch-token";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _bare;
    private readonly string _repo;
    private readonly ITestOutputHelper _out;
    private readonly List<IDisposable> _junk = [];

    public DV4_3CourierSeamTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-dv43-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        _bare = Path.Combine(_tmp, "no-courier-here");
        _repo = Path.Combine(_tmp, "alpha-repo");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(_bare);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# dv43 rig\n");
    }

    public void Dispose()
    {
        foreach (var d in _junk) { try { d.Dispose(); } catch (Exception) { } }
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    // ── the rig ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A port the OS just told us is free. Never the named one — see the class remarks.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private CourierSettings OperativeCourier(string? apiBase = null)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = apiBase,
            PollIntervalSeconds = 1,
            Chats = [new CourierChat(AdminChat, "admin")],
            Projects = [new CourierProject("Alpha", _repo)],
        };
        settings.Save(_stateHome);
        return settings;
    }

    /// <summary>A presence record for THIS process, so <see cref="CourierPresence.Live"/> believes
    /// it — the pid is real and its start time is the real one.</summary>
    private CourierPresence WritePresence(int? port, int protocol = CourierProtocol.Version)
    {
        var live = CourierPresence.Current("Conductor Courier (dv43 scratch)", port) with { Protocol = protocol };
        live.Write(_stateHome);
        return live;
    }

    private CourierListener StartListener(Func<CourierPush, CancellationToken, Task<CourierAck>> onPush,
        out int port, string? secret = null)
    {
        port = FreePort();
        var presence = CourierPresence.Current("Conductor Courier (dv43 scratch)", port);
        var listener = new CourierListener(() => presence, onPush,
            secret ?? CourierSecret.Resolve(_stateHome), NullLogger.Instance, port);
        Assert.True(listener.TryStart(out var refusal), refusal);
        _junk.Add(listener);
        return listener;
    }

    private static readonly List<CourierPush> Nothing = [];

#pragma warning disable RCS1163 // the delegate shape the listener hands over is (push, ct); this handler needs no token.
    private static Task<CourierAck> Accept(List<CourierPush> seen, CourierPush push, CancellationToken ct)
    {
        lock (seen) seen.Add(push);
        return Task.FromResult(new CourierAck(true));
    }
#pragma warning restore RCS1163

    private PlanConfig Plan(string apiRoot, bool twoWay = true) => new()
    {
        Name = "DV43Plan",
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "DV4", Title = "The courier", Sessions = 1 } },
        Telegram = new TelegramConfig
        {
            PollIntervalSeconds = 60,
            ApiBaseUrl = apiRoot,
            EnableTwoWay = twoWay,
            Chats = { new TelegramChatEntry { ChatId = AdminChat, Profile = "admin" } },
        },
    };

    // ── §6.5: the secret is a FILE, and the file is the boundary ────────────────────────────

    [Fact]
    public void The_secret_is_created_once_and_locked_to_this_account()
    {
        var first = CourierSecret.Resolve(_stateHome);
        var second = CourierSecret.Resolve(_stateHome);

        Assert.Equal(first, second);          // one install, one secret
        Assert.Equal(64, first.Length);       // 32 random bytes, hex
        Assert.Equal(first, CourierSecret.Read(_stateHome));

        // The claim that matters is about the FILE, not about the call that wrote it.
        var complaint = CourierSecret.ProtectionComplaint(_stateHome);
        _out.WriteLine("protection complaint: " + (complaint ?? "<none>"));
        Assert.Null(complaint);
    }

    [Fact]
    public void The_secret_file_grants_this_account_and_nobody_else()
    {
        // The ACL half of the claim is a Windows API; the mode half is asserted by
        // ProtectionComplaint in the test above, on whichever platform is running.
        if (!OperatingSystem.IsWindows()) return;

        CourierSecret.Resolve(_stateHome);
        var path = CourierHome.SecretPathFor(_stateHome);
        var acl = new FileInfo(path).GetAccessControl();

        Assert.True(acl.AreAccessRulesProtected, "the secret must not inherit the state home's permissions");

        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var rules = new List<string>();
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                 acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            var who = rule.IdentityReference.Value;
            if (!rules.Contains(who, StringComparer.Ordinal)) rules.Add(who);
        }

        _out.WriteLine("acl identities: " + string.Join(", ", rules));
        Assert.Equal([me], rules);
    }

    [Fact]
    public void A_secret_comparison_refuses_empty_and_wrong_values()
    {
        var secret = CourierSecret.Resolve(_stateHome);
        Assert.True(CourierSecret.Matches(secret, secret));
        Assert.False(CourierSecret.Matches(null, secret));
        Assert.False(CourierSecret.Matches("", secret));
        // CH4.1: appending a LITERAL "0" is not a mutation when the secret already ends in one, and
        // CourierSecret.Resolve mints a fresh value per state home - so this line was a 1-in-16 flake
        // that asserted the opposite of its intent whenever it fired. Flip to a character that cannot
        // be the one that is there.
        Assert.False(CourierSecret.Matches(secret[..^1] + (secret[^1] == '0' ? '1' : '0'), secret));
        Assert.False(CourierSecret.Matches(secret, null));
    }

    // ── §6.5: loopback only, one named port, auth on every verb ─────────────────────────────

    [Fact]
    public void The_courier_binds_loopback_and_only_loopback()
    {
        Assert.StartsWith("http://127.0.0.1:", CourierEndpoint.PrefixFor(CourierEndpoint.DefaultPort),
            StringComparison.Ordinal);
        Assert.EndsWith("/", CourierEndpoint.PrefixFor(CourierEndpoint.DefaultPort), StringComparison.Ordinal);

        // And on the wire: the same listener that answers on 127.0.0.1 is not reachable on this
        // machine's own LAN address, which is what "loopback-only" has to mean to be worth stating.
        using var listener = StartListener((p, c) => Accept(Nothing, p, c), out var port);
        var lan = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (lan is null) { _out.WriteLine("no non-loopback IPv4 on this machine - negative not provable here"); return; }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var reached = socket.BeginConnect(lan!, port, null, null).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3))
                      && socket.Connected;
        _out.WriteLine($"connect to {lan}:{port} -> {(reached ? "REACHED" : "refused/timed out")}");
        Assert.False(reached);
    }

    [Fact]
    public async Task The_hello_is_not_exempt_from_the_shared_secret()
    {
        var secret = CourierSecret.Resolve(_stateHome);
        using var listener = StartListener((p, c) => Accept(Nothing, p, c), out var port);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var anonymous = await http.GetAsync(new Uri(CourierEndpoint.BaseUrl(port) + CourierEndpoint.HelloPath));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, CourierEndpoint.BaseUrl(port) + CourierEndpoint.HelloPath);
        wrong.Headers.TryAddWithoutValidation(CourierEndpoint.AuthHeader, new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        using var right = new HttpRequestMessage(HttpMethod.Get, CourierEndpoint.BaseUrl(port) + CourierEndpoint.HelloPath);
        right.Headers.TryAddWithoutValidation(CourierEndpoint.AuthHeader, secret);
        using var ok = await http.SendAsync(right);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var body = await ok.Content.ReadAsStringAsync();
        _out.WriteLine("hello: " + body);
        var hello = System.Text.Json.JsonSerializer.Deserialize<CourierPresence>(body, CourierJson.Options);
        Assert.Equal(CourierProtocol.Version, hello!.Protocol);
        Assert.Equal(Environment.ProcessId, hello.Pid);
        Assert.Equal(port, hello.Port);
    }

    [Fact]
    public void A_taken_port_is_refused_by_name_and_never_scanned_past()
    {
        using var first = StartListener((p, c) => Accept(Nothing, p, c), out var port);

        var second = new CourierListener(() => CourierPresence.Current(null, port),
            (p, c) => Accept(Nothing, p, c), CourierSecret.Resolve(_stateHome), NullLogger.Instance, port);
        _junk.Add(second);

        Assert.False(second.TryStart(out var refusal));
        _out.WriteLine("refusal: " + refusal);
        Assert.Contains(port.ToString(CultureInfo.InvariantCulture), refusal!, StringComparison.Ordinal);
        Assert.Contains(CourierEndpoint.PortEnvVar, refusal!, StringComparison.Ordinal);
        Assert.Contains("courier status", refusal!, StringComparison.Ordinal);

        // The claim behind the refusal: it did not quietly land on the next port up.
        Assert.Equal(port, second.Port);
    }

    // ── the client's three refusals, each by name ───────────────────────────────────────────

    [Fact]
    public void A_run_with_no_courier_is_refused_by_name()
    {
        Assert.Null(CourierClient.TryOpen(_bare, out var refusal));
        _out.WriteLine(refusal!);
        Assert.Contains("no courier is running", refusal!, StringComparison.Ordinal);
        Assert.Contains(CourierProtocol.RestartVerb, refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_courier_with_no_listener_is_refused_by_name()
    {
        WritePresence(port: null);
        Assert.Null(CourierClient.TryOpen(_stateHome, out var refusal));
        _out.WriteLine(refusal!);
        Assert.Contains("without a loopback listener", refusal!, StringComparison.Ordinal);
        Assert.Contains(CourierProtocol.RestartVerb, refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_courier_is_refused_by_the_same_rule_DV4_2_wrote()
    {
        var stale = WritePresence(port: 65000, protocol: CourierProtocol.Version - 1);
        Assert.Null(CourierClient.TryOpen(_stateHome, out var refusal));
        _out.WriteLine(refusal!);

        // Not a second definition of stale: the sentence IS RefuseStale's.
        Assert.Equal(CourierProtocol.RefuseStale(stale), refusal);
    }

    [Fact]
    public void A_courier_without_a_secret_is_refused_by_name()
    {
        WritePresence(port: 65000);
        Assert.Null(CourierClient.TryOpen(_stateHome, out var refusal));
        _out.WriteLine(refusal!);
        Assert.Contains(CourierHome.SecretPathFor(_stateHome), refusal!, StringComparison.Ordinal);
    }

    // ── the seam, end to end ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_run_dials_the_courier_named_in_the_presence_record_and_pushes_through_it()
    {
        var seen = new List<CourierPush>();
        using var listener = StartListener((p, c) => Accept(seen, p, c), out var port);
        WritePresence(port);

        using var client = CourierClient.TryOpen(_stateHome, out var refusal);
        Assert.Null(refusal);
        Assert.NotNull(client);

        var hello = await client!.HelloAsync();
        Assert.Equal(CourierProtocol.Version, hello!.Protocol);

        var ack = await client.PushAsync(new CourierPush(AdminChat, "the run says hello",
            Buttons: [new CourierButton("promote", "promote:1")], Stamp: "<i>alpha@main · DV4</i>"));
        Assert.True(ack.Accepted, ack.Detail);

        var got = Assert.Single(seen);
        Assert.Equal(AdminChat, got.ChatId);
        Assert.Equal("<i>alpha@main · DV4</i>\nthe run says hello", got.Stamped());
        Assert.Equal("promote:1", Assert.Single(got.Buttons!).CallbackData);
    }

    [Fact]
    public async Task A_push_from_a_newer_run_is_refused_by_name_rather_than_half_delivered()
    {
        var seen = new List<CourierPush>();
        using var listener = StartListener((p, c) => Accept(seen, p, c), out var port);
        WritePresence(port);

        using var client = CourierClient.TryOpen(_stateHome, out _);
        var ack = await client!.PushAsync(new CourierPush(AdminChat, "from the future",
            Protocol: CourierProtocol.Version + 1));

        _out.WriteLine(ack.Detail);
        Assert.False(ack.Accepted);
        Assert.Contains(CourierProtocol.RestartVerb, ack.Detail, StringComparison.Ordinal);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task A_dead_daemon_makes_the_channel_say_why_instead_of_throwing()
    {
        var port = FreePort();          // bound by nobody: the daemon was killed
        WritePresence(port);
        CourierSecret.Resolve(_stateHome);

        var channel = new CourierChannel([new ChatTarget(AdminChat, ChatProfile.Admin)], _stateHome, "Alpha");
        await channel.SendAsync(new OutboundMessage(AdminChat, "nobody is listening"), CancellationToken.None);

        _out.WriteLine(channel.LastRefusal!);
        Assert.NotNull(channel.LastRefusal);
        Assert.Contains(CourierProtocol.RestartVerb, channel.LastRefusal!, StringComparison.Ordinal);

        // Fire-and-forget by contract: the seam's own doc comment says this must never throw.
        await channel.EnqueueAsync(new OutboundMessage(AdminChat, "still nobody"), CancellationToken.None);
    }

    [Fact]
    public async Task The_channel_carries_the_runs_stamp_because_only_a_run_can_render_it()
    {
        var seen = new List<CourierPush>();
        using var listener = StartListener((p, c) => Accept(seen, p, c), out var port);
        WritePresence(port);

        var channel = new CourierChannel([new ChatTarget(AdminChat, ChatProfile.Admin)], _stateHome, "Alpha",
            stamp: (session, stage) => $"<i>alpha · s{session} · {stage}</i>");

        Assert.True(channel.IsLive);
        await channel.SendAsync(
            new OutboundMessage(AdminChat, "body", SessionNumber: 12, StageId: "DV4"), CancellationToken.None);

        var got = Assert.Single(seen);
        Assert.Equal("<i>alpha · s12 · DV4</i>\nbody", got.Stamped());
        Assert.Null(channel.LastRefusal);
    }

    // ── §6.9: the token handover ────────────────────────────────────────────────────────────

    [Fact]
    public void Precedence_needs_a_courier_that_is_actually_operative()
    {
        Assert.False(CourierPrecedence.Configured(_bare));
        Assert.Null(CourierPrecedence.PollingRefusal(_bare));

        // Half-written is not configured: the daemon refuses to start on this, so nothing will ever
        // hold the token and a run that stopped polling for it would go deaf for no reason.
        new CourierSettings { Chats = [new CourierChat(AdminChat, "admin")] }.Save(_stateHome);
        Assert.False(CourierPrecedence.Configured(_stateHome));
        Assert.Null(CourierPrecedence.PollingRefusal(_stateHome));

        OperativeCourier();
        Assert.True(CourierPrecedence.Configured(_stateHome));
        var refusal = CourierPrecedence.PollingRefusal(_stateHome);
        _out.WriteLine(refusal!);
        Assert.Contains(CourierTask.DefaultName, refusal!, StringComparison.Ordinal);
        Assert.Contains("conductor courier status", refusal!, StringComparison.Ordinal);

        // KS11.1 rule one: precedence is channel-agnostic code, so the sentence an operator reads
        // names the courier and the rule and never the messenger - in a literal as much as in a type.
        Assert.DoesNotContain("telegram", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_configured_courier_stops_the_run_polling_and_names_itself()
    {
        OperativeCourier();
        var seen = new List<CourierPush>();
        using var listener = StartListener((p, c) => Accept(seen, p, c), out var port);
        WritePresence(port);

        using var bot = new RecordingBotApi();
        using var svc = new TelegramService(Plan(bot.Root), new RunState { RunId = "dv43", SessionCounter = 3 },
            NullLogger<TelegramService>.Instance) { CourierStateHome = _stateHome };

        await ((IHostedService)svc).StartAsync(CancellationToken.None);

        Assert.False(svc.Polling);
        Assert.NotNull(svc.Courier);
        _out.WriteLine(svc.PollingRefusedBy!);
        Assert.Contains(CourierTask.DefaultName, svc.PollingRefusedBy!, StringComparison.Ordinal);

        // Two-way is withdrawn with the poll loop: nothing is reading the updates this run would act on.
        Assert.False(((IMessageChannel)svc).AllowsControl);

        // And the push it would have sent itself goes THROUGH the daemon.
        await ((IMessageChannel)svc).SendAsync(new OutboundMessage(AdminChat, "through the courier"),
            CancellationToken.None);
        Assert.Equal("through the courier", Assert.Single(seen).Text);
        Assert.Empty(bot.Snapshot());

        await ((IHostedService)svc).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_courier_less_machine_is_byte_identical()
    {
        using var bot = new RecordingBotApi();
        var plan = Plan(bot.Root);
        SecretsStore.WriteTelegramToken(plan.StateDir, ScratchToken);

        using var svc = new TelegramService(plan, new RunState { RunId = "dv43", SessionCounter = 3 },
            NullLogger<TelegramService>.Instance) { CourierStateHome = _bare };

        await ((IHostedService)svc).StartAsync(CancellationToken.None);

        Assert.True(svc.Polling);                       // today's behaviour, unchanged
        Assert.Null(svc.Courier);
        Assert.Null(svc.PollingRefusedBy);
        Assert.True(((IMessageChannel)svc).AllowsControl);

        // The roll-up an old-shape plan prints has no courier token in it at all - the golden
        // replay standard KS11.1 set, applied to the surface DV1.1 owns.
        var channels = ChannelHealthProbe.Collect(plan, telegramStarted: true, courierStateHome: _bare);
        var line = ChannelHealthProbe.SummaryLine(channels);
        _out.WriteLine("roll-up: " + line);
        Assert.Equal("telegram ready · github off", line);
        Assert.DoesNotContain("courier", line, StringComparison.OrdinalIgnoreCase);

        await ((IHostedService)svc).StopAsync(CancellationToken.None);
    }

    // ── §1.4-B: the daemon is a new single point of failure, and it is LOUD ──────────────────

    [Fact]
    public void Killing_the_daemon_makes_the_channel_roll_up_say_so()
    {
        OperativeCourier();
        var plan = Plan("http://127.0.0.1:1");

        // Alive: a listener bound, a presence record for this very process, a secret on disk.
        using var listener = StartListener((p, c) => Accept(Nothing, p, c), out var port);
        WritePresence(port);
        var healthy = ChannelHealthProbe.Collect(plan, courierStateHome: _stateHome)
            .Single(c => c.Channel == ChannelHealthProbe.CourierChannel);
        _out.WriteLine("alive: " + healthy.Line);
        Assert.Equal(ChannelState.Ready, healthy.State);
        Assert.False(healthy.IsLoud);

        // Killed: the record it left behind names a pid that is not there any more.
        (CourierPresence.Current("Conductor Courier (dv43 scratch)", port) with { Pid = 999_999 }).Write(_stateHome);

        var dead = ChannelHealthProbe.Collect(plan, courierStateHome: _stateHome)
            .Single(c => c.Channel == ChannelHealthProbe.CourierChannel);
        _out.WriteLine("killed: " + dead.Line);
        Assert.Equal(ChannelState.Dead, dead.State);
        Assert.True(dead.IsLoud, "a dead courier has to reach REPORT.md, /status and the owner queue");
        Assert.Equal(CourierProtocol.RestartVerb, dead.FixCommand);
        Assert.Contains("courier DEAD", ChannelHealthProbe.SummaryLine(
            ChannelHealthProbe.Collect(plan, courierStateHome: _stateHome)), StringComparison.Ordinal);
    }

    // ── the wire, at the adapter ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_artifact_is_named_in_the_message_rather_than_dropped()
    {
        using var bot = new RecordingBotApi();
        var settings = OperativeCourier(bot.Root);
        using var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);

        var shot = Path.Combine(_tmp, "evidence.png");
        await File.WriteAllBytesAsync(shot, [1, 2, 3]);

        var why = await source.SendAsync(new CourierPush(AdminChat, "the run finished",
            Severity: nameof(PushSeverity.Alert), AttachmentPath: shot, AttachmentAsPhoto: true,
            AttachmentCaption: "a screenshot"), CancellationToken.None);

        Assert.Null(why);
        var call = Assert.Single(bot.Snapshot());
        _out.WriteLine(call.Describe());
        Assert.Equal("sendMessage", call.Method);
        Assert.False(call.DisableNotification);         // Alert buzzes; Quiet does not
        Assert.Contains("not attached", call.Text!, StringComparison.Ordinal);
        Assert.Contains(shot, call.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_send_comes_back_as_a_sentence_not_an_exception()
    {
        var settings = OperativeCourier("http://127.0.0.1:1");
        using var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);

        var why = await source.SendAsync(new CourierPush(AdminChat, "nowhere to go"), CancellationToken.None);
        _out.WriteLine(why!);
        Assert.NotNull(why);
        Assert.Contains("bot API", why!, StringComparison.Ordinal);
    }
}
