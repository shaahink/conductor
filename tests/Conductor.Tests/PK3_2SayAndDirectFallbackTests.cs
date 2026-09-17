using System.Net;
using System.Net.Sockets;
using System.Text;

using Conductor.Commands;
using Conductor.Core.Courier;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;
using Conductor.Courier;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>PK3.2 / D3 + D5 - <c>conductor say</c>, and the direct fallback: a send no courier takes
/// goes out with the environment's token, says so, and the health line names the path.
///
/// <para>Every Bot API call lands on <see cref="RecordingBotApi"/>; the test process has no ambient
/// token (<c>TestEnvironmentIsolation</c>) and every state home is a scratch one.</para></summary>
[Trait("Category", "Integration")]
public sealed class PK3_2SayAndDirectFallbackTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ScratchToken = "111111:pk32-scratch-token";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _repo;
    private readonly ITestOutputHelper _out;
    private readonly List<IDisposable> _junk = [];

    public PK3_2SayAndDirectFallbackTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-pk32-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        _repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# pk32 rig\n");
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_junk)) { try { d.Dispose(); } catch (Exception) { } }
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    // -- the rig ---------------------------------------------------------------------------------

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>A configured courier home whose Bot API is the stub - and no courier running.</summary>
    private CourierSettings Courier(RecordingBotApi bot)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = bot.Root,
            Chats = [new CourierChat(AdminChat, "admin")],
            Projects = [new CourierProject("PK32", _repo)],
        };
        settings.Save(_stateHome);
        return settings;
    }

    /// <summary>A live courier in the scratch home: listener, desk, source, and a presence record for
    /// this very process so <see cref="CourierPresence.Live"/> believes it.</summary>
    private void StartCourier(CourierSettings settings)
    {
        var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        _junk.Add(source);
        var port = FreePort();
        var listener = new CourierListener(() => CourierPresence.Current("Conductor Courier (pk32 scratch)", port),
            new CourierDesk(source, settings, _stateHome), CourierSecret.Resolve(_stateHome), NullLogger.Instance, port);
        Assert.True(listener.TryStart(out var refusal), refusal);
        _junk.Add(listener);
        CourierPresence.Current("Conductor Courier (pk32 scratch)", port).Write(_stateHome);
    }

    private async Task<(int Exit, string Output)> SayAsync(SayCommand.Settings settings, string? token = ScratchToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        await using var output = new StringWriter();
        var exit = await SayCommand.RunAsync(settings, output, _stateHome, http, token, CancellationToken.None);
        _out.WriteLine($"exit {exit}:\n{output}");
        return (exit, output.ToString());
    }

    private string Scratch(string name, long bytes)
    {
        var path = Path.Combine(_tmp, name);
        using var fs = File.Create(path);
        fs.SetLength(bytes);
        return path;
    }

    // -- say --dry-run ---------------------------------------------------------------------------

    [Fact]
    public async Task Dry_run_prints_the_exact_bytes_the_resolved_chat_and_the_path_and_sends_nothing()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);
        var body = Path.Combine(_tmp, "body.md");
        const string bytes = "<b>PK3.2</b> a body with an accent: café\nand a second line\n";
        await File.WriteAllTextAsync(body, bytes, new UTF8Encoding(false));

        var (exit, output) = await SayAsync(new SayCommand.Settings { To = "admin", File = body, ReplyTo = 12, DryRun = true });

        Assert.Equal(0, exit);
        Assert.Contains("chat:       admin -> " + AdminChat, output, StringComparison.Ordinal);
        Assert.Contains("path:       directly, with this environment's token - courier unreachable: no courier is running", output, StringComparison.Ordinal);
        Assert.Contains("method:     sendMessage", output, StringComparison.Ordinal);
        Assert.Contains("reply to:   12", output, StringComparison.Ordinal);
        Assert.Contains($"text:    {bytes.Length} characters, {Encoding.UTF8.GetByteCount(bytes)} UTF-8 bytes", output, StringComparison.Ordinal);
        Assert.Contains("----- exact bytes -----" + Environment.NewLine + bytes + "----- end -----", output, StringComparison.Ordinal);
        Assert.Empty(bot.Snapshot());
        Assert.Empty(CourierMessageLedger.Read(_stateHome));
    }

    [Fact]
    public async Task Dry_run_with_a_live_courier_names_its_port_and_a_media_group()
    {
        using var bot = new RecordingBotApi();
        StartCourier(Courier(bot));
        var photos = $"{Scratch("a.png", 3)},{Scratch("b.png", 4)}";

        var (exit, output) = await SayAsync(new SayCommand.Settings { Text = "before and after", Photo = photos, DryRun = true });

        Assert.Equal(0, exit);
        Assert.Contains("path:       through the courier on port ", output, StringComparison.Ordinal);
        Assert.Contains("method:     sendMediaGroup (2 photos)", output, StringComparison.Ordinal);
        Assert.Contains("caption:    16 characters", output, StringComparison.Ordinal);
        Assert.Empty(bot.Snapshot());
    }

    // -- say, through the courier and directly ----------------------------------------------------

    [Fact]
    public async Task A_live_courier_takes_the_send_and_say_prints_its_ids()
    {
        using var bot = new RecordingBotApi();
        StartCourier(Courier(bot));

        var (exit, output) = await SayAsync(new SayCommand.Settings { To = "admin", Text = "through the courier" }, token: null);

        Assert.Equal(0, exit);
        Assert.Contains($"sent through the courier: chat {AdminChat}, message id(s) {RecordingBotApi.AssignedMessageId}", output, StringComparison.Ordinal);
        Assert.Equal("through the courier", Assert.Single(bot.Snapshot()).Text);
        var line = Assert.Single(CourierMessageLedger.Read(_stateHome));
        Assert.Equal((SayCommand.Origin, CourierDesk.SendVerb), (line.Origin, line.Verb));
    }

    [Fact]
    public async Task With_no_courier_running_say_sends_directly_and_says_so()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);

        var (exit, output) = await SayAsync(new SayCommand.Settings { To = "admin", Text = "no courier" });

        Assert.Equal(0, exit);
        Assert.Contains($"courier unreachable - sent directly: chat {AdminChat}, message id(s) {RecordingBotApi.AssignedMessageId}", output, StringComparison.Ordinal);
        var call = Assert.Single(bot.Snapshot());
        Assert.Equal(("sendMessage", "no courier", AdminChat), (call.Method, call.Text, call.ChatId));
        var line = Assert.Single(CourierMessageLedger.Read(_stateHome));
        Assert.Equal(SayCommand.Origin + " (sent directly)", line.Origin);
    }

    [Fact]
    public async Task A_courier_whose_port_refuses_the_connection_is_unreachable_too()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);
        // A presence record for a live pid naming a port nothing listens on: the connection is refused.
        CourierSecret.Resolve(_stateHome);
        CourierPresence.Current("Conductor Courier (pk32 scratch)", FreePort()).Write(_stateHome);

        var (exit, output) = await SayAsync(new SayCommand.Settings { Text = "refused connection" });

        Assert.Equal(0, exit);
        Assert.Contains("courier unreachable - sent directly", output, StringComparison.Ordinal);
        Assert.Contains("did not answer", output, StringComparison.Ordinal);
        Assert.Single(bot.Snapshot());
    }

    [Fact]
    public async Task Without_a_courier_or_a_token_say_fails_by_name()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);

        var (exit, output) = await SayAsync(new SayCommand.Settings { Text = "nowhere" }, token: null);

        Assert.Equal(1, exit);
        Assert.Contains(TelegramCourierSource.TokenEnvVar + " is not set", output, StringComparison.Ordinal);
        Assert.Empty(bot.Snapshot());
    }

    [Fact]
    public async Task React_and_delete_go_directly_when_the_courier_is_down()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);

        var (reactExit, reacted) = await SayAsync(new SayCommand.Settings { React = "\U0001FAE1", Message = 77 });
        var (deleteExit, deleted) = await SayAsync(new SayCommand.Settings { Delete = 77 });

        Assert.Equal((0, 0), (reactExit, deleteExit));
        Assert.Contains("courier unreachable - reacted directly", reacted, StringComparison.Ordinal);
        Assert.Contains("courier unreachable - deleted directly", deleted, StringComparison.Ordinal);
        Assert.Equal(["setMessageReaction", "deleteMessage"], bot.Snapshot().Select(c => c.Method));
        var line = Assert.Single(CourierMessageLedger.Read(_stateHome));
        Assert.Equal((77L, CourierDesk.DeleteVerb), (line.Id, line.Verb));
    }

    // -- refusals, by name, before anything is sent ------------------------------------------------

    [Fact]
    public async Task Ceilings_and_nonsense_are_refused_by_name_before_anything_is_sent()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);
        var photo = Scratch("ok.png", 10);
        var eleven = string.Join(",", Enumerable.Range(0, 11).Select(i => Scratch($"p{i}.png", 1)));

        var cases = new (SayCommand.Settings Settings, string Named)[]
        {
            (new() { Text = new string('x', TelegramLimits.MaxMessageChars + 1) }, "4096-character message ceiling"),
            (new() { Text = new string('x', TelegramLimits.MaxCaptionChars + 1), Photo = photo }, "1024-character caption ceiling"),
            (new() { Text = "eleven", Photo = eleven }, "ceiling of 10 files"),
            (new() { Text = "huge", Photo = Scratch("huge.png", TelegramLimits.MaxPhotoBytes + 1) }, "10 MB photo ceiling"),
            (new() { Text = "huge", Document = Scratch("huge.bin", TelegramLimits.MaxDocumentBytes + 1) }, "50 MB document ceiling"),
            (new() { Text = "mixed", Photo = photo, Document = photo }, "photos or documents, not both"),
            (new() { Text = "x", File = photo }, "--text and --file are both the message"),
            (new() { React = "x" }, "--react needs --message"),
            (new() { Message = 5 }, "add --react"),
            (new() { React = "x", Message = 5, Text = "and a send" }, "three different requests"),
            (new() { }, "nothing to say"),
        };

        foreach (var (settings, named) in cases)
        {
            var (exit, output) = await SayAsync(settings);
            Assert.Equal(2, exit);
            Assert.Contains("say refused: ", output, StringComparison.Ordinal);
            Assert.Contains(named, output, StringComparison.Ordinal);
        }
        Assert.Empty(bot.Snapshot());
    }

    // -- D3 in a run: TelegramService in courier mode, the courier down ----------------------------

    private PlanConfig Plan(string apiRoot) => new()
    {
        Name = "PK32Plan",
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "PK3", Title = "One transport", Sessions = 1 } },
        Telegram = new TelegramConfig
        {
            PollIntervalSeconds = 60,
            ApiBaseUrl = apiRoot,
            Chats = { new TelegramChatEntry { ChatId = AdminChat, Profile = "admin" } },
        },
    };

    private sealed class Captured : ILogger<TelegramService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task A_run_whose_courier_is_down_sends_directly_logs_it_and_the_health_line_names_the_path()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);
        var plan = Plan(bot.Root);
        SecretsStore.WriteTelegramToken(plan.StateDir, ScratchToken);
        var log = new Captured();

        using var svc = new TelegramService(plan, new RunState { RunId = "pk32", SessionCounter = 6 }, log) { CourierStateHome = _stateHome };
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        Assert.False(svc.Polling);                    // courier mode: the poll stays off, courier or no courier
        Assert.NotNull(svc.Courier);

        await ((IMessageChannel)svc).SendAsync(new OutboundMessage(AdminChat, "the courier is down"), CancellationToken.None);

        var call = Assert.Single(bot.Snapshot());
        Assert.Equal("sendMessage", call.Method);
        Assert.EndsWith("the courier is down", call.Text!, StringComparison.Ordinal);   // the run's own stamp above it
        string[] lines;
        lock (log.Lines) lines = [.. log.Lines];
        foreach (var l in lines) _out.WriteLine("log: " + l);
        Assert.Contains(lines, l => l.StartsWith("courier unreachable - sent directly: no courier is running", StringComparison.Ordinal));
        Assert.Null(svc.Courier!.LastRefusal);

        var health = ChannelHealthProbe.Collect(plan, courierStateHome: _stateHome).Single(c => c.Channel == ChannelHealthProbe.CourierChannel);
        _out.WriteLine("health: " + health.Line);
        Assert.Equal(ChannelState.Dead, health.State);                                 // still loud: nothing files notes
        Assert.Contains("last push went directly at ", health.Line, StringComparison.Ordinal);
        Assert.Contains("sends directly", health.Fix, StringComparison.Ordinal);

        // The courier comes back: the next push goes through it, and the line says so.
        StartCourier(Courier(bot));
        await ((IMessageChannel)svc).SendAsync(new OutboundMessage(AdminChat, "the courier is back"), CancellationToken.None);
        Assert.Equal(2, bot.Snapshot().Count);
        var back = ChannelHealthProbe.Collect(plan, courierStateHome: _stateHome).Single(c => c.Channel == ChannelHealthProbe.CourierChannel);
        _out.WriteLine("health: " + back.Line);
        Assert.Equal(ChannelState.Ready, back.State);
        Assert.Contains("last push went through the courier at ", back.Line, StringComparison.Ordinal);
        Assert.Equal(CourierDesk.PushVerb, Assert.Single(CourierMessageLedger.Read(_stateHome)).Verb);

        await ((IHostedService)svc).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_run_without_a_token_of_its_own_still_records_the_refusal_rather_than_sending()
    {
        using var bot = new RecordingBotApi();
        Courier(bot);
        var plan = Plan(bot.Root);

        using var svc = new TelegramService(plan, new RunState { RunId = "pk32" }, NullLogger<TelegramService>.Instance) { CourierStateHome = _stateHome };
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        await ((IMessageChannel)svc).SendAsync(new OutboundMessage(AdminChat, "nobody to send it"), CancellationToken.None);

        Assert.Empty(bot.Snapshot());
        Assert.Contains("no courier is running", svc.Courier!.LastRefusal, StringComparison.Ordinal);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);
    }

    // -- the caption the run's own transport sends --------------------------------------------------

    [Fact]
    public async Task An_attachment_caption_is_clipped_inside_the_ceiling_not_one_past_it()
    {
        using var bot = new RecordingBotApi();
        var plan = Plan(bot.Root);
        SecretsStore.WriteTelegramToken(plan.StateDir, ScratchToken);
        var bare = Path.Combine(_tmp, "no-courier");
        Directory.CreateDirectory(bare);
        var shot = Scratch("evidence.png", 5);

        using var svc = new TelegramService(plan, new RunState { RunId = "pk32" }, NullLogger<TelegramService>.Instance) { CourierStateHome = bare };
        await svc.SendAsync(new OutboundMessage(AdminChat, "evidence", Attachment: new OutboundAttachment(shot, true, new string('x', 2000))),
            CancellationToken.None);

        var call = Assert.Single(bot.Snapshot());
        Assert.Equal("sendPhoto", call.Method);
        Assert.Equal(TelegramLimits.MaxCaptionChars, call.Caption!.Length);
    }
}
