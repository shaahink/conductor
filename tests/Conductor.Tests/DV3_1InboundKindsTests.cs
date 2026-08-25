using System.Globalization;

using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV3.1 on the WIRE — every inbound message kind the owner's phone can actually send, driven
/// through a real <see cref="TelegramService"/> long-polling a loopback stand-in for
/// api.telegram.org.
///
/// <para>The defect these pin is not a wrong answer, it is NO answer: <c>TgMessage</c> carried
/// <c>message_id</c>, <c>text</c> and <c>chat</c>, so a voice note was invisible — not refused, not
/// logged, not acknowledged (findings §1.2 gap 2). Each test below sends one real-shaped update and
/// asserts two things: what landed on this machine, and what the sender was told.</para>
///
/// <para>Scratch token, scratch chat ids, a stub at <see cref="TelegramConfig.ApiBaseUrl"/> and a
/// temp repo per test: no proof here touches a real bot, a real chat or this repo's state.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DV3_1InboundKindsTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ObserverChat = "-1002220002";

    private readonly string _repo;
    private readonly string _stateDir;
    private readonly ITestOutputHelper _out;

    public DV3_1InboundKindsTests(ITestOutputHelper output)
    {
        _out = output;
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-dv31-{Guid.NewGuid():N}", "inbox-rig");
        _stateDir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# DV3.1 rig\n");
        SecretsStore.WriteTelegramToken(_stateDir, "111111:dv31-scratch-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    private string MediaDir => Path.Combine(_stateDir, "inbox", "media");

    // ── the four kinds, each fetched and acknowledged ──

    /// <summary>The headline payload of the whole era: a voice note. It is fetched through getFile,
    /// the OGG bytes land on disk, and the sender is told what arrived and how long it was.</summary>
    [Fact]
    public async Task A_voice_note_is_downloaded_and_acknowledged_by_kind()
    {
        using var bot = new RecordingBotApi();
        var audio = Bytes(3_000, 0x4F);            // "OggS"-ish; the assertion is the byte count
        bot.AddFile("voice-1", "voice/file_7.oga", audio);
        Send(bot, """
            {"message_id":501,"chat":{"id":ADMIN,"type":"private"},
             "voice":{"file_id":"voice-1","file_unique_id":"u1","duration":42,
                      "mime_type":"audio/ogg","file_size":3000}}
            """);

        var (reply, log) = await DriveAsync(bot);

        var saved = Path.Combine(MediaDir, "501-voice.oga");
        Assert.True(File.Exists(saved), $"expected {saved}; found: {Found()}");
        Assert.Equal(audio, await File.ReadAllBytesAsync(saved));

        Assert.Equal(AdminChat, reply.ChatId);
        Assert.Contains("Voice note received", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("42s", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("2.9 KB", reply.Text!, StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains("inbound note: voice", StringComparison.Ordinal));
    }

    /// <summary>An audio FILE is not a voice note — different property on the wire, different noun
    /// in the answer — and it keeps the name its sender gave it.</summary>
    [Fact]
    public async Task An_audio_file_keeps_its_own_name_and_is_not_called_a_voice_note()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("audio-1", "music/file_2.mp3", Bytes(2_048, 0x49));
        Send(bot, """
            {"message_id":502,"chat":{"id":ADMIN},
             "audio":{"file_id":"audio-1","duration":12,"file_name":"standup.mp3",
                      "mime_type":"audio/mpeg","file_size":2048}}
            """);

        var (reply, _) = await DriveAsync(bot);

        Assert.True(File.Exists(Path.Combine(MediaDir, "502-standup.mp3")), Found());
        Assert.Contains("Audio received", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("standup.mp3", reply.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("Voice note", reply.Text!, StringComparison.Ordinal);
    }

    /// <summary>A document, with a CAPTION — which is where the words are when the payload is a
    /// file, and which <c>TgMessage</c> had no field for at all.</summary>
    [Fact]
    public async Task A_document_lands_on_disk_and_its_caption_is_the_notes_text()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("doc-1", "documents/file_9.pdf", Bytes(4_096, 0x25));
        Send(bot, """
            {"message_id":503,"chat":{"id":ADMIN},
             "caption":"the acceptance for DV3.2, read this first",
             "document":{"file_id":"doc-1","file_name":"acceptance.pdf",
                         "mime_type":"application/pdf","file_size":4096}}
            """);

        var (reply, log) = await DriveAsync(bot);

        Assert.True(File.Exists(Path.Combine(MediaDir, "503-acceptance.pdf")), Found());
        Assert.Contains("Document received", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("acceptance.pdf", reply.Text!, StringComparison.Ordinal);
        // The caption came back with the acknowledgement, so the sender can see it was read.
        Assert.Contains("read this first", reply.Text!, StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains("text 41 chars", StringComparison.Ordinal));
    }

    /// <summary>A photo arrives as the same image at several resolutions. The engine takes the
    /// LARGEST — the thumbnail is not what the sender meant to show anyone.</summary>
    [Fact]
    public async Task A_photo_is_fetched_at_the_largest_size_offered()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("photo-small", "photos/file_1.jpg", Bytes(120, 0x11));
        bot.AddFile("photo-big", "photos/file_3.jpg", Bytes(9_000, 0x22));
        Send(bot, """
            {"message_id":504,"chat":{"id":ADMIN},
             "photo":[{"file_id":"photo-small","file_unique_id":"s","width":90,"height":60,"file_size":120},
                      {"file_id":"photo-big","file_unique_id":"b","width":1280,"height":853,"file_size":9000}]}
            """);

        var (reply, _) = await DriveAsync(bot);

        var saved = Path.Combine(MediaDir, "504-photo.jpg");
        Assert.True(File.Exists(saved), Found());
        Assert.Equal(9_000, new FileInfo(saved).Length);
        Assert.Contains("Photo received", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("8.8 KB", reply.Text!, StringComparison.Ordinal);
    }

    // ── the two routing hints DV3.4 will need, carried off the wire ──

    /// <summary>Reply-to and forum-topic: the zero-typing routing mechanisms (findings §1.5). DV3.4
    /// turns them into a project; DV3.1's job is that they arrive at all, and they are named in the
    /// log line so a routing decision is debuggable before it exists.</summary>
    [Fact]
    public async Task A_reply_to_a_push_and_a_forum_topic_both_survive_the_wire()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("voice-2", "voice/file_8.oga", Bytes(500, 0x4F));
        Send(bot, """
            {"message_id":505,"chat":{"id":ADMIN,"type":"supergroup"},
             "message_thread_id":77,
             "reply_to_message":{"message_id":4242,"chat":{"id":ADMIN},
                                 "text":"Session 5 ended - DV2.4 CLAIMED"},
             "voice":{"file_id":"voice-2","duration":8,"mime_type":"audio/ogg","file_size":500}}
            """);

        var (_, log) = await DriveAsync(bot);

        var line = Assert.Single(log, l => l.Contains("inbound note:", StringComparison.Ordinal));
        _out.WriteLine(line);
        Assert.Contains("reply to 4242", line, StringComparison.Ordinal);
        Assert.Contains("topic 77", line, StringComparison.Ordinal);
    }

    // ── the 20 MB cap, refused BY NAME ──

    /// <summary>The Bot API will not serve a bot a file over 20 MB. The message declares the size,
    /// so the refusal happens before the round trip — and it NAMES the file, the size and the cap,
    /// because "couldn't do that" is the silent drop with an apology stapled to it.</summary>
    [Fact]
    public async Task A_file_over_the_twenty_megabyte_cap_is_refused_by_name_before_any_fetch()
    {
        using var bot = new RecordingBotApi();
        Send(bot, """
            {"message_id":506,"chat":{"id":ADMIN},
             "document":{"file_id":"huge-1","file_name":"walkthrough.mp4",
                         "mime_type":"video/mp4","file_size":26214400}}
            """);

        var (reply, _) = await DriveAsync(bot);

        Assert.Contains("walkthrough.mp4", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("25 MB", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("20 MB", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("Your message was kept", reply.Text!, StringComparison.Ordinal);

        // Not dropped, and not attempted: the sender heard about it and the API was never asked.
        Assert.Equal(0, bot.GetFileCalls);
        Assert.False(Directory.Exists(MediaDir), Found());
    }

    /// <summary>The other half of the same rule: a message that declares NO size, and a getFile that
    /// answers "file is too big". Telegram's own sentence comes back with the cap spelled out.</summary>
    [Fact]
    public async Task A_file_the_api_itself_refuses_still_names_the_file_and_the_cap()
    {
        using var bot = new RecordingBotApi();
        bot.RefuseFile("huge-2", "Bad Request: file is too big");
        Send(bot, """
            {"message_id":507,"chat":{"id":ADMIN},
             "document":{"file_id":"huge-2","file_name":"dump.zip","mime_type":"application/zip"}}
            """);

        var (reply, _) = await DriveAsync(bot);

        Assert.Contains("dump.zip", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("file is too big", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("20 MB", reply.Text!, StringComparison.Ordinal);
        Assert.Equal(1, bot.GetFileCalls);
        Assert.False(File.Exists(Path.Combine(MediaDir, "507-dump.zip")));
    }

    // ── the boundary: who may file, and what a sender-supplied name may do ──

    /// <summary>Findings §1.8 — inbound text becomes agent-prompt text, so filing is admin-only. An
    /// observer is told so BY NAME, and nothing of theirs is fetched: the fetch gate is in front of
    /// the download, not behind it, so an unauthorised sender cannot put bytes on this machine.</summary>
    [Fact]
    public async Task An_observer_may_not_file_and_nothing_of_theirs_is_downloaded()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("voice-3", "voice/file_4.oga", Bytes(700, 0x4F));
        Send(bot, """
            {"message_id":508,"chat":{"id":OBSERVER},
             "voice":{"file_id":"voice-3","duration":5,"mime_type":"audio/ogg","file_size":700}}
            """);

        var (reply, _) = await DriveAsync(bot);

        Assert.Equal(ObserverChat, reply.ChatId);
        Assert.Contains("observer", reply.Text!, StringComparison.Ordinal);
        Assert.Contains("has to come from an admin chat", reply.Text!, StringComparison.Ordinal);
        Assert.Equal(0, bot.GetFileCalls);
        Assert.False(Directory.Exists(MediaDir), Found());
    }

    /// <summary><c>file_name</c> is whatever the SENDER typed. A name that is a path traversal must
    /// become a harmless leaf, not a write outside the media directory.</summary>
    [Fact]
    public async Task A_document_name_that_is_a_traversal_cannot_escape_the_media_directory()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("evil-1", "documents/file_11.bin", Bytes(64, 0x66));
        Send(bot, """
            {"message_id":509,"chat":{"id":ADMIN},
             "document":{"file_id":"evil-1","file_name":"../../../../plan.json",
                         "mime_type":"application/json","file_size":64}}
            """);

        await DriveAsync(bot);

        var written = Directory.GetFiles(MediaDir);
        var one = Assert.Single(written);
        _out.WriteLine("stored as: " + one);
        Assert.Equal(MediaDir, Path.GetDirectoryName(one));
        Assert.Equal("509-plan.json", Path.GetFileName(one));
        // The traversal target: four levels up from the media dir is the temp root.
        Assert.False(File.Exists(Path.Combine(_repo, "plan.json")));
        Assert.False(File.Exists(Path.Combine(_stateDir, "plan.json")));
    }

    // ── the two things that must NOT have moved ──

    /// <summary>KS11.1's golden-replay standard: text is text and takes the path it always took. The
    /// media route is reached only when a message carries a file.</summary>
    [Fact]
    public async Task Plain_text_still_takes_the_old_command_path()
    {
        using var bot = new RecordingBotApi();
        bot.QueueCommand(AdminChat, "/abort");

        var (reply, log) = await DriveAsync(bot);

        Assert.Contains("Confirm abort?", reply.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain(log, l => l.Contains("inbound note:", StringComparison.Ordinal));
        Assert.Equal(0, bot.GetFileCalls);
    }

    /// <summary>A note is a RECORD, not a control verb (findings §1.7), so it files on a plan with
    /// two-way control switched OFF. If filing needed <c>enableTwoWay</c> the safest plans would be
    /// the ones that could not receive feedback.</summary>
    [Fact]
    public async Task A_note_files_even_with_two_way_control_switched_off()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("voice-4", "voice/file_5.oga", Bytes(900, 0x4F));
        Send(bot, """
            {"message_id":510,"chat":{"id":ADMIN},
             "voice":{"file_id":"voice-4","duration":9,"mime_type":"audio/ogg","file_size":900}}
            """);

        var (reply, _) = await DriveAsync(bot, twoWay: false);

        Assert.True(File.Exists(Path.Combine(MediaDir, "510-voice.oga")), Found());
        Assert.Contains("Voice note received", reply.Text!, StringComparison.Ordinal);
    }

    /// <summary>Findings §6.1 — the inbox is written under <c>.conductor</c>, which this repo's own
    /// gitignore denies by default. The trap this pins is a future session "fixing" the invisible
    /// directory by allowlisting it: this repo is PUBLIC, and that push ships the owner's voice
    /// notes to the world.</summary>
    [Fact]
    public void The_repos_conductor_gitignore_has_no_allowlist_entry_for_the_inbox()
    {
        var ignore = Path.Combine(RepoRoot(), ".conductor", ".gitignore");
        Assert.True(File.Exists(ignore), ignore);
        var lines = File.ReadAllLines(ignore);
        _out.WriteLine(string.Join("\n", lines));

        Assert.Contains(lines, l => l.Trim() == "*");
        Assert.DoesNotContain(lines, l => l.Contains("inbox", StringComparison.OrdinalIgnoreCase));
    }

    // ── the rig ──

    /// <summary>Starts the service, waits for the one reply the stub's long-poll provokes, and stops
    /// it. Returns the reply and the engine's log lines.</summary>
    private async Task<(BotCall Reply, List<string> Log)> DriveAsync(RecordingBotApi bot, bool twoWay = true)
    {
        var log = new CapturingLogger();
        using var svc = new TelegramService(Plan(bot.Root, twoWay), new RunState { RunId = "dv31", SessionCounter = 6 }, log);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && bot.Snapshot().Count == 0)
            await Task.Delay(50);

        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var calls = bot.Snapshot();
        foreach (var c in calls) _out.WriteLine(c.Describe());
        return (Assert.Single(calls), log.Lines);
    }

    private PlanConfig Plan(string apiRoot, bool twoWay) => new()
    {
        Name = "Divan",
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "DV3", Title = "The inbox", Sessions = 1 } },
        Telegram = new TelegramConfig
        {
            PollIntervalSeconds = 1,
            ApiBaseUrl = apiRoot,
            EnableTwoWay = twoWay,
            Chats =
            {
                new TelegramChatEntry { ChatId = AdminChat, Profile = "admin" },
                new TelegramChatEntry { ChatId = ObserverChat, Profile = "observer" },
            },
        },
    };

    /// <summary>Queues one message body, with the chat placeholders substituted. The bodies are
    /// plain raw strings rather than interpolated ones because JSON ends in runs of closing braces
    /// and an interpolated raw string reads those as its own.</summary>
    private static void Send(RecordingBotApi bot, string messageJson) =>
        bot.QueueMessage(messageJson
            .Replace("ADMIN", AdminChat, StringComparison.Ordinal)
            .Replace("OBSERVER", ObserverChat, StringComparison.Ordinal));

    private static byte[] Bytes(int count, byte fill)
    {
        var b = new byte[count];
        Array.Fill(b, fill);
        return b;
    }

    private string Found() => Directory.Exists(MediaDir)
        ? "media dir holds: " + string.Join(", ", Directory.GetFiles(MediaDir).Select(Path.GetFileName))
        : "media dir does not exist: " + MediaDir;

    /// <summary>This repo's own root, walked up from the test binary — the gitignore assertion is
    /// about THIS repository's privacy posture, not about the temp rig.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repo root above " + AppContext.BaseDirectory);
    }

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
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_gate) _lines.Add(formatter(state, exception));
        }
    }
}
