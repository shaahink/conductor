using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Inbox;

/// <summary>DV3.4 / findings §1.5 (2) and (3) — which project a chat is currently talking about.
///
/// <para><b>On disk, not in memory.</b> A selection the owner made last week has to survive an
/// engine restart, a reboot and — at DV4 — a courier that is a different process entirely. A sticky
/// selection held in a field is one that quietly reverts to "the run I happen to be" the first time
/// anything restarts, and the owner finds out by a note landing in the wrong project.</para>
///
/// <para>The key is the chat AND the forum topic. In a supergroup, one topic per project routes with
/// no command at all (§1.5 (3)); the topic's selection is its own and cannot leak into the chat-level
/// one, which is what stops a note in the "payesh" topic being filed against whatever the chat as a
/// whole was last set to.</para></summary>
public sealed class ChatRoutes
{
    /// <summary>The file, under the machine's state home — beside the catalogue it points into,
    /// rather than inside any one project. A selection is about a CHAT, and a chat outlives any of
    /// the projects it talks about.</summary>
    public const string FileName = "chat-routes.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public ChatRoutes(string stateHomeRoot)
    {
        ArgumentNullException.ThrowIfNull(stateHomeRoot);
        _path = Path.Combine(stateHomeRoot, FileName);
    }

    public string Path_ => _path;

    /// <summary>The selection in force for a message: the topic's if it has one, else the chat's.
    /// Null when nothing has been selected — which is not an error, it is the ordinary state of a
    /// machine running one project.</summary>
    public string? Current(string chatId, long? threadId)
    {
        var map = Read();
        if (threadId is { } t && map.TryGetValue(Key(chatId, t), out var topic)) return topic;
        return map.TryGetValue(Key(chatId, null), out var chat) ? chat : null;
    }

    /// <summary>Sets it, for this chat or this topic. Written whole and atomically: the map is small,
    /// and a torn selection file would silently route notes to the wrong project.</summary>
    public void Set(string chatId, long? threadId, string projectSlug)
    {
        var map = Read();
        map[Key(chatId, threadId)] = projectSlug;
        Write(map);
    }

    /// <summary>Forgets it. The chat falls back to the topic-less selection, or to the local run.</summary>
    public void Clear(string chatId, long? threadId)
    {
        var map = Read();
        if (map.Remove(Key(chatId, threadId))) Write(map);
    }

    /// <summary>Every selection this machine holds — for a status answer, and for the test that
    /// proves a topic's selection did not become the chat's.</summary>
    public IReadOnlyDictionary<string, string> All() => Read();

    private static string Key(string chatId, long? threadId) =>
        threadId is { } t
            ? chatId + ":" + t.ToString(CultureInfo.InvariantCulture)
            : chatId;

    /// <summary>Public for MA0045's public-member exemption, the same reason
    /// <see cref="InboxStore.AppendIndexLine"/> is: this store is synchronous by design because the
    /// routing decision above it is, not because the IO was overlooked.</summary>
    public Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Json)
                   ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable selection file falls back to no selection rather than to a guess: the
            // note then lands in the local run and the sender is told which project it went to.
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Public for the same MA0045 reason as <see cref="Read"/>.</summary>
    public void Write(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, JsonSerializer.Serialize(map, Json), new UTF8Encoding(false));
        try { File.Move(temp, _path, overwrite: true); }
        catch (IOException) { TryDelete(temp); }
    }

    /// <summary>Removes a TEMP file this class just wrote, and nothing else — the same named helper
    /// <see cref="InboxStore"/> uses, so the architecture test that proves prune is the only path to
    /// deleting a NOTE can tell the two apart by name.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
