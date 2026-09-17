using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;

namespace Conductor.Core.Courier;

/// <summary>PK4.1 / D6 — a room: the chats a project talks to, the words its card ends with, and where
/// its voice is kept.
///
/// <para><b>Rooms are conductor's, and they stay private.</b> One file per project under the courier
/// home, <c>rooms/&lt;project-slug&gt;.json</c> — machine-level like everything else the courier owns,
/// so nothing about a room ever enters a repository. The chat ids of a stakeholder group and the voice
/// file that describes the people in it are exactly what a shared checkout must not carry
/// (F-COUR-9).</para>
///
/// <para><b>The voice is pointed at, never copied.</b> <see cref="Voice"/> is a path to wherever the
/// owner keeps the file; this type reads the path, not the file. The part of the voice every room
/// shares is in the tree as <c>docs/rooms/character.md</c>.</para>
///
/// <para><b>A room authorises nothing</b> (F-OBS-4). It says where the engine speaks; who may give an
/// order is still the terminal's business.</para></summary>
public sealed class Room
{
    /// <summary>The project's name — the room's name, the only thing <c>room list</c> prints.</summary>
    public string Project { get; set; } = "";

    /// <summary>The checkout this room belongs to, when it is known. An imported room has none: the
    /// old private home was keyed by the repo's folder name, and so is the fallback match.</summary>
    public string? Repo { get; set; }

    public RoomChats Chats { get; set; } = new();

    /// <summary>The card's closing words, in the project's own language (PK4.2 renders them).</summary>
    public RoomFooter Footer { get; set; } = new();

    /// <summary>The path to the room's voice file. Pointed at, never read into a repository.</summary>
    public string? Voice { get; set; }

    /// <summary>Where this room came from: the room file, or the fallback it was assembled from. Never
    /// written.</summary>
    [JsonIgnore]
    public string Source { get; set; } = "";
}

/// <summary>The two chats a room has. Ids stay strings, as <see cref="CourierChat"/> keeps them.</summary>
public sealed class RoomChats
{
    /// <summary>The owner's chat: findings, parks, anything that wants a hand on it.</summary>
    public string? Admin { get; set; }

    /// <summary>The stakeholder group: cards, never orders.</summary>
    public string? Observer { get; set; }
}

/// <summary>The strings a card closes with. Null means the card's own default.</summary>
public sealed class RoomFooter
{
    /// <summary>The counts line, e.g. "done of total, live live".</summary>
    public string? Counters { get; set; }

    /// <summary>The footer when the checkpoint is live.</summary>
    public string? Live { get; set; }

    /// <summary>The footer when it lands with a later deploy.</summary>
    public string? Pending { get; set; }
}

/// <summary>PK4.1 — where rooms live and how a checkout finds its own.</summary>
public static class Rooms
{
    /// <summary>The rooms directory under the courier home.</summary>
    public const string DirName = "rooms";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
    public static string DirFor(string? stateHomeRoot = null) =>
        Path.Combine(CourierHome.DirFor(stateHomeRoot), DirName);

