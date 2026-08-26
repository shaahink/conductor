using System.Globalization;

using Conductor.Core.Courier;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV4.1 / findings §1.4-B and §6.2 — the courier, driven end to end against a loopback stand-in for
/// api.telegram.org.
///
/// <para>Everything here runs on a scratch state home under the temp directory with a scratch token
/// and scratch chat ids: no proof in this file touches the operator's real inbox, real chat routes,
/// real dead-letter box or real bot.</para>
///
/// <para>The headline is <see cref="A_kill_between_receive_and_acknowledge_files_the_note_exactly_once"/>.
/// It is the one falsifiable exit the checkpoint names, and it is asserted the way it is written: the
/// courier receives an update, files it, and dies before the sender is answered; a second courier
/// starts on the same state home, is re-served the same update because nothing confirmed it, and the
/// note is on disk exactly once.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DV4_1CourierTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ObserverChat = "-1002220002";
    private const string ScratchToken = "111111:dv41-scratch-token";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _repo;
    private readonly ITestOutputHelper _out;

    public DV4_1CourierTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-dv41-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        _repo = Path.Combine(_tmp, "alpha-repo");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# dv41 rig\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    // ── the rig ─────────────────────────────────────────────────────────────────────────────

    private CourierSettings Settings(RecordingBotApi bot, params string[] repos)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = bot.Root,
            PollIntervalSeconds = 1,
            Chats = [new CourierChat(AdminChat, "admin"), new CourierChat(ObserverChat, "observer")],
            Projects = [.. repos.Select(r => new CourierProject(Path.GetFileName(r), r))],
        };
        settings.Save(_stateHome);
        return settings;
    }

    private TelegramCourierSource Source(CourierSettings settings) =>
        new(settings, ScratchToken, NullLogger.Instance, _stateHome);

    private CourierDaemon Daemon(ICourierSource source, CourierSettings settings) =>
        new(source, settings, _stateHome, m => _out.WriteLine(m));

    private static void Send(RecordingBotApi bot, string messageJson) =>
        bot.QueueMessage(messageJson.Replace("ADMIN", AdminChat, StringComparison.Ordinal)
                                    .Replace("OBSERVER", ObserverChat, StringComparison.Ordinal)
                                    .Replace("\r\n", "", StringComparison.Ordinal)
                                    .Replace("\n", "", StringComparison.Ordinal));

    private InboxStore Inbox(string repo) => new(Path.Combine(repo, ".conductor"));

    private static byte[] Bytes(int n, byte fill)
    {
        var b = new byte[n];
        Array.Fill(b, fill);
        return b;
    }

    // ── routing to an allowlisted project ───────────────────────────────────────────────────

    /// <summary>The ordinary day: choose a project once, then speak. The note lands in that project's
    /// own inbox with its audio beside it — in a process that has no run, no plan and no engine.</summary>
    [Fact]
    public async Task A_voice_note_is_filed_into_the_project_the_chat_selected()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot, _repo);
        using var source = Source(settings);
        var courier = Daemon(source, settings);

        Send(bot, """{"message_id":1,"chat":{"id":ADMIN},"text":"/project alpha-repo"}""");
        bot.AddFile("voice-1", "voice/file_1.oga", Bytes(2_048, 0x4F));
        Send(bot, """
            {"message_id":2,"chat":{"id":ADMIN},
             "voice":{"file_id":"voice-1","file_unique_id":"u1","duration":9,
                      "mime_type":"audio/ogg","file_size":2048}}
            """);

        var tick = await courier.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, tick.Received);
        Assert.Equal(1, tick.Filed);
        Assert.Equal(0, tick.Parked);

        var notes = Inbox(_repo).All();
        var note = Assert.Single(notes);
        Assert.Equal("voice", note.Kind);
        Assert.NotNull(note.MediaPath);
        Assert.True(File.Exists(Path.Combine(Inbox(_repo).Dir,
            note.MediaPath!.Replace('/', Path.DirectorySeparatorChar))),
            "the audio should have been adopted into the project's own inbox");

        var replies = bot.Snapshot().Where(c => c.Method == "sendMessage").ToList();
        Assert.Contains(replies, r => r.Text!.Contains("now file against", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text!.Contains("Filed against", StringComparison.Ordinal));
    }

    // ── THE falsifiable exit ────────────────────────────────────────────────────────────────

    /// <summary>Findings §6.2, as the checkpoint words it: kill the courier between receive and
    /// acknowledge, restart it, and the note files exactly once.
    ///
    /// <para>The kill is simulated at the worst possible instant — after the note is on disk and
    /// before the sender is answered — by a source whose reply throws. Nothing catches it, so the
    /// offset is never written, which is precisely the state a killed process leaves behind. The stub
    /// then re-serves the unconfirmed update to the second courier exactly as api.telegram.org
    /// would, and the assertion is on what is on the disk: ONE note file, ONE index line, and a poll
    /// that reports the delivery as a duplicate rather than filing it again.</para></summary>
    [Fact]
    public async Task A_kill_between_receive_and_acknowledge_files_the_note_exactly_once()
    {
        using var bot = new RecordingBotApi { HonourOffset = true };
        var settings = Settings(bot, _repo);
        new ChatRoutes(_stateHome).Set(AdminChat, null, StateHome.SlugFor(_repo, Path.GetFileName(_repo)));

        bot.AddFile("voice-9", "voice/file_9.oga", Bytes(1_024, 0x4F));
        Send(bot, """
            {"message_id":77,"chat":{"id":ADMIN},"caption":"the one that must not double",
             "voice":{"file_id":"voice-9","file_unique_id":"u9","duration":5,
                      "mime_type":"audio/ogg","file_size":1024}}
            """);

        // ── first courier: receives, files, and dies before it can answer ──
        using (var dying = Source(settings))
        {
            var killed = Daemon(new KilledOnReply(dying), settings);
            await Assert.ThrowsAsync<KilledException>(() => killed.PollOnceAsync(CancellationToken.None));
        }

        var afterKill = Inbox(_repo).All();
        Assert.Single(afterKill);
        Assert.Equal(0, new CourierOffset(_stateHome).Read());   // nothing was confirmed

        // ── second courier: same state home, same bot, nothing confirmed, so it is served again ──
        using var restarted = Source(settings);
        var courier = Daemon(restarted, settings);
        var tick = await courier.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, tick.Received);
        Assert.Equal(1, tick.Duplicates);
        Assert.Equal(0, tick.Filed);
        Assert.Equal(0, tick.Parked);

        var notes = Inbox(_repo).All();
        Assert.Single(notes);
        // Filed under the DELIVERY id, not the message id: the stub assigned update_id 1 to the
        // only queued message, and that is the key InboxStore.Append refuses to overwrite.
        Assert.Equal(1, notes[0].Id);

        var indexLines = InboxStore.ReadLinesShared(Inbox(_repo).IndexPath)
            .Where(l => l.Trim().Length > 0).ToList();
        Assert.Single(indexLines);

        // The media directory too. The first version of this passed every assertion above and still
        // left an ORPHAN copy of the audio here: the replay adopted the file into the inbox before
        // Append could refuse the note, and prune deletes the files a note NAMES, so nothing would
        // ever have removed it. Found by the live proof, not by this test — hence this line.
        var media = Directory.GetFiles(Path.Combine(Inbox(_repo).Dir, "media"));
        Assert.Single(media);

        // The offset moved this time, so a THIRD courier is served nothing at all.
        Assert.True(new CourierOffset(_stateHome).Read() > 0, "the offset should have advanced");
        var third = await Daemon(restarted, settings).PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, third.Received);

        // And the owner was told once, not twice: the duplicate is answered with silence.
        var filed = bot.Snapshot()
            .Count(c => c.Method == "sendMessage" && c.Text!.Contains("Filed against", StringComparison.Ordinal));
        Assert.Equal(0, filed);   // the ONLY "filed" reply was the one the kill prevented
    }

    /// <summary>The other half of the same rule: on a clean poll the offset IS written, and it is
    /// written past the delivery — so nothing is re-fetched and nothing is skipped.</summary>
    [Fact]
    public async Task The_offset_is_durable_and_advances_past_the_delivery_it_handled()
    {
        using var bot = new RecordingBotApi { HonourOffset = true };
        var settings = Settings(bot, _repo);
        using var source = Source(settings);
        var courier = Daemon(source, settings);

        Send(bot, """{"message_id":5,"chat":{"id":ADMIN},"text":"/project alpha-repo"}""");
        await courier.PollOnceAsync(CancellationToken.None);

        var offset = new CourierOffset(_stateHome);
        Assert.True(File.Exists(offset.Path_), "the offset must survive the process, not the poll loop");
        Assert.Equal(2, offset.Read());   // the stub assigns update_id 1; the courier confirms through 2

        // A brand new CourierOffset object reads the same number off the disk — the point of the file.
        Assert.Equal(2, new CourierOffset(_stateHome).Read());

        // And it is written in the same camelCase courier.json beside it uses, readable either way.
        // This was PascalCase until DV4.1's live proof: a hand-edited `{"offset": 400}` deserialised
        // to 0 with no error, because System.Text.Json matches property names case-sensitively.
        var raw = await File.ReadAllTextAsync(offset.Path_);
        Assert.Contains("\"offset\"", raw, StringComparison.Ordinal);
        await File.WriteAllTextAsync(offset.Path_, """{"offset": 400, "updatedUtc": "2026-01-01T00:00:00Z"}""");
        Assert.Equal(400, new CourierOffset(_stateHome).Read());
    }

    // ── the explicit allowlist ──────────────────────────────────────────────────────────────

    /// <summary>The decision this checkpoint records: the courier's projects are an EXPLICIT list, not
    /// the state catalogue. A project this machine has genuinely run — catalogued, with a real
    /// checkout — is invisible to a courier that was not told about it, and a note that names it is
    /// refused by name and PARKED rather than filed somewhere close.</summary>
    [Fact]
    public async Task A_catalogued_project_that_is_not_on_the_allowlist_is_refused_and_parked()
    {
        var other = Path.Combine(_tmp, "beta-repo");
        Directory.CreateDirectory(Path.Combine(other, ".conductor"));
        StateCatalogue.Upsert(_stateHome, other, "beta", Path.Combine(_stateHome, "runs", "beta", "run.db"));

        using var bot = new RecordingBotApi();
        var settings = Settings(bot, _repo);          // alpha only — beta is catalogued, not allowed
        using var source = Source(settings);
        var courier = Daemon(source, settings);

        Send(bot, """{"message_id":11,"chat":{"id":ADMIN},"text":"/project beta"}""");
        Send(bot, """
            {"message_id":12,"chat":{"id":ADMIN},"text":"beta should never see this",
             "reply_to_message":{"message_id":9,"text":"beta \u00b7 s4\nstage BE1 done"}}
            """);

        var tick = await courier.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, tick.Parked);
        Assert.Equal(0, tick.Filed);
        Assert.Empty(Inbox(other).All());
        Assert.Empty(Inbox(_repo).All());

        var parked = new DeadLetterBox(_stateHome).All();
        Assert.Single(parked);
        Assert.Contains("beta should never see this",
            await File.ReadAllTextAsync(parked[0]), StringComparison.Ordinal);

        var replies = bot.Snapshot().Where(c => c.Method == "sendMessage").Select(c => c.Text!).ToList();
        Assert.Contains(replies, t => t.Contains("no project called", StringComparison.Ordinal)
                                   || t.Contains("not a project on this machine", StringComparison.Ordinal));
        Assert.Contains(replies, t => t.Contains("Kept, not filed", StringComparison.Ordinal));
    }

    /// <summary>The exactly-once rule has to hold on the path that CANNOT use the inbox's dedup. A
    /// parked note is not in any inbox, and its filename carries the arrival instant — so a replay
    /// after a kill would have parked a second copy of the one note nobody could file. That is the
    /// worst possible note to duplicate, which is why it is asserted rather than assumed.</summary>
    [Fact]
    public async Task A_kill_on_the_parked_path_parks_the_note_exactly_once()
    {
        using var bot = new RecordingBotApi { HonourOffset = true };
        var settings = Settings(bot, _repo);   // nothing selected for this chat, so nothing routes

        Send(bot, """{"message_id":88,"chat":{"id":ADMIN},"text":"nowhere to put this"}""");

        using (var dying = Source(settings))
        {
            var killed = Daemon(new KilledOnReply(dying), settings);
            await Assert.ThrowsAsync<KilledException>(() => killed.PollOnceAsync(CancellationToken.None));
        }
        Assert.Single(new DeadLetterBox(_stateHome).All());
        Assert.Equal(0, new CourierOffset(_stateHome).Read());

        using var restarted = Source(settings);
        var tick = await Daemon(restarted, settings).PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, tick.Received);
        Assert.Single(new DeadLetterBox(_stateHome).All());
    }

    /// <summary>Same claim, stated against the type that enforces it, so a future refactor that
    /// quietly restores the catalogue fallback fails here rather than in a rig.</summary>
    [Fact]
    public void An_explicit_project_list_replaces_the_catalogue_entirely()
    {
        var other = Path.Combine(_tmp, "gamma-repo");
        Directory.CreateDirectory(other);
        StateCatalogue.Upsert(_stateHome, other, "gamma", Path.Combine(_stateHome, "runs", "gamma", "run.db"));

        var allowed = new CourierSettings { Projects = [new CourierProject("alpha", _repo)] }.Allowed();
        var directory = new ProjectDirectory(_stateHome, local: null, only: allowed);

        Assert.Single(directory.All());
        Assert.Null(directory.Resolve("gamma").Project);
        Assert.NotNull(directory.Resolve("alpha").Project);

        // The catalogue really does hold it — the point is that the courier's directory does not.
        Assert.Contains(StateCatalogue.Read(_stateHome), e => e.Plan == "gamma");
        Assert.NotNull(new ProjectDirectory(_stateHome).Resolve("gamma").Project);
    }

    // ── who may talk to it ──────────────────────────────────────────────────────────────────

    /// <summary>An unlisted chat gets silence — but its update is still acknowledged, or the courier
    /// re-fetches the same stranger's message every poll for the next 24 hours.</summary>
    [Fact]
    public async Task An_unlisted_chat_is_answered_with_silence_and_still_acknowledged()
    {
        using var bot = new RecordingBotApi { HonourOffset = true };
        var settings = Settings(bot, _repo);
        using var source = Source(settings);
        var courier = Daemon(source, settings);

        Send(bot, """{"message_id":30,"chat":{"id":-1009999999},"text":"hello?"}""");
        var tick = await courier.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, tick.Received);
        Assert.Equal(0, tick.Filed);
        Assert.DoesNotContain(bot.Snapshot(), c => c.Method == "sendMessage");
        Assert.True(new CourierOffset(_stateHome).Read() > 0);

        var again = await courier.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, again.Received);
    }

    /// <summary>An observer may read a run, not write to one — and nothing is downloaded on their
    /// behalf, so an unauthorised sender cannot put bytes on this machine by sending them.</summary>
    [Fact]
    public async Task An_observer_may_not_file_and_no_bytes_are_fetched()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot, _repo);
        using var source = Source(settings);
        var courier = Daemon(source, settings);

        bot.AddFile("doc-1", "documents/file_1.pdf", Bytes(512, 0x25));
        Send(bot, """
            {"message_id":40,"chat":{"id":OBSERVER},
             "document":{"file_id":"doc-1","file_name":"budget.pdf",
                         "mime_type":"application/pdf","file_size":512}}
            """);

        var tick = await courier.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, tick.Filed);
        Assert.Equal(0, bot.GetFileCalls);
        Assert.Empty(Inbox(_repo).All());
        Assert.Contains(bot.Snapshot(),
            c => c.Method == "sendMessage" && c.Text!.Contains("may read the run, not file",
                StringComparison.Ordinal));
    }

    // ── the wire's own small rules ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/project alpha", "project alpha")]
    [InlineData("/project@conductor_bot alpha", "project alpha")]
    [InlineData("/project@conductor_bot", "project")]
    [InlineData("/project", "project")]
    [InlineData("plain words", null)]
    [InlineData("", null)]
    [InlineData("/", null)]
    public void A_slash_command_is_read_without_its_at_botname_suffix(string text, string? expected) =>
        Assert.Equal(expected, TelegramCourierSource.CommandIn(text, null));

    /// <summary>A caption is what the sender said ABOUT a file, never an instruction. Reading it as
    /// one is how a voice note captioned "/project is wrong" reconfigures the machine instead of
    /// being filed.</summary>
    [Fact]
    public void A_caption_on_a_file_is_never_read_as_a_command()
    {
        var media = new InboundMedia(InboundMediaKind.Voice, "f", "voice note", "audio/ogg", 10, 3,
            "C:\\nowhere\\voice.oga", null);
        Assert.Null(TelegramCourierSource.CommandIn("/project alpha", media));
    }

    // ── the 24-hour limit, stated where a person will read it ───────────────────────────────

    /// <summary>Findings §6.3 asks for the retention limit to be stated in the courier's docs. A
    /// design document nobody opens is not where it belongs, so this pins it in the shipped CLI
    /// reference AND in the sentence the verb itself prints.</summary>
    [Fact]
    public void The_twenty_four_hour_retention_limit_is_stated_in_the_docs_and_by_the_verb()
    {
        var cli = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "cli.md"));
        var courierSection = cli[cli.IndexOf("## The courier", StringComparison.Ordinal)..];
        Assert.Contains("24 hours", courierSection, StringComparison.Ordinal);
        Assert.Contains("no run live", courierSection, StringComparison.Ordinal);

        Assert.Contains("24 hours", Conductor.Commands.CourierCommand.RetentionNotice, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>A courier that dies the instant before it answers. Everything up to that point is the
    /// REAL source — the real getUpdates, the real getFile, the real download — so what the first
    /// process left on disk is what a killed courier really leaves on disk.</summary>
    private sealed class KilledOnReply(ICourierSource inner) : ICourierSource
    {
        public string Describe => inner.Describe;

        public Task<IReadOnlyList<CourierDelivery>> FetchAsync(long offset, CancellationToken ct) =>
            inner.FetchAsync(offset, ct);

        public Task ReplyAsync(string chatId, string text, long? threadId, CancellationToken ct,
            IReadOnlyList<CourierButton>? buttons = null) =>
            throw new KilledException();

        public Task<string?> SendAsync(CourierPush push, CancellationToken ct) =>
            throw new KilledException();
    }

    private sealed class KilledException : Exception
    {
        public KilledException() : base("the courier process died here") { }
        public KilledException(string message) : base(message) { }
        public KilledException(string message, Exception innerException) : base(message, innerException) { }
    }
}
