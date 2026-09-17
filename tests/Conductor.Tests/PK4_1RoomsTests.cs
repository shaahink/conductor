using Conductor.Commands;
using Conductor.Core.Courier;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>PK4.1 / D6 — rooms: one file per project in the courier home, found by repo then by folder
/// name, falling back to the plan's chats and then the courier's; imported once from the old private
/// home with the voice pointed at, never copied; and a verb whose output never carries a chat id.
///
/// <para>Every voice here is a fixture written by this class. No test reads the owner's
/// <c>~/.claude/telegram</c>: <c>--from</c> is always a temp directory.</para></summary>
public sealed class PK4_1RoomsTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string GroupChat = "-1009876543210";

    /// <summary>A line only the fixture voice carries. If it shows up in a room file, the voice was
    /// copied rather than pointed at.</summary>
    private const string VoiceMarker = "FIXTURE-VOICE pk41 the people in this room are imaginary";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _oldHome;

    public PK4_1RoomsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-pk41-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        _oldHome = Path.Combine(_tmp, "telegram");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(_oldHome);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    // -- the store -------------------------------------------------------------------------------

    [Theory]
    [InlineData("BookToCourse", "booktocourse")]
    [InlineData("cv", "cv")]
    [InlineData("  Takhteh Feel!  ", "takhteh-feel")]
    [InlineData("a__b--c", "a-b-c")]
    [InlineData("-._-", "")]
    public void TheSlugIsTheNameFoldedToLettersDigitsAndSingleDashes(string project, string slug) =>
        Assert.Equal(slug, Rooms.SlugFor(project));

    /// <summary>The property rather than the examples: whatever a project is called, its file name is
    /// safe, stable under case, and never starts or ends with a dash.</summary>
    [Fact]
    public void EverySlugIsAFileNameAndCaseDoesNotMatter()
    {
        var rng = new Random(41);
        const string alphabet = "abcXYZ019 -_./\\:*?\"<>|é😀";
        for (var i = 0; i < 500; i++)
        {
            var name = new string([.. Enumerable.Range(0, rng.Next(0, 24)).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
            var slug = Rooms.SlugFor(name);
            Assert.Matches("^([a-z0-9]+(-[a-z0-9]+)*)?$", slug);
            Assert.Equal(slug, Rooms.SlugFor(name.ToUpperInvariant()));
        }
    }

    [Fact]
    public void ARoomRoundTripsThroughItsSlugFileInTheCourierHome()
    {
        var path = Rooms.Save(new Room
        {
            Project = "BookToCourse",
            Chats = { Admin = AdminChat, Observer = GroupChat },
            Footer = { Counters = "{done}/{total}", Live = "live", Pending = "later" },
            Voice = FixtureVoice("b2c"),
        }, _stateHome);

        Assert.Equal(Path.Combine(_stateHome, CourierHome.DirName, Rooms.DirName, "booktocourse.json"), path);
        var back = Assert.Single(Rooms.List(_stateHome).Rooms);
        Assert.Equal("BookToCourse", back.Project);
        Assert.Equal(GroupChat, back.Chats.Observer);
        Assert.Equal("{done}/{total}", back.Footer.Counters);
        Assert.Equal(path, back.Source);
        Assert.DoesNotContain(VoiceMarker, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenRoomFileIsNamedNotSkippedInSilence()
    {
        Rooms.Save(new Room { Project = "good", Chats = { Admin = AdminChat } }, _stateHome);
        File.WriteAllText(Path.Combine(Rooms.DirFor(_stateHome), "broken.json"), "{ not json");

        var (rooms, unreadable) = Rooms.List(_stateHome);
        Assert.Equal("good", Assert.Single(rooms).Project);
        Assert.Equal("broken.json", Assert.Single(unreadable));
    }

    [Fact]
    public void ARepoFindsTheRoomNamingItFirstThenTheRoomNamedLikeItsFolder()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_tmp, "code", "BookToCourse")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_tmp, "other", "BookToCourse")).FullName;

        Rooms.Save(new Room { Project = "BookToCourse", Chats = { Admin = AdminChat } }, _stateHome);
        Assert.Equal("BookToCourse", Rooms.Find(repo, _stateHome)?.Project);
        Assert.Equal("BookToCourse", Rooms.Find(repo.ToUpperInvariant(), _stateHome)?.Project);

        // A room that names its repo is that repo's alone: it beats the folder-name match, and a
        // different checkout with the same folder name does not get it.
        Rooms.Save(new Room { Project = "b2c-pinned", Repo = elsewhere, Chats = { Admin = AdminChat } }, _stateHome);
        Assert.Equal("b2c-pinned", Rooms.Find(elsewhere, _stateHome)?.Project);
        Assert.Equal("BookToCourse", Rooms.Find(repo, _stateHome)?.Project);

        Assert.Null(Rooms.Find(Path.Combine(_tmp, "code", "cv"), _stateHome));
    }

    // -- D6's fallback ---------------------------------------------------------------------------

    [Fact]
    public void AnUnmigratedRepoFallsBackToThePlanChatsThenTheCourierChats()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_tmp, "code", "unmigrated")).FullName;
        var plan = new TelegramConfig { Chats = [new TelegramChatEntry { ChatId = AdminChat }] };
        var courier = new CourierSettings { Chats = [new CourierChat(GroupChat, "observer"), new CourierChat("123", "admin")] };

        var room = Rooms.Resolve(repo, plan.ResolvedChats(), courier, _stateHome);
        Assert.NotNull(room);
        Assert.Equal("unmigrated", room.Project);
        Assert.Equal(AdminChat, room.Chats.Admin);            // the plan's, not the courier's 123
        Assert.Equal(GroupChat, room.Chats.Observer);         // the plan names none; the courier has one
        Assert.Equal("the plan's telegram.chats and the courier's chats", room.Source);

        // Two observer groups on the machine is a guess, and a guess is a card in the wrong room.
        courier.Chats.Add(new CourierChat("-100222", "observer"));
        Assert.Null(Rooms.Resolve(repo, plan.ResolvedChats(), courier, _stateHome)?.Chats.Observer);

        Assert.Null(Rooms.Resolve(repo, new TelegramConfig().ResolvedChats(), new CourierSettings(), _stateHome));
    }

    [Fact]
    public void ARoomFileBeatsEveryFallback()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_tmp, "code", "cv")).FullName;
        Rooms.Save(new Room { Project = "cv", Chats = { Admin = AdminChat } }, _stateHome);
        var plan = new TelegramConfig { Chats = [new TelegramChatEntry { ChatId = "555", Profile = "observer" }] };

        var room = Rooms.Resolve(repo, plan.ResolvedChats(), new CourierSettings(), _stateHome);
        Assert.NotNull(room);
        Assert.Null(room.Chats.Observer);
        Assert.EndsWith("cv.json", room.Source, StringComparison.Ordinal);
    }

    // -- the one-time import ---------------------------------------------------------------------

    [Fact]
    public void ImportTurnsEachOldConfigIntoARoomAndPointsAtTheVoice()
    {
        var b2cVoice = OldRoom("BookToCourse", $$"""
            { "chats": { "admin": "{{AdminChat}}", "observer": "{{GroupChat}}" },
              "report": { "counters": "{done}/{total} fixed", "footerLive": "live now", "footerPending": "with {stage}" } }
            """, withVoice: true);
        OldRoom("cv", $$"""{ "chats": { "admin": "{{AdminChat}}" } }""", withVoice: true);
        OldRoom("flat", $$"""{ "admin": "{{AdminChat}}" }""", withVoice: false);
        OldRoom("broken", "{ nope", withVoice: false);
        OldRoom("nochats", """{ "report": {} }""", withVoice: false);
        Directory.CreateDirectory(Path.Combine(_oldHome, "no-config-here"));

        var offers = RoomImport.Offers(_oldHome, _stateHome);
        Assert.Equal(["BookToCourse", "broken", "cv", "flat", "nochats"], offers.Select(o => o.Folder));
        Assert.NotNull(offers.Single(o => o.Folder == "broken").Problem);
        Assert.NotNull(offers.Single(o => o.Folder == "nochats").Problem);

        Assert.Equal(["BookToCourse", "cv", "flat"], RoomImport.Import(offers, _stateHome));

        var b2c = Rooms.Read(Rooms.PathFor("BookToCourse", _stateHome));
        Assert.NotNull(b2c);
        Assert.Equal(GroupChat, b2c.Chats.Observer);
        Assert.Equal("{done}/{total} fixed", b2c.Footer.Counters);
        Assert.Equal("live now", b2c.Footer.Live);
        Assert.Equal("with {stage}", b2c.Footer.Pending);
        Assert.Equal(b2cVoice, b2c.Voice);
        Assert.Null(Rooms.Read(Rooms.PathFor("flat", _stateHome))?.Voice);

        // Pointed at, never copied: the voice's words are in no room file.
        foreach (var file in Directory.EnumerateFiles(Rooms.DirFor(_stateHome)))
            Assert.DoesNotContain(VoiceMarker, File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondImportNeverOverwritesARoom()
    {
        OldRoom("cv", $$"""{ "chats": { "admin": "{{AdminChat}}" } }""", withVoice: false);
        RoomImport.Import(RoomImport.Offers(_oldHome, _stateHome), _stateHome);

        var edited = Rooms.Read(Rooms.PathFor("cv", _stateHome))!;
        edited.Chats.Observer = GroupChat;
        Rooms.Save(edited, _stateHome);

        var again = RoomImport.Offers(_oldHome, _stateHome);
        Assert.True(Assert.Single(again).AlreadyARoom);
        Assert.Empty(RoomImport.Import(again, _stateHome));
        Assert.Equal(GroupChat, Rooms.Read(Rooms.PathFor("cv", _stateHome))?.Chats.Observer);
    }

    // -- the verb --------------------------------------------------------------------------------

    [Fact]
    public void ListOffersTheImportThenPrintsTheRoomsByNameOnly()
    {
        OldRoom("BookToCourse", $$"""{ "chats": { "admin": "{{AdminChat}}", "observer": "{{GroupChat}}" } }""", withVoice: true);
        OldRoom("cv", $$"""{ "chats": { "admin": "{{AdminChat}}" } }""", withVoice: true);

        var (code, before) = Verb(new RoomCommand.Settings { Verb = "list", From = _oldHome });
        Assert.Equal(0, code);
        Assert.Contains("no rooms yet", before, StringComparison.Ordinal);
        Assert.Contains("not imported yet from", before, StringComparison.Ordinal);
        Assert.Contains("BookToCourse, cv", before, StringComparison.Ordinal);

        var (imported, importLog) = Verb(new RoomCommand.Settings { Verb = "import", From = _oldHome });
        Assert.Equal(0, imported);
        Assert.Contains("2 imported", importLog, StringComparison.Ordinal);

        var (_, after) = Verb(new RoomCommand.Settings { From = _oldHome });
        var names = after.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
        Assert.Equal(["BookToCourse", "cv"], names);
        Assert.DoesNotContain("not imported yet", after, StringComparison.Ordinal);
        AssertNoChatId(before + importLog + after);
    }

    [Fact]
    public void ShowSaysWhetherAChatIsSetAndNeverTheId()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_tmp, "code", "BookToCourse")).FullName;
        var voice = FixtureVoice("show");
        var (added, addLog) = Verb(new RoomCommand.Settings
        {
            Verb = "add", Repo = repo, Admin = AdminChat, Observer = GroupChat, Voice = voice, FooterLive = "live",
        });
        Assert.Equal(0, added);
        Assert.Contains("room BookToCourse saved", addLog, StringComparison.Ordinal);

        var (code, shown) = Verb(new RoomCommand.Settings { Verb = "show", Repo = repo });
        Assert.Equal(0, code);
        Assert.Contains("observer  set", shown, StringComparison.Ordinal);
        Assert.Contains("voice     " + voice, shown, StringComparison.Ordinal);
        Assert.Contains("live      live", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(VoiceMarker, shown, StringComparison.Ordinal);
        AssertNoChatId(addLog + shown);

        // add changes only what it is given.
        Verb(new RoomCommand.Settings { Verb = "add", Repo = repo, FooterPending = "later" });
        var room = Rooms.Read(Rooms.PathFor("BookToCourse", _stateHome))!;
        Assert.Equal(GroupChat, room.Chats.Observer);
        Assert.Equal("live", room.Footer.Live);
        Assert.Equal("later", room.Footer.Pending);
        Assert.Equal(voice, room.Voice);
    }

    [Theory]
    [InlineData("add", null, null, null, "needs --repo")]
    [InlineData("add", "REPO", "not-a-number", null, "numeric chat id")]
    [InlineData("add", "REPO", null, "missing.md", "is not there")]
    [InlineData("summon", null, null, null, "unknown room verb")]
    public void TheVerbRefusesByName(string verb, string? repo, string? observer, string? voice, string why)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tmp, "code", "refused")).FullName;
        var (code, output) = Verb(new RoomCommand.Settings
        {
            Verb = verb,
            Repo = repo is null ? null : dir,
            Observer = observer,
            Voice = voice is null ? null : Path.Combine(_tmp, voice),
        });
        Assert.Equal(1, code);
        Assert.Contains(why, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Rooms.DirFor(_stateHome)) && Directory.EnumerateFiles(Rooms.DirFor(_stateHome)).Any());
    }

    // -- helpers ---------------------------------------------------------------------------------

    private (int Code, string Output) Verb(RoomCommand.Settings settings)
    {
        using var writer = new StringWriter();
        var code = RoomCommand.Run(settings, writer, _stateHome);
        return (code, writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private string FixtureVoice(string name)
    {
        var path = Path.Combine(_tmp, "voices", name, "voice.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# voice\n" + VoiceMarker + "\n");
        return path;
    }

    private string OldRoom(string folder, string config, bool withVoice)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_oldHome, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, RoomImport.ConfigFileName), config);
        var voice = Path.Combine(dir, RoomImport.VoiceFileName);
        if (withVoice) File.WriteAllText(voice, "# voice\n" + VoiceMarker + "\n");
        return voice;
    }

    private static void AssertNoChatId(string output)
    {
        Assert.DoesNotContain(AdminChat, output, StringComparison.Ordinal);
        Assert.DoesNotContain(GroupChat.TrimStart('-'), output, StringComparison.Ordinal);
    }
}
