using System.Reflection;
using System.Text;

using Conductor.Core.Courier;

namespace Conductor.Core;

/// <summary>PK4.3 / D8 — a session writes in the room's voice because the voice reaches it as a battery.
///
/// <para>Two parts, in this order: <c>docs/rooms/character.md</c>, the engine of the voice every room
/// shares (embedded in this assembly, so a run in any repository carries it), and the room's own voice
/// file, read from wherever the room POINTS — never copied anywhere but the prompt it is for. On only
/// for a room with an observer chat: the words are for the card, and a room nobody observes has no
/// card.</para>
///
/// <para>Bounded in bytes like every other battery. The two parts get a max-min fair split of this
/// battery's cap, so a long voice cannot starve the character or the other way round, and the
/// battery group then applies the plan's <c>batteries.maxBytes</c> on top - by asking this battery to
/// recompose inside its share (<see cref="IFittingBattery"/>), so the group's cut is the same fair
/// split and never a tail cut that would lose the room's own voice. A part that is cut says so.</para></summary>
public sealed class RoomVoiceBattery : IFittingBattery
{
    /// <summary>The manifest name <c>docs/rooms/character.md</c> is embedded under.</summary>
    public const string CharacterResource = "Conductor.Core.Rooms.character.md";

    /// <summary>This battery's own ceiling, before the group's.</summary>
    public const int DefaultMaxBytes = 4096;

    private const string Cut = "\n… (cut to fit the battery)";

    private readonly int _maxBytes;
    private readonly string? _header;
    private readonly string _shared = "";
    private readonly string _voice = "";

    /// <param name="character">The shared character, or null for the embedded one (a test seam).</param>
    public RoomVoiceBattery(Room? room, int maxBytes = DefaultMaxBytes, string? character = null)
    {
        _maxBytes = maxBytes;
        if (room?.Chats.Observer is not { Length: > 0 }) return;

        _header = $"The room {room.Project} hears from the engine. When the verdict confirms a claim made with "
            + "`conductor task --done <id> --evidence <path> --tell \"<title> | <two to four sentences>\"`, the engine posts the card; "
            + "write those words in this voice. A finding mid-way goes out with `conductor say --to observer`. Never post a card yourself.";
        _shared = (character ?? EmbeddedCharacter()).Trim();
        var (voice, why) = ReadVoice(room.Voice);
        _voice = voice?.Trim() ?? why!;
    }

    public string Name => "room-voice";
    public string Section => _header is null ? "" : Compose(_maxBytes);
    public bool IsEmpty => _header is null;

    public string SectionWithin(int maxChars) => _header is null ? "" : Compose(Math.Min(maxChars, _maxBytes));

    /// <summary>The header whole, then the two parts in a max-min fair split of what is left of
    /// <paramref name="max"/>: a part that wants less than half keeps all of it and hands the rest over.</summary>
    private string Compose(int max)
    {
        var budget = max - Frame("", "").Length;
        if (budget < 4 * Cut.Length) return Clip(_header!, max);

        var shareShared = _shared.Length + _voice.Length <= budget ? _shared.Length : Math.Max(budget / 2, budget - _voice.Length);
        var shareVoice = budget - Math.Min(_shared.Length, shareShared);
        return Frame(Clip(_shared, shareShared), Clip(_voice, shareVoice));
    }

    private string Frame(string shared, string voice) => new StringBuilder(_header).AppendLine().AppendLine()
        .AppendLine("#### the character (every room)")
        .AppendLine(shared)
        .AppendLine()
        .AppendLine("#### the voice (this room)")
        .Append(voice)
        .ToString();

    /// <summary>The character every room shares, as shipped in this build.</summary>
    public static string EmbeddedCharacter()
    {
        using var stream = typeof(RoomVoiceBattery).Assembly.GetManifestResourceStream(CharacterResource);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The room's voice file, or null with the reason it is not there. Read, never copied.</summary>
    public static (string? Voice, string? Why) ReadVoice(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, "(this room names no voice file - the character alone decides)");
        try
        {
            return File.Exists(path)
                ? (File.ReadAllText(path, Encoding.UTF8), null)
                : (null, "(the room's voice file is missing - the character alone decides)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"(the room's voice file could not be read: {ex.Message})");
        }
    }

    /// <summary>Cut at a line boundary inside <paramref name="max"/> characters, and say it was cut.</summary>
    private static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max <= Cut.Length) return "";
        var room = max - Cut.Length;
        var end = text.LastIndexOf('\n', Math.Max(0, room - 1));
        return text[..(end > room / 2 ? end : room)].TrimEnd() + Cut;
    }
}
