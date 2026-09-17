using System.Globalization;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core.Courier;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// PK5.1 / D9 / findings F-COUR-5 and F-COUR-6 - inbound with a name. A note carries who sent it and
/// the message it was; the courier acknowledges it with a reaction on that message and never with a
/// message; the promote button rides the reply to <c>/note</c>; <c>say --reply-to</c> takes a note id.
///
/// <para>Driven against the loopback Bot API stand-in on a scratch state home, a scratch token and
/// scratch chat ids. Identity here is for addressing only (F-OBS-4): no assertion below lets a sender
/// change what a note may do.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PK5_1InboundWithANameTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string OtherChat = "770000009";
    private const string ScratchToken = "111111:pk51-scratch-token";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _repo;
    private readonly ITestOutputHelper _out;

    public PK5_1InboundWithANameTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-pk51-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        _repo = Path.Combine(_tmp, "pk51-repo");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# pk51 rig\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    // -- the rig ---------------------------------------------------------------------------------

    private CourierSettings Settings(RecordingBotApi bot)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = bot.Root,
            PollIntervalSeconds = 1,
            Chats = [new CourierChat(AdminChat, "admin")],
            Projects = [new CourierProject("PK51", _repo)],
        };
        settings.Save(_stateHome);
        new ChatRoutes(_stateHome).Set(AdminChat, null, StateHome.SlugFor(_repo, "PK51"));
        return settings;
    }

    private (TelegramCourierSource Source, CourierDaemon Daemon) Courier(CourierSettings settings)
    {
        var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        return (source, new CourierDaemon(source, settings, _stateHome, m => _out.WriteLine(m)));
    }

    private InboxStore Inbox() => new(Path.Combine(_repo, ".conductor"));

    private static readonly object Ada = new { id = 5550001, is_bot = false, first_name = "Ada", last_name = "Lovelace", username = "ada_l" };

    private static readonly object AdminChatRef = new { id = long.Parse(AdminChat, CultureInfo.InvariantCulture), type = "private" };

    /// <summary>A Bot API message object, as the update carries it.</summary>
    private static string Json(object message) => JsonSerializer.Serialize(message);

    /// <summary>One message of each kind the channel files, from Ada. A kind added to
    /// <see cref="InboundMediaKind"/> without a line here fails the property test below rather than
    /// escaping it.</summary>
    private static string MessageOf(InboundMediaKind? kind, long messageId, RecordingBotApi bot)
    {
        if (kind is null) return Json(new { message_id = messageId, chat = AdminChatRef, from = Ada, text = "a typed note " + messageId.ToString(CultureInfo.InvariantCulture) });

        var fileId = "f-" + messageId.ToString(CultureInfo.InvariantCulture);
        bot.AddFile(fileId, "files/" + fileId + ".bin", new byte[512]);
        var file = new { file_id = fileId, file_unique_id = "u" + fileId, duration = 3, file_size = 512, file_name = "f.bin", width = 10, height = 10 };
        return kind switch
        {
            InboundMediaKind.Voice => Json(new { message_id = messageId, chat = AdminChatRef, from = Ada, voice = file }),
            InboundMediaKind.Audio => Json(new { message_id = messageId, chat = AdminChatRef, from = Ada, audio = file }),
            InboundMediaKind.Document => Json(new { message_id = messageId, chat = AdminChatRef, from = Ada, document = file }),
            InboundMediaKind.Photo => Json(new { message_id = messageId, chat = AdminChatRef, from = Ada, photo = new[] { file } }),
            _ => throw new InvalidOperationException($"PK5.1: no rig message for the new kind {kind}; add one so its acknowledgement is pinned too."),
        };
    }

    // -- D9: the note carries a name and a message id ------------------------------------------------

    [Fact]
    public async Task A_note_filed_from_a_rig_update_carries_its_sender_and_message_id()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot);
        var (source, courier) = Courier(settings);
        using var _ = source;

        bot.QueueMessage(MessageOf(null, 41, bot));
        var tick = await courier.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, tick.Filed);

        var note = Assert.Single(Inbox().All());
        Assert.Equal(41, note.MessageId);
        Assert.Equal(5550001, note.SenderId);
        Assert.Equal("Ada Lovelace", note.SenderName);
        Assert.Equal("ada_l", note.SenderUsername);
        Assert.Equal("Ada Lovelace (@ada_l)", note.Sender);

        // On disk, where a session three weeks later reads it - not only in memory.
        var onDisk = await File.ReadAllTextAsync(Path.Combine(Inbox().Dir, "notes", note.Id.ToString(CultureInfo.InvariantCulture) + ".json"));
        _out.WriteLine(onDisk);
        using var json = JsonDocument.Parse(onDisk);
        Assert.Equal(41, json.RootElement.GetProperty("MessageId").GetInt64());
        Assert.Equal(5550001, json.RootElement.GetProperty("SenderId").GetInt64());
        Assert.Equal("Ada Lovelace", json.RootElement.GetProperty("SenderName").GetString());
        Assert.Equal("ada_l", json.RootElement.GetProperty("SenderUsername").GetString());
    }

    /// <summary>A note written before PK5.1 has none of the four fields. It still reads, still lists,
    /// and simply has no sender - nothing guesses one.</summary>
    [Fact]
    public void A_note_filed_before_PK5_1_still_reads_and_lists_with_no_sender()
    {
        var store = Inbox();
        var notes = Path.Combine(store.Dir, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "83806400.json"), """
            {
              "Id": 83806400,
              "ReceivedUtc": "2026-09-05T10:00:00Z",
              "ChatId": "99205495",
              "Kind": "text",
              "Text": "an old note, filed by the DV4 courier",
              "ReplyToMessageId": 651
            }
            """);

        var old = Assert.Single(store.All());
        Assert.Equal("an old note, filed by the DV4 courier", old.Text);
        Assert.Null(old.MessageId);
        Assert.Null(old.SenderId);
        Assert.Null(old.Sender);
        Assert.Null(store.FindByMessage(AdminChat, 651));   // a reply-to id is not the note's own message
        Assert.Equal("an old note, filed by the DV4 courier", old.Summary);
    }

    // -- D9: the acknowledgement is a reaction, never a message --------------------------------------

    /// <summary>The property: whatever kind of note is filed, the sender is answered by exactly one
    /// reaction on that note's own message and by no message at all.</summary>
    [Fact]
    public async Task Every_kind_of_filed_note_is_acknowledged_by_a_reaction_on_it_and_never_a_message()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot);
        var (source, courier) = Courier(settings);
        using var _ = source;

        var kinds = new List<InboundMediaKind?> { null };
        kinds.AddRange(Enum.GetValues<InboundMediaKind>().Select(k => (InboundMediaKind?)k));
        var messageId = 100L;

        foreach (var kind in kinds)
        {
            messageId++;
            var before = bot.Snapshot().Count;
            bot.QueueMessage(MessageOf(kind, messageId, bot));
            var tick = await courier.PollOnceAsync(CancellationToken.None);
            Assert.Equal(1, tick.Filed);

            var calls = bot.Snapshot().Skip(before).Where(c => c.Method is not ("getUpdates" or "getFile" or "download")).ToList();
            _out.WriteLine($"{kind?.ToString() ?? "text"}: {string.Join(", ", calls.Select(c => c.Method))}");
            Assert.DoesNotContain(calls, c => c.Method.StartsWith("send", StringComparison.Ordinal));
            var reaction = Assert.Single(calls, c => c.Method == "setMessageReaction");
            using var body = JsonDocument.Parse(reaction.Json!);
            Assert.Equal(messageId, body.RootElement.GetProperty("message_id").GetInt64());
            Assert.Equal(AdminChat, body.RootElement.GetProperty("chat_id").ToString());
            Assert.Equal(InboundAck.Reaction, body.RootElement.GetProperty("reaction")[0].GetProperty("emoji").GetString());
        }

        Assert.Equal(kinds.Count, Inbox().All().Count);
        Assert.All(Inbox().All(), n => Assert.NotNull(n.MessageId));
    }

    /// <summary>A refusal is not an acknowledgement: a file too big to fetch is still said in words,
    /// because the sender has to know the file is not kept. The note itself still gets its reaction.</summary>
    [Fact]
    public async Task A_file_too_big_to_fetch_is_refused_in_words_and_the_note_still_gets_its_reaction()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot);
        var (source, courier) = Courier(settings);
        using var _ = source;

        bot.QueueMessage(Json(new
        {
            message_id = 61, chat = AdminChatRef, from = Ada, caption = "the long one",
            document = new { file_id = "huge", file_unique_id = "uh", mime_type = "video/mp4", file_size = TelegramLimits.MaxDownloadBytes + 1, file_name = "huge.mp4" },
        }));
        await courier.PollOnceAsync(CancellationToken.None);

        var refusal = Assert.Single(bot.Snapshot(), c => c.Method == "sendMessage");
        Assert.Contains("huge.mp4", refusal.Text!, StringComparison.Ordinal);
        Assert.Null(refusal.ReplyMarkup);
        Assert.Single(bot.Snapshot(), c => c.Method == "setMessageReaction");
    }

    // -- D9: the button rides the reply to /note -----------------------------------------------------

    [Fact]
    public async Task Note_asked_as_a_reply_names_the_note_its_sender_and_project_and_carries_the_button()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot);
        var (source, courier) = Courier(settings);
        using var _ = source;

        bot.QueueMessage(MessageOf(null, 71, bot));
        await courier.PollOnceAsync(CancellationToken.None);
        var filed = Assert.Single(Inbox().All());

        bot.QueueMessage(Json(new
        {
            message_id = 72, chat = AdminChatRef, from = Ada, text = "/note",
            reply_to_message = new { message_id = 71, chat = AdminChatRef, text = "a typed note 71" },
        }));
        await courier.PollOnceAsync(CancellationToken.None);

        var answer = Assert.Single(bot.Snapshot(), c => c.Method == "sendMessage");
        _out.WriteLine(answer.Text);
        Assert.Contains("<code>" + filed.Id.ToString(CultureInfo.InvariantCulture) + "</code>", answer.Text!, StringComparison.Ordinal);
        Assert.Contains("message 71", answer.Text!, StringComparison.Ordinal);
        Assert.Contains("from Ada Lovelace (@ada_l)", answer.Text!, StringComparison.Ordinal);
        Assert.Contains("Filed against <b>PK51</b>", answer.Text!, StringComparison.Ordinal);
        Assert.Contains(NotePromoter.Callback(StateHome.SlugFor(_repo, "PK51"), filed.Id), answer.ReplyMarkup!, StringComparison.Ordinal);

        // A /note is a question, not a note: nothing more is filed and it gets no reaction of its own.
        Assert.Single(Inbox().All());
        Assert.Single(bot.Snapshot(), c => c.Method == "setMessageReaction");

        // Pointed at a message that is not a note, it says so and offers no button.
        bot.QueueMessage(Json(new
        {
            message_id = 73, chat = AdminChatRef, text = "/note",
            reply_to_message = new { message_id = 5, chat = AdminChatRef, text = "a push" },
        }));
        await courier.PollOnceAsync(CancellationToken.None);
        var miss = bot.Snapshot().Last(c => c.Method == "sendMessage");
        Assert.Contains("not a note this courier filed", miss.Text!, StringComparison.Ordinal);
        Assert.Null(miss.ReplyMarkup);
    }

    // -- D9: say --reply-to takes a note id or a message id ------------------------------------------

    private async Task<(int Exit, string Output)> SayDryRun(long replyTo, string? to = null)
    {
        new CourierSettings
        {
            Chats = [new CourierChat(AdminChat, "admin"), new CourierChat(OtherChat, "observer")],
            Projects = [new CourierProject("PK51", _repo)],
        }.Save(_stateHome);

        var settings = new SayCommand.Settings { To = to, Text = "answered", ReplyTo = replyTo, DryRun = true };
        await using var output = new StringWriter();
        using var http = new HttpClient();
        var exit = await SayCommand.RunAsync(settings, output, _stateHome, http, token: null, CancellationToken.None);
        _out.WriteLine(output.ToString());
        return (exit, output.ToString());
    }

    [Fact]
    public async Task Say_reply_to_a_note_id_answers_that_notes_message_in_the_chat_it_came_from()
    {
        Assert.True(Inbox().Append(new InboxNote(83806501, DateTime.UtcNow, OtherChat, InboxNote.TextKind, "from the other room",
            MessageId: 652, SenderId: 5550001, SenderName: "Ada Lovelace", SenderUsername: "ada_l")));

        var (exit, output) = await SayDryRun(83806501);

        Assert.Equal(0, exit);
        Assert.Contains($"chat:       {OtherChat}", output, StringComparison.Ordinal);
        Assert.Contains($"reply to:   note 83806501 of PK51: message 652 in chat {OtherChat}, from Ada Lovelace (@ada_l)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Say_reply_to_a_number_that_is_no_note_is_a_message_id_as_before()
    {
        var (exit, output) = await SayDryRun(12);

        Assert.Equal(0, exit);
        Assert.Contains($"chat:       admin -> {AdminChat}", output, StringComparison.Ordinal);
        Assert.Contains("reply to:   12" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Say_reply_to_an_old_note_or_to_a_note_of_another_chat_is_refused_by_name()
    {
        Assert.True(Inbox().Append(new InboxNote(83806400, DateTime.UtcNow, AdminChat, InboxNote.TextKind, "an old note")));
        Assert.True(Inbox().Append(new InboxNote(83806502, DateTime.UtcNow, AdminChat, InboxNote.TextKind, "a new note", MessageId: 660)));

        var (oldExit, oldOutput) = await SayDryRun(83806400);
        Assert.Equal(2, oldExit);
        Assert.Contains("filed before notes carried a message id", oldOutput, StringComparison.Ordinal);

        var (otherExit, otherOutput) = await SayDryRun(83806502, to: "observer");
        Assert.Equal(2, otherExit);
        Assert.Contains("drop --to to answer it where it was said", otherOutput, StringComparison.Ordinal);

        var (sameExit, sameOutput) = await SayDryRun(83806502, to: "admin");
        Assert.Equal(0, sameExit);
        Assert.Contains("message 660 in chat " + AdminChat, sameOutput, StringComparison.Ordinal);
    }
}