    /// <summary>The file name a project's room is kept under: the name lower-cased, anything that is
    /// not a letter or a digit folded to one dash. Empty when the name has nothing left.</summary>
    public static string SlugFor(string project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var chars = new List<char>(project.Length);
        foreach (var c in project.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) chars.Add(c);
            else if (chars.Count > 0 && chars[^1] != '-') chars.Add('-');
        }
        return new string([.. chars]).Trim('-');
    }

    public static string PathFor(string project, string? stateHomeRoot = null) =>
        Path.Combine(DirFor(stateHomeRoot), SlugFor(project) + ".json");

    /// <summary>Every room on this machine, by name, and the files that could not be read — named, not
    /// skipped in silence.</summary>
    public static (IReadOnlyList<Room> Rooms, IReadOnlyList<string> Unreadable) List(string? stateHomeRoot = null)
    {
        var dir = DirFor(stateHomeRoot);
        var rooms = new List<Room>();
        var unreadable = new List<string>();
        if (!Directory.Exists(dir)) return (rooms, unreadable);

        foreach (var path in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            if (Read(path) is { } room) rooms.Add(room);
            else unreadable.Add(Path.GetFileName(path));
        }
        rooms.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Project, b.Project));
        return (rooms, unreadable);
    }

    /// <summary>One room file, or null when it is missing, broken or names no project.</summary>
    public static Room? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var room = JsonSerializer.Deserialize<Room>(File.ReadAllText(path), Json);
            if (room is null || string.IsNullOrWhiteSpace(room.Project)) return null;
            room.Chats ??= new RoomChats();
            room.Footer ??= new RoomFooter();
            room.Source = path;
            return room;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the room atomically to its slug's file and returns the path.</summary>
    public static string Save(Room room, string? stateHomeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (SlugFor(room.Project).Length == 0)
            throw new ArgumentException($"a room needs a project name with a letter or a digit in it, not '{room.Project}'.", nameof(room));

        var path = PathFor(room.Project, stateHomeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, JsonSerializer.Serialize(room, Json));
        room.Source = path;
        return path;
    }

    /// <summary>The room for a checkout: the one that names this repo, else the one whose project is the
    /// repo's folder name — the rule the private home was keyed by, so an imported room is found
    /// without anyone typing its path. Null when neither matches.</summary>
    public static Room? Find(string repo, string? stateHomeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var rooms = List(stateHomeRoot).Rooms;
        var key = StateHome.NormalizeRepo(repo);
        var byRepo = rooms.FirstOrDefault(r => r.Repo is { Length: > 0 } own
            && string.Equals(StateHome.NormalizeRepo(own), key, StringComparison.Ordinal));
        if (byRepo is not null) return byRepo;

        var leaf = Path.GetFileName(key.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return rooms.FirstOrDefault(r => r.Repo is not { Length: > 0 }
            && string.Equals(r.Project, leaf, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>D6 — the room a run speaks to. The room file when there is one; otherwise one assembled
    /// from what an unmigrated repo already had, so it keeps working: the plan's
    /// <c>telegram.chats</c> first, then the machine's <c>courier chat</c> entries for a profile the plan
    /// does not name. A profile is taken from the courier only when exactly one chat carries it — a
    /// guess between two groups is a card in the wrong room. Null when nothing names a chat.
    ///
    /// <para>The plan's chats arrive as plain (id, profile) pairs — the caller passes
    /// <c>ResolvedChats()</c> — so a room stays a courier-home type and never names the messenger's
    /// config (KS11.1's seam: only the declared adapter files may).</para></summary>
    public static Room? Resolve(string repo, IEnumerable<(string ChatId, string? Profile)>? planChats,
        CourierSettings? courier, string? stateHomeRoot = null)
    {
        if (Find(repo, stateHomeRoot) is { } room) return room;

        var assembled = new Room
        {
            Project = Path.GetFileName(Path.GetFullPath(repo)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Repo = repo,
        };
        var sources = new List<string>();
        if (planChats is not null)
        {
            var chats = planChats.Select(c => (c.ChatId, c.Profile ?? ChatProfiles.AdminName)).ToList();
            assembled.Chats.Admin = OnlyChat(chats, ChatProfiles.AdminName);
            assembled.Chats.Observer = OnlyChat(chats, ChatProfiles.ObserverName);
            if (assembled.Chats.Admin is not null || assembled.Chats.Observer is not null) sources.Add("the plan's telegram.chats");
        }
        if (courier is not null && (assembled.Chats.Admin is null || assembled.Chats.Observer is null))
        {
            var chats = courier.Chats.Select(c => (c.ChatId, c.Profile ?? ChatProfiles.AdminName)).ToList();
            var fromCourier = false;
            if (assembled.Chats.Admin is null && OnlyChat(chats, ChatProfiles.AdminName) is { } admin)
            {
                assembled.Chats.Admin = admin;
                fromCourier = true;
            }
            if (assembled.Chats.Observer is null && OnlyChat(chats, ChatProfiles.ObserverName) is { } observer)
            {
                assembled.Chats.Observer = observer;
                fromCourier = true;
            }
            if (fromCourier) sources.Add("the courier's chats");
        }

        if (sources.Count == 0) return null;
        assembled.Source = string.Join(" and ", sources);
        return assembled;
    }

    private static string? OnlyChat(IEnumerable<(string ChatId, string Profile)> chats, string profile)
    {
        var ids = chats.Where(c => string.Equals(c.Profile, profile, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ChatId).Distinct(StringComparer.Ordinal).Take(2).ToList();
        return ids.Count == 1 ? ids[0] : null;
    }
}
