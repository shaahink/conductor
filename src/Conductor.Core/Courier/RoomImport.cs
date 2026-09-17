using System.Text.Json;

namespace Conductor.Core.Courier;

/// <summary>PK4.1 / D6 — the one-time move from the skill's private home to conductor's rooms.
///
/// <para>Until this era a room was a folder under <c>~/.claude/telegram/&lt;repo folder&gt;/</c>: a
/// <c>config.json</c> with <c>chats</c> and <c>report</c>, and a <c>voice.md</c> beside it. The
/// import reads every such <c>config.json</c> and turns it into a room of the same name. The voice is
/// not read, let alone copied — the room is given the file's path, and the file stays where the owner
/// keeps it.</para>
///
/// <para>It never overwrites. A room that already exists is the owner's newer word, so a second import
/// is a no-op for it, which is what makes "one-time" safe to run twice.</para></summary>
public static class RoomImport
{
    /// <summary>The old private home's config file name.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>The old private home's voice file name.</summary>
    public const string VoiceFileName = "voice.md";

    /// <summary>What one folder of the old home offers: the room it would become, or why it cannot.</summary>
    public sealed record Offer(string Folder, Room? Room, string? Problem, bool AlreadyARoom);

    /// <summary>The old home, <c>~/.claude/telegram</c>.</summary>
    public static string DefaultSource() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "telegram");

    /// <summary>Every folder under <paramref name="source"/> holding a config file, as an offer. Reads
    /// chat ids and footer strings; opens no voice file.</summary>
    public static IReadOnlyList<Offer> Offers(string source, string? stateHomeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Directory.Exists(source)) return [];

        var existing = Rooms.List(stateHomeRoot).Rooms
            .Select(r => Rooms.SlugFor(r.Project)).ToHashSet(StringComparer.Ordinal);
        var offers = new List<Offer>();
        foreach (var folder in Directory.EnumerateDirectories(source).Order(StringComparer.Ordinal))
        {
            var config = Path.Combine(folder, ConfigFileName);
            if (!File.Exists(config)) continue;

            var name = Path.GetFileName(folder);
            var (room, problem) = Parse(name, config);
            offers.Add(new Offer(name, room, problem, existing.Contains(Rooms.SlugFor(name))));
        }
        return offers;
    }

    /// <summary>Imports every offer that parsed and is not already a room. Returns the names imported.</summary>
    public static IReadOnlyList<string> Import(IEnumerable<Offer> offers, string? stateHomeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(offers);
        var imported = new List<string>();
        foreach (var offer in offers)
        {
            if (offer.Room is null || offer.AlreadyARoom) continue;
            Rooms.Save(offer.Room, stateHomeRoot);
            imported.Add(offer.Room.Project);
        }
        return imported;
    }

    /// <summary>One old <c>config.json</c> as the room it becomes, or the reason it cannot. Public for
    /// MA0045's sake as much as a test's: a synchronous read compiles only on a public method.</summary>
    public static (Room? Room, string? Problem) Parse(string name, string configPath)
    {
        if (Rooms.SlugFor(name).Length == 0)
            return (null, "the folder name has no letter or digit to name a room by");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            var room = new Room { Project = name };

            // config.json keeps chats under a key; the older chats.json was the flat map itself.
            var chats = root.TryGetProperty("chats", out var c) && c.ValueKind == JsonValueKind.Object ? c : root;
            room.Chats.Admin = Text(chats, "admin");
            room.Chats.Observer = Text(chats, "observer");
            if (room.Chats.Admin is null && room.Chats.Observer is null)
                return (null, "it names neither an admin nor an observer chat");

            if (root.TryGetProperty("report", out var report) && report.ValueKind == JsonValueKind.Object)
            {
                room.Footer.Counters = Text(report, "counters");
                room.Footer.Live = Text(report, "footerLive");
                room.Footer.Pending = Text(report, "footerPending");
            }

            var voice = Path.Combine(Path.GetDirectoryName(configPath)!, VoiceFileName);
            if (File.Exists(voice)) room.Voice = Path.GetFullPath(voice);
            return (room, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{ConfigFileName} could not be read ({ex.Message})");
        }
    }

    private static string? Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            && value.ToString().Trim() is { Length: > 0 } text
            ? text
            : null;
}
