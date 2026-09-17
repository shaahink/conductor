using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Conductor.Core.Courier;
using Conductor.Core.Integrations;
using Conductor.Courier;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>PK3.1 / D5 - protocol 3 on the courier's loopback: <c>POST /send</c>, <c>/react</c>,
/// <c>/delete</c> and <c>GET /chats</c>, every send answering with its message ids and writing them to
/// <c>messages.jsonl</c>, and a protocol-2 <c>/push</c> still taken.
///
/// <para>Driven end to end over HTTP: the real listener, the real desk and the real Telegram source,
/// with only the Bot API stubbed (<see cref="RecordingBotApi"/>). A free port and a scratch state
/// home per test - never the named port or the machine's courier home, which the real courier owns.</para></summary>
[Trait("Category", "Integration")]
public sealed class PK3_1CourierProtocol3Tests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ScratchToken = "111111:pk31-scratch-token";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly ITestOutputHelper _out;
    private readonly List<IDisposable> _junk = [];

    public PK3_1CourierProtocol3Tests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-pk31-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        Directory.CreateDirectory(_stateHome);
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

    /// <summary>A scratch courier on a free port: listener, desk and Telegram source, the Bot API
    /// stubbed. Returns a client that already carries the secret.</summary>
    private HttpClient StartCourier(RecordingBotApi bot, params CourierChat[] chats)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = bot.Root,
            Chats = chats.Length > 0 ? [.. chats] : [new CourierChat(AdminChat, "admin")],
        };
        settings.Save(_stateHome);

        var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        _junk.Add(source);
        var port = FreePort();
        var secret = CourierSecret.Resolve(_stateHome);
        var listener = new CourierListener(() => CourierPresence.Current("Conductor Courier (pk31 scratch)", port),
            new CourierDesk(source, settings, _stateHome, _out.WriteLine), secret, NullLogger.Instance, port);
        Assert.True(listener.TryStart(out var refusal), refusal);
        _junk.Add(listener);

        var http = new HttpClient { BaseAddress = new Uri(CourierEndpoint.BaseUrl(port)) };
        http.DefaultRequestHeaders.Add(CourierEndpoint.AuthHeader, secret);
        _junk.Add(http);
        return http;
    }

    private async Task<(HttpStatusCode Status, CourierAck Ack)> PostAsync(HttpClient http, string path, object body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body, CourierJson.Options), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(path, content);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"POST {path} -> {(int)resp.StatusCode} {text}");
        return (resp.StatusCode, JsonSerializer.Deserialize<CourierAck>(text, CourierJson.Options)!);
    }

    private string Scratch(string name, long bytes)
    {
        var path = Path.Combine(_tmp, name);
        using var fs = File.Create(path);
        fs.SetLength(bytes);
        return path;
    }

    /// <summary>Every path the loopback serves, read off <see cref="CourierEndpoint"/> itself - so a verb
    /// added next era is covered by the properties below without anyone remembering to list it.</summary>
    private static IEnumerable<string> ServedPaths() =>
        typeof(CourierEndpoint).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Path", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>The paths served ONLY on GET. The hello is served on both since PK3.3 (a run's POST names
    /// its project), so it counts as a POST verb here and carries a body below.</summary>
    private static bool IsGet(string path) => path is CourierEndpoint.ChatsPath;

    // -- the four verbs --------------------------------------------------------------------------

    [Fact]
    public async Task A_scratch_courier_answers_all_four_verbs_and_every_send_returns_its_ids()
    {
        using var bot = new RecordingBotApi();
        var http = StartCourier(bot);

        // /send, text, to a chat named by PROFILE, as a reply.
        var (status, text) = await PostAsync(http, CourierEndpoint.SendPath,
            new CourierSend("admin", "<b>PK3.1</b> protocol 3", ReplyTo: 77, Silent: true, Stamp: "conductor@feat - PK3 - PK3.1", Origin: "pk31-test"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(text.Accepted, text.Detail);
        Assert.Equal(AdminChat, text.ChatId);
        Assert.Equal([RecordingBotApi.AssignedMessageId], text.MessageIds);

        // /send, a media group of three photos: three ids back, in order.
        var photos = new[] { Scratch("a.png", 3), Scratch("b.png", 4), Scratch("c.png", 5) };
        var (groupStatus, group) = await PostAsync(http, CourierEndpoint.SendPath,
            new CourierSend(AdminChat, "before and after", Photos: photos, Origin: "pk31-test"));
        Assert.Equal(HttpStatusCode.OK, groupStatus);
        Assert.Equal([4242L, 4243L, 4244L], group.MessageIds);

        // /react and /delete, by id.
        var (reactStatus, react) = await PostAsync(http, CourierEndpoint.ReactPath, new CourierReact("admin", 4243, "\U0001FAE1", "pk31-test"));
        Assert.Equal(HttpStatusCode.OK, reactStatus);
        Assert.True(react.Accepted, react.Detail);
        var (deleteStatus, delete) = await PostAsync(http, CourierEndpoint.DeletePath, new CourierDelete(AdminChat, 4242, "pk31-test"));
        Assert.Equal(HttpStatusCode.OK, deleteStatus);
        Assert.True(delete.Accepted, delete.Detail);

        // GET /chats.
        var chatsJson = await http.GetStringAsync(CourierEndpoint.ChatsPath);
        _out.WriteLine("GET /chats -> " + chatsJson);
        var chats = JsonSerializer.Deserialize<List<CourierChat>>(chatsJson, CourierJson.Options)!;
        Assert.Equal(new CourierChat(AdminChat, "admin"), Assert.Single(chats));

        // What reached the Bot API.
        var calls = bot.Snapshot();
        foreach (var c in calls) _out.WriteLine(c.Method + " " + (c.Json ?? c.Media ?? ""));
        Assert.Equal(["sendMessage", "sendMediaGroup", "setMessageReaction", "deleteMessage"], calls.Select(c => c.Method));
        Assert.Equal("<b>PK3.1</b> protocol 3", calls[0].Text);                 // the exact bytes, no stamp added
        Assert.Equal(77, calls[0].ReplyToMessageId);
        Assert.True(calls[0].DisableNotification);
        Assert.Contains("\"parse_mode\":\"HTML\"", calls[0].Json!, StringComparison.Ordinal);
        Assert.Equal(3, calls[1].FileCount);
        Assert.Contains("attach://file2", calls[1].Media!, StringComparison.Ordinal);
        Assert.Contains("before and after", calls[1].Media!, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":4243", calls[2].Json!, StringComparison.Ordinal);
        Assert.Contains("\"emoji\"", calls[2].Json!, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":4242", calls[3].Json!, StringComparison.Ordinal);

        // The ledger: every id a send returned, with chat, origin, stamp and when; the delete as its own line.
        var ledger = CourierMessageLedger.Read(_stateHome);
        foreach (var m in ledger) _out.WriteLine("ledger: " + JsonSerializer.Serialize(m, CourierJson.Options));
        Assert.Equal([4242L, 4242L, 4243L, 4244L, 4242L], ledger.Select(m => m.Id));
        Assert.Equal(["send", "send", "send", "send", "delete"], ledger.Select(m => m.Verb));
        Assert.All(ledger, m => Assert.Equal(AdminChat, m.Chat));
        Assert.All(ledger, m => Assert.Equal("pk31-test", m.Origin));
        Assert.Equal("conductor@feat - PK3 - PK3.1", ledger[0].Stamp);
        Assert.All(ledger, m => Assert.True(DateTimeOffset.UtcNow - m.When < TimeSpan.FromMinutes(5)));
        Assert.True(File.Exists(Path.Combine(_stateHome, CourierHome.DirName, CourierHome.MessagesFileName)));
    }

    [Fact]
    public async Task A_protocol_2_push_is_still_accepted_and_its_id_is_ledgered()
    {
        using var bot = new RecordingBotApi();
        var http = StartCourier(bot);

        // The shape the installed engine pushes: protocol 2, a stamp, no /send anywhere.
        var (status, ack) = await PostAsync(http, CourierEndpoint.PushPath,
            new CourierPush(AdminChat, "session 5 ended", Protocol: 2, Stamp: "conductor@feat - PK2", Origin: "run f85c0bd7"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(ack.Accepted, ack.Detail);
        Assert.Equal([RecordingBotApi.AssignedMessageId], ack.MessageIds);
        var call = Assert.Single(bot.Snapshot());
        Assert.Equal("sendMessage", call.Method);
        Assert.Equal("conductor@feat - PK2\nsession 5 ended", call.Text);   // a push is still stamped

        var line = Assert.Single(CourierMessageLedger.Read(_stateHome));
        Assert.Equal((RecordingBotApi.AssignedMessageId, AdminChat, "run f85c0bd7", "push"), (line.Id, line.Chat, line.Origin, line.Verb));
    }

    // -- properties over the verb list -----------------------------------------------------------

    [Fact]
    public async Task Every_served_path_demands_the_secret()
    {
        using var bot = new RecordingBotApi();
        StartCourier(bot);
        var port = _junk.OfType<CourierListener>().Single().Port;
        using var stranger = new HttpClient { BaseAddress = new Uri(CourierEndpoint.BaseUrl(port)) };

        var paths = ServedPaths().ToList();
        Assert.Superset(new HashSet<string>(StringComparer.Ordinal)
            { CourierEndpoint.SendPath, CourierEndpoint.ReactPath, CourierEndpoint.DeletePath, CourierEndpoint.ChatsPath },
            new HashSet<string>(paths, StringComparer.Ordinal));

        foreach (var path in paths)
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = IsGet(path) ? await stranger.GetAsync(path) : await stranger.PostAsync(path, content);
            _out.WriteLine($"{path} without the secret -> {(int)resp.StatusCode}");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        Assert.Empty(bot.Snapshot());
    }

    [Fact]
    public async Task A_newer_protocol_is_refused_by_name_on_every_post_verb()
    {
        using var bot = new RecordingBotApi();
        var http = StartCourier(bot);
        var newer = CourierProtocol.Version + 1;

        var bodies = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [CourierEndpoint.PushPath] = new CourierPush(AdminChat, "x", Protocol: newer),
            [CourierEndpoint.SendPath] = new CourierSend(AdminChat, "x", Protocol: newer),
            [CourierEndpoint.ReactPath] = new CourierReact(AdminChat, 1, "\U0001F44D", Protocol: newer),
            [CourierEndpoint.DeletePath] = new CourierDelete(AdminChat, 1, Protocol: newer),
            [CourierEndpoint.HelloPath] = new CourierHello(_tmp, "PK31", "pk31", Protocol: newer),
        };
        // The property holds for every POST verb the endpoint names; a new one without a body here fails.
        Assert.Equal(ServedPaths().Where(p => !IsGet(p)).Order(StringComparer.Ordinal), bodies.Keys.Order(StringComparer.Ordinal));

        foreach (var (path, body) in bodies)
        {
            var (status, ack) = await PostAsync(http, path, body);
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Contains($"speaks protocol {CourierProtocol.Version}", ack.Detail, StringComparison.Ordinal);
            Assert.Contains(CourierProtocol.RestartVerb, ack.Detail, StringComparison.Ordinal);
        }
        Assert.Empty(bot.Snapshot());
    }

    // -- ceilings, by name, before any upload ----------------------------------------------------

    [Fact]
    public async Task Ceilings_are_refused_by_name_before_anything_reaches_the_bot_api()
    {
        using var bot = new RecordingBotApi();
        var http = StartCourier(bot);
        var photo = Scratch("ok.png", 10);
        var hugePhoto = Scratch("huge.png", TelegramLimits.MaxPhotoBytes + 1);
        var eleven = Enumerable.Range(0, 11).Select(i => Scratch($"p{i}.png", 1)).ToArray();

        var cases = new (CourierSend Send, string Named)[]
        {
            (new CourierSend(AdminChat, new string('x', TelegramLimits.MaxMessageChars + 1)), "4096-character message ceiling"),
            (new CourierSend(AdminChat, new string('x', TelegramLimits.MaxCaptionChars + 1), Photos: [photo]), "1024-character caption ceiling"),
            (new CourierSend(AdminChat, "eleven", Photos: eleven), "ceiling of 10 files"),
            (new CourierSend(AdminChat, "huge", Photos: [hugePhoto]), "10 MB photo ceiling"),
            (new CourierSend(AdminChat, "mixed", Photos: [photo], Documents: [photo]), "photos or documents, not both"),
            (new CourierSend(AdminChat, "gone", Documents: [Path.Combine(_tmp, "missing.md")]), "not a readable file"),
            (new CourierSend(AdminChat, "mode", ParseMode: "Markdown"), "use HTML, MarkdownV2 or none"),
            (new CourierSend(AdminChat, "   "), "text or at least one file"),
        };

        foreach (var (send, named) in cases)
        {
            var (status, ack) = await PostAsync(http, CourierEndpoint.SendPath, send);
            Assert.NotEqual(HttpStatusCode.OK, status);
            Assert.False(ack.Accepted);
            Assert.Contains(named, ack.Detail, StringComparison.Ordinal);
        }
        Assert.Empty(bot.Snapshot());
        Assert.Empty(CourierMessageLedger.Read(_stateHome));

        // The ceilings are ceilings, not one short of them.
        var (atStatus, at) = await PostAsync(http, CourierEndpoint.SendPath, new CourierSend(AdminChat, new string('x', TelegramLimits.MaxMessageChars)));
        Assert.Equal(HttpStatusCode.OK, atStatus);
        Assert.True(at.Accepted, at.Detail);
    }

    [Fact]
    public async Task Parse_mode_none_sends_the_bytes_as_plain_text()
    {
        using var bot = new RecordingBotApi();
        var http = StartCourier(bot);

        var (_, ack) = await PostAsync(http, CourierEndpoint.SendPath, new CourierSend(AdminChat, "a <b> is not bold here", ParseMode: "none"));

        Assert.True(ack.Accepted, ack.Detail);
        var call = Assert.Single(bot.Snapshot());
        Assert.Equal("a <b> is not bold here", call.Text);
        Assert.DoesNotContain("parse_mode", call.Json!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_delete_says_why_in_the_messengers_words_and_the_ledger_keeps_nothing()
    {
        using var bot = new RecordingBotApi();
        var http = StartCourier(bot);

        var (status, ack) = await PostAsync(http, CourierEndpoint.DeletePath, new CourierDelete(AdminChat, 404, "pk31-test"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.False(ack.Accepted);
        Assert.Contains("message to delete not found", ack.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(ScratchToken, ack.Detail, StringComparison.Ordinal);
        Assert.Empty(CourierMessageLedger.Read(_stateHome));
    }

    // -- the chat a sender means -----------------------------------------------------------------

    [Fact]
    public void A_profile_resolves_to_its_one_chat_and_anything_else_is_refused_by_name()
    {
        var one = new CourierSettings { Chats = [new CourierChat(AdminChat, "admin"), new CourierChat("-100200300", "observer")] };
        Assert.Equal(AdminChat, one.ChatFor("admin", out _));
        Assert.Equal("-100200300", one.ChatFor(" Observer ", out _));
        Assert.Equal("-100999", one.ChatFor("-100999", out _));           // an id is taken as written
        Assert.Equal("@a_channel", one.ChatFor("@a_channel", out _));

        Assert.Null(one.ChatFor("stakeholders", out var notAProfile));
        Assert.Contains("admin, observer", notAProfile!, StringComparison.Ordinal);
        Assert.Null(one.ChatFor("", out var empty));
        Assert.Contains("has to name its chat", empty!, StringComparison.Ordinal);

        var none = new CourierSettings { Chats = [new CourierChat(AdminChat, null)] };   // unnamed reads as admin
        Assert.Equal(AdminChat, none.ChatFor("admin", out _));
        Assert.Null(none.ChatFor("observer", out var noObserver));
        Assert.Contains("conductor courier chat --id", noObserver!, StringComparison.Ordinal);

        var two = new CourierSettings { Chats = [new CourierChat("1", "admin"), new CourierChat("2", "admin")] };
        Assert.Null(two.ChatFor("admin", out var ambiguous));
        Assert.Contains("lists 2 admin chats", ambiguous!, StringComparison.Ordinal);
    }
}
