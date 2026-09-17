using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>PK4.3 / D8 — a session writes in the room's voice because the voice reaches it as a battery:
/// the character every room shares plus the room's own voice file, bounded in bytes, on only where a
/// card can go.
///
/// <para>Every voice here is a fixture written by this class; no test reads the owner's
/// <c>~/.claude/telegram</c>. The courier home is always a temp directory passed through the
/// <see cref="PromptBuilder.CourierStateHome"/> seam, so the machine's own courier chats never decide
/// a result here.</para></summary>
public sealed class PK4_3RoomVoiceTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string GroupChat = "-1009876543210";
    private const string VoiceMarker = "FIXTURE-VOICE pk43 the people in this room are imaginary";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _repo;
    private readonly string _voice;

    public PK4_3RoomVoiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-pk43-{Guid.NewGuid():N}");
        _stateHome = Directory.CreateDirectory(Path.Combine(_tmp, "state-home")).FullName;
        _repo = Directory.CreateDirectory(Path.Combine(_tmp, "code", "roomy")).FullName;
        _voice = Path.Combine(_tmp, "voice.md");
        File.WriteAllText(_voice, $"# voice\n\n{VoiceMarker}\nShort sentences. Say what changed for the reader.\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    private Room ObservedRoom(string? voice = null) => new()
    {
        Project = "roomy",
        Repo = _repo,
        Chats = { Admin = AdminChat, Observer = GroupChat },
        Voice = voice ?? _voice,
    };

    // -- the battery -----------------------------------------------------------------------------

    [Fact]
    public void ARoomNobodyObservesHasNoVoice()
    {
        Assert.True(new RoomVoiceBattery(null).IsEmpty);
        Assert.True(new RoomVoiceBattery(new Room { Project = "roomy", Chats = { Admin = AdminChat }, Voice = _voice }).IsEmpty);
        Assert.Equal("", new RoomVoiceBattery(null).Section);
    }

    [Fact]
    public void AnObservedRoomCarriesTheCharacterThenItsOwnVoiceAndTheTellVerb()
    {
        var battery = new RoomVoiceBattery(ObservedRoom());

        Assert.False(battery.IsEmpty);
        Assert.Equal("room-voice", battery.Name);
        var section = battery.Section;
        Assert.Contains("--tell \"<title> | <two to four sentences>\"", section, StringComparison.Ordinal);
        Assert.Contains("Never post a card yourself", section, StringComparison.Ordinal);
        Assert.DoesNotContain("report.ps1", section, StringComparison.Ordinal);
        Assert.Contains("# The character", section, StringComparison.Ordinal);   // the embedded one
        Assert.Contains(VoiceMarker, section, StringComparison.Ordinal);
        Assert.True(section.IndexOf("#### the character", StringComparison.Ordinal)
            < section.IndexOf("#### the voice", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCharacterShipsInTheAssemblyAsTheTreeHasIt()
    {
        var shipped = RoomVoiceBattery.EmbeddedCharacter();
        var tree = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "rooms", "character.md"));
        Assert.Equal(tree.ReplaceLineEndings(), shipped.ReplaceLineEndings());
    }

    [Theory]
    [InlineData(null, "names no voice file")]
    [InlineData("missing", "voice file is missing")]
    public void AVoiceThatIsNotThereIsSaidAndTheCharacterStillSpeaks(string? which, string said)
    {
        var path = which is null ? null : Path.Combine(_tmp, "no-such-voice.md");
        var room = ObservedRoom();
        room.Voice = path;

        var section = new RoomVoiceBattery(room).Section;
        Assert.Contains(said, section, StringComparison.Ordinal);
        Assert.Contains("# The character", section, StringComparison.Ordinal);
    }

    /// <summary>The property: whatever the sizes of the two parts and the cap, the battery stays inside
    /// its cap, a part that fits is whole, and a part that was cut says so - neither starves the other.</summary>
    [Fact]
    public void WhateverTheSizesTheBatteryStaysInsideItsCapAndACutPartSaysSo()
    {
        var rng = new Random(43);
        for (var i = 0; i < 300; i++)
        {
            var cap = rng.Next(1024, 8193);
            var character = "# fixture character\n" + Lines('c', rng.Next(0, 12000), rng);
            var voiceText = VoiceMarker + "\n" + Lines('v', rng.Next(0, 12000), rng);
            File.WriteAllText(_voice, voiceText);

            var section = new RoomVoiceBattery(ObservedRoom(), cap, character).Section;

            Assert.True(section.Length <= cap, $"case {i}: {section.Length} > cap {cap}");
            Assert.Contains("# fixture character", section, StringComparison.Ordinal);
            Assert.Contains(VoiceMarker, section, StringComparison.Ordinal);
            var cuts = CountOf(section, "(cut to fit the battery)");
            var whole = (section.Contains(character.Trim(), StringComparison.Ordinal) ? 1 : 0)
                + (section.Contains(voiceText.Trim(), StringComparison.Ordinal) ? 1 : 0);
            Assert.Equal(2, cuts + whole);
        }
    }

    // -- the wiring ------------------------------------------------------------------------------

    [Fact]
    public void APlanWhoseRoomHasAnObserverCarriesTheVoiceInsideTheBatteryCap()
    {
        Rooms.Save(ObservedRoom(), _stateHome);
        var plan = Plan(chats: [new TelegramChatEntry { ChatId = AdminChat }]);
        plan.Batteries = new BatteriesConfig { MaxBytes = 3000, Lessons = false, RecentFailure = false };

        var section = new PromptBuilder(plan) { CourierStateHome = _stateHome }.BatterySection(null, null, null, null, null);

        Assert.Contains("### room-voice", section, StringComparison.Ordinal);
        Assert.Contains(VoiceMarker, section, StringComparison.Ordinal);
        Assert.True(section.Length <= 3000, $"{section.Length} > 3000");
    }

    [Fact]
    public void APlanThatNamesNoChatsIsNeverVoicedEvenWithARoomOnTheMachine()
    {
        Rooms.Save(ObservedRoom(), _stateHome);
        var section = new PromptBuilder(Plan(chats: null)) { CourierStateHome = _stateHome }.BatterySection(null, null, null, null, null);
        Assert.DoesNotContain("room-voice", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoomWithNoObserverAnywhereLeavesThePromptAsItWas()
    {
        Rooms.Save(new Room { Project = "roomy", Repo = _repo, Chats = { Admin = AdminChat }, Voice = _voice }, _stateHome);
        var plan = Plan(chats: [new TelegramChatEntry { ChatId = AdminChat }]);

        var voiced = new PromptBuilder(plan) { CourierStateHome = _stateHome }.BatterySection(null, null, null, null, null);
        Assert.DoesNotContain("room-voice", voiced, StringComparison.Ordinal);
        Assert.DoesNotContain(VoiceMarker, voiced, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmigratedRepoIsVoicedByThePlansObserverChatWithTheCharacterAlone()
    {
        var plan = Plan(chats: [new TelegramChatEntry { ChatId = AdminChat }, new TelegramChatEntry { ChatId = GroupChat, Profile = "observer" }]);
        var section = new PromptBuilder(plan) { CourierStateHome = _stateHome }.BatterySection(null, null, null, null, null);

        Assert.Contains("### room-voice", section, StringComparison.Ordinal);
        Assert.Contains("names no voice file", section, StringComparison.Ordinal);
        Assert.DoesNotContain(GroupChat, section, StringComparison.Ordinal);
    }

    private PlanConfig Plan(List<TelegramChatEntry>? chats) => new()
    {
        Repo = _repo,
        Telegram = chats is null ? null : new TelegramConfig { Chats = chats },
    };

    private static string Lines(char c, int length, Random rng)
    {
        var sb = new System.Text.StringBuilder(length);
        while (sb.Length < length) sb.Append(c, rng.Next(1, 90)).Append('\n');
        return sb.ToString();
    }

    private static int CountOf(string text, string what)
    {
        var n = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
